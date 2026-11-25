using System.Buffers.Binary;

namespace EvidenceImage;

internal sealed record VolumeRange(long Offset, long Length, string Scheme);

internal static class DiskLayout
{
    private static uint U32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);
    private static long U64(ReadOnlySpan<byte> b) => checked((long)BinaryPrimitives.ReadUInt64LittleEndian(b));
    internal static byte[] ReadAt(Stream disk, long pos, int count)
    {
        if (pos < 0 || pos > disk.Length - count)
            throw new InvalidDataException("Disk structure points outside the image.");
        disk.Position = pos;
        var b = new byte[count];
        disk.ReadExactly(b);
        return b;
    }

    internal static List<VolumeRange> Read(Stream disk, List<string> warnings, CancellationToken ct)
    {
        var results = new List<VolumeRange>();
        if (disk.Length < 512)
            return results;
        var boot = ReadAt(disk, 0, 512);
        if (IsNtfs(boot) || IsBitLocker(boot))
            return [new(0, disk.Length, "Volume")];
        foreach (int sector in new[] { 512, 4096 })
        {
            ct.ThrowIfCancellationRequested();
            if (disk.Length >= sector + 512 && ReadAt(disk, sector, 8).AsSpan().SequenceEqual("EFI PART"u8))
                return ReadGpt(disk, sector, ct);
        }
        if (boot[510] != 0x55 || boot[511] != 0xaa)
            return results;
        bool protective = false;
        foreach (var (_, type, _, _) in MbrEntries(boot))
            if (type == 0xee)
                protective = true;
        if (protective)
            throw new InvalidDataException("The protective MBR is present but the primary GPT header could not be read.");

        // An MBR does not encode logical sector size. Accept a candidate only when its boot sector identifies the filesystem.
        foreach (int sector in new[] { 512, 4096 })
        {
            var extended = new HashSet<long>();
            void Visit(long sectorLba, long baseLba, long limit, byte[] table)
            {
                foreach (var (_, type, start, sectors) in MbrEntries(table))
                {
                    ct.ThrowIfCancellationRequested();
                    bool link = type is 0x05 or 0x0f or 0x85;
                    long lba = checked((link ? baseLba : sectorLba) + start);
                    long offset = checked(lba * sector), length = checked((long)sectors * sector);
                    if (lba >= limit || offset > disk.Length - 512)
                        continue;
                    if (link)
                    {
                        if (extended.Count >= 128 || !extended.Add(lba))
                        {
                            warnings.Add("Cyclic or excessive extended partition chain.");
                            continue;
                        }
                        var next = ReadAt(disk, offset, 512);
                        if (next[510] == 0x55 && next[511] == 0xaa)
                            Visit(lba, baseLba == 0 ? lba : baseLba, Math.Min(limit, checked(lba + sectors)), next);
                    }
                    else if (length >= 512 && lba + sectors <= limit && offset <= disk.Length - length)
                    {
                        var candidate = ReadAt(disk, offset, 512);
                        if ((IsNtfs(candidate) || IsBitLocker(candidate)) && results.All(x => x.Offset != offset))
                            results.Add(new(offset, length, "MBR"));
                    }
                }
            }
            Visit(0, 0, disk.Length / sector, boot);
        }
        return results;
    }

    private static IEnumerable<(int Index, byte Type, uint Start, uint Sectors)> MbrEntries(byte[] sector)
    {
        for (int i = 0; i < 4; i++)
        {
            int at = 446 + (i * 16);
            byte type = sector[at + 4];
            uint start = U32(sector.AsSpan(at + 8)), size = U32(sector.AsSpan(at + 12));
            if (type != 0 && start != 0 && size != 0)
                yield return (i, type, start, size);
        }
    }

    private static List<VolumeRange> ReadGpt(Stream disk, int sector, CancellationToken ct)
    {
        var header = ReadAt(disk, sector, sector);
        uint size = U32(header.AsSpan(12)), stored = U32(header.AsSpan(16));
        if (size < 92 || size > sector || U64(header.AsSpan(24)) != 1)
            throw new InvalidDataException("Invalid primary GPT header.");
        header.AsSpan(16, 4).Clear();
        if (Crc(header.AsSpan(0, (int)size)) != stored)
            throw new InvalidDataException("GPT header CRC32 mismatch.");
        long start = checked(U64(header.AsSpan(72)) * sector);
        uint count = U32(header.AsSpan(80)), stride = U32(header.AsSpan(84));
        if (stride < 128 || stride > 4096 || count == 0 || (long)count * stride > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid GPT partition array size.");
        var table = ReadAt(disk, start, checked((int)(count * stride)));
        if (Crc(table) != U32(header.AsSpan(88)))
            throw new InvalidDataException("GPT partition array CRC32 mismatch.");
        var volumes = new List<VolumeRange>();
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = table.AsSpan(checked((int)(i * stride)));
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0)
                continue;
            long first = U64(entry[32..]), last = U64(entry[40..]);
            if (first < U64(header.AsSpan(40)) || last > U64(header.AsSpan(48)) || first > last || last >= disk.Length / sector)
                throw new InvalidDataException("GPT partition bounds are invalid.");
            volumes.Add(new(checked(first * sector), checked((last - first + 1) * sector), "GPT"));
        }
        return volumes;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte b in bytes)
        {
            value ^= b;
            for (int i = 0; i < 8; i++)
                value = (value >> 1) ^ (0xedb88320u & (uint)-(int)(value & 1));
        }
        return ~value;
    }
    internal static bool IsNtfs(ReadOnlySpan<byte> boot) => boot.Slice(3, 8).SequenceEqual("NTFS    "u8);
    internal static bool IsBitLocker(ReadOnlySpan<byte> boot) => boot.Slice(3, 8).SequenceEqual("-FVE-FS-"u8);
}
