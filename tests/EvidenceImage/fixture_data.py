"""Generate synthetic records, disk partition tables and damaged copies in a test directory."""

import binascii
import random
import shutil
import struct
import sys
import uuid
from pathlib import Path

PAGE = 4096
SECTOR = 512


def metadata(folder, label, file_number):
    mft = (folder / "mft-before.bin").read_bytes()
    sequence = struct.unpack_from("<H", mft, file_number * 1024 + 16)[0]
    root_sequence = struct.unpack_from("<H", mft, 5 * 1024 + 16)[0]
    journal = bytearray(65536)
    for name, reason in [(label + "-old.txt", 0x1000), (label + "-renamed.txt", 0x2000),
                         (label + "-renamed.txt", 0x80000000)]:
        text = name.encode("utf-16le")
        length = (60 + len(text) + 7) & ~7
        offset = len(journal)
        header = struct.pack("<IHHQQqqIIIIHH", length, 2, 0, (sequence << 48) | file_number,
                             (root_sequence << 48) | 5, offset, 133887456000000000 + offset,
                             reason, 0, 1, 0x20, len(text), 60)
        journal.extend((header + text).ljust(length, b"\0"))
    (folder / "journal.bin").write_bytes(journal)
    (folder / "log.bin").write_bytes(transaction_log())


def transaction_log():
    """A 2 MiB NTFS 2.0 log with two RSTR copies and one protected RCRD page."""
    log = bytearray(2 << 20)
    number = 34

    def lsn(offset):
        return (1 << 19) | ((number * PAGE + offset) // 8)

    def protect(page, sequence):
        struct.pack_into("<HH", page, 4, 40, 9)
        struct.pack_into("<H", page, 40, sequence)
        for i in range(1, 9):
            tail = i * SECTOR - 2
            page[40 + 2 * i:42 + 2 * i] = page[tail:tail + 2]
            struct.pack_into("<H", page, tail, sequence)

    for i in range(2):
        page = bytearray(PAGE)
        page[:4] = b"RSTR"
        struct.pack_into("<IIHhh", page, 16, PAGE, PAGE, 64, 0, 2)
        struct.pack_into("<QHHHHIHHQIHHI", page, 64, lsn(160), 1, 65535, 0, 0, 45, 208,
                         48, len(log), 48, 48, 64, 3)
        client = 112
        struct.pack_into("<QQHHH", page, client, lsn(64), 0, 65535, 65535, 1)
        name = "NTFS".encode("utf-16le")
        struct.pack_into("<I", page, client + 28, len(name))
        page[client + 32:client + 32 + len(name)] = name
        protect(page, 0x8912 + i)
        log[i * PAGE:(i + 1) * PAGE] = page

    page = bytearray(PAGE)
    page[:4] = b"RCRD"
    struct.pack_into("<Q", page, 8, lsn(160))
    struct.pack_into("<H", page, 24, 256)
    for offset, previous, operation in [(64, 0, 11), (160, lsn(64), 26)]:
        struct.pack_into("<QQQIHHiI", page, offset, lsn(offset), previous, 0, 48, 1, 0, 1, 64)
        struct.pack_into("<HHHHHH", page, offset + 48, operation, 0, 32, 0, 32, 0)
    protect(page, 0x7211)
    log[number * PAGE:(number + 1) * PAGE] = page
    return log


def disks(base):
    alpha, beta = [(base / label / "volume.raw").read_bytes() for label in ("Alpha", "Beta")]
    sectors = len(alpha) // SECTOR

    def partition(kind, start, count):
        return struct.pack("<B3sB3sII", 0, b"\0\x02\0", kind, b"\xfe\xff\xff", start, count)

    mbr_start = 4096
    mbr = bytearray((mbr_start + sectors + 32) * SECTOR)
    mbr[446:462] = partition(7, mbr_start, sectors)
    mbr[510:512] = b"\x55\xaa"
    mbr[mbr_start * SECTOR:(mbr_start + sectors) * SECTOR] = alpha
    (base / "case-mbr.raw").write_bytes(mbr)
    (base / "truncated.raw").write_bytes(mbr[:2560000])
    for i, offset in enumerate(range(0, len(mbr), 12 << 20), start=1):
        chunk = mbr[offset:offset + (12 << 20)]
        (base / "split" / f"case.{i:03}").write_bytes(chunk)
        if i != 2:
            (base / "splitmissing" / f"case.{i:03}").write_bytes(chunk)

    first = 2048
    second = first + sectors + 2048
    total = second + sectors + 2048
    disk = bytearray(total * SECTOR)
    disk[446:462] = partition(0xee, 1, total - 1)
    disk[510:512] = b"\x55\xaa"
    table = bytearray(128 * 128)
    for i, (label, start, content) in enumerate([("Alpha", first, alpha), ("Beta", second, beta)]):
        struct.pack_into("<16s16sQQQ72s", table, i * 128,
                         uuid.UUID("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7").bytes_le,
                         uuid.uuid5(uuid.NAMESPACE_DNS, label).bytes_le, start, start + sectors - 1,
                         0, label.encode("utf-16le"))
        disk[start * SECTOR:(start + sectors) * SECTOR] = content

    def header(at, backup, location):
        block = bytearray(struct.pack("<8sIIIIQQQQ16sQIII", b"EFI PART", 65536, 92, 0, 0, at,
                          backup, 34, total - 34, uuid.UUID("d77a416f-b211-4dc3-a87f-a1374ac64227").bytes_le,
                          location, 128, 128, binascii.crc32(table)))
        struct.pack_into("<I", block, 16, binascii.crc32(block))
        return block.ljust(SECTOR, b"\0")

    disk[SECTOR:2 * SECTOR] = header(1, total - 1, 2)
    disk[2 * SECTOR:2 * SECTOR + len(table)] = table
    disk[(total - 33) * SECTOR:(total - 1) * SECTOR] = table
    disk[(total - 1) * SECTOR:] = header(total - 1, 1, total - 33)
    (base / "case-gpt.raw").write_bytes(disk)
    disk[SECTOR + 56] ^= 1
    (base / "case-corrupt-gpt.raw").write_bytes(disk)


if __name__ == "__main__":
    command, folder = sys.argv[1], Path(sys.argv[2])
    if command == "metadata":
        metadata(folder, sys.argv[3], int(sys.argv[4]))
    elif command == "padding":
        # Keeps the compressed EWF set large enough to need three test segments.
        (folder / "ordinary.bin").write_bytes(random.Random(480123).randbytes(2500000))
    elif command == "disks":
        disks(folder)
    else:
        raise SystemExit(f"Unrecognized fixture operation: {command}")

