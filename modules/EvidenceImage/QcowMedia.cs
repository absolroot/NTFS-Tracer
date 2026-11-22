using System.Buffers.Binary;
using System.IO.Compression;

namespace EvidenceImage;

// Read-only QCOW1 and standard-L2 QCOW2/3 active image. Never open paths from backing-file metadata.
internal sealed class QcowMedia : Stream
{
    private readonly Stream source;
    private readonly int version, clusterBits, clusterSize, l2Bits;
    private readonly byte[] firstLevel;
    private byte[]? secondLevel, decoded;
    private long currentTable = -1, currentCluster = -1;
    private static uint U32(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32BigEndian(s);
    private static ulong U64(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt64BigEndian(s);
    private const ulong DiskOffset = 0x00ff_ffff_ffff_fe00;

    public QcowMedia(Stream source)
    {
        this.source = source;
        var h = DiskLayout.ReadAt(source, 0, 48);
        version = checked((int)U32(h.AsSpan(4)));
        if (version is < 1 or > 3)
            throw new NotSupportedException("Unsupported QCOW version.");
        if (U64(h.AsSpan(8)) != 0 || U32(h.AsSpan(16)) != 0)
            throw new NotSupportedException("This QCOW image requires a backing file.");
        Length = checked((long)U64(h.AsSpan(24)));
        clusterBits = version == 1 ? h[32] : checked((int)U32(h.AsSpan(20)));
        l2Bits = version == 1 ? h[33] : clusterBits - 3;
        if (Length <= 0 || clusterBits < 9 || clusterBits > (version == 1 ? 16 : 21) || l2Bits < 6 || l2Bits > (version == 1 ? 13 : 18))
            throw new InvalidDataException("Invalid QCOW geometry.");
        clusterSize = 1 << clusterBits;
        if (U32(h.AsSpan(version == 1 ? 36 : 32)) != 0)
            throw new NotSupportedException("Encrypted QCOW images cannot be read.");
        if (version == 3 && U64(DiskLayout.ReadAt(source, 72, 8)) != 0)
            throw new NotSupportedException("Unsupported QCOW3 feature flags (e.g. external data, extended L2, dirty or corrupt image).");
        long requiredEntries = ((Length - 1) >> (clusterBits + l2Bits)) + 1;
        long entries = version == 1 ? requiredEntries : U32(h.AsSpan(36));
        if (entries < requiredEntries || entries > 4 * 1024 * 1024)
            throw new InvalidDataException("Invalid QCOW L1 table size.");
        long l1 = checked((long)U64(h.AsSpan(40)));
        if (l1 < 48 || (l1 & 7) != 0)
            throw new InvalidDataException("Invalid QCOW L1 table position.");
        firstLevel = DiskLayout.ReadAt(source, l1, checked((int)entries * 8));
    }

    public override long Length
    {
        get;
    }
    public override long Position
    {
        get; set;
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> output)
    {
        if (Position < 0)
            throw new IOException("Negative QCOW position.");
        int total = 0;
        while (!output.IsEmpty && Position < Length)
        {
            long index = Position >> clusterBits;
            int offset = (int)(Position & (clusterSize - 1));
            int amount = (int)Math.Min(Math.Min(output.Length, clusterSize - offset), Length - Position);
            ulong pointer = Pointer(index);
            bool compressed = (pointer & (1UL << (version == 1 ? 63 : 62))) != 0;
            var target = output[..amount];
            if (compressed)
            {
                if (currentCluster != index)
                {
                    int shift = version == 1 ? 63 - clusterBits : 62 - (clusterBits - 8);
                    long start = (long)(pointer & ((1UL << shift) - 1));
                    int compressedSize = version == 1 ? (int)((pointer >> shift) & (uint)(clusterSize - 1))
                        : (((int)((pointer >> shift) & (uint)((1 << (clusterBits - 8)) - 1)) + 1) * 512) - (int)(start % 512);
                    if (compressedSize <= 0)
                        throw new InvalidDataException("Invalid QCOW compressed extent.");
                    byte[] input = DiskLayout.ReadAt(source, start, compressedSize);
                    using var inflater = new DeflateStream(new MemoryStream(input), CompressionMode.Decompress);
                    decoded ??= new byte[clusterSize];
                    inflater.ReadExactly(decoded);
                    if (inflater.ReadByte() != -1)
                        throw new InvalidDataException("Oversized decompressed QCOW cluster.");
                    currentCluster = index;
                }
                decoded!.AsSpan(offset, amount).CopyTo(target);
            }
            else
            {
                if (version != 1 && (pointer & ~(DiskOffset | (1UL << 63) | (version == 3 ? 1UL : 0UL))) != 0)
                    throw new InvalidDataException("Invalid QCOW data mapping.");
                long start = checked((long)(version == 1 ? pointer : pointer & DiskOffset));
                if (start == 0 || version == 3 && (pointer & 1) != 0)
                    target.Clear();
                else
                {
                    if (start % clusterSize != 0 || start > source.Length - offset - amount)
                        throw new InvalidDataException("QCOW cluster outside the image.");
                    source.Position = start + offset;
                    source.ReadExactly(target);
                }
            }
            total += amount;
            Position += amount;
            output = output[amount..];
        }
        return total;
    }

    private ulong Pointer(long index)
    {
        ulong entry = U64(firstLevel.AsSpan(checked((int)(index >> l2Bits) * 8)));
        if (entry == 0)
            return 0;
        if (version != 1 && (entry & ~(DiskOffset | (1UL << 63))) != 0)
            throw new InvalidDataException("Invalid QCOW L1 entry.");
        long at = checked((long)(version == 1 ? entry : entry & DiskOffset));
        if (at == 0 || at % clusterSize != 0)
            throw new InvalidDataException("Invalid QCOW L2 position.");
        if (currentTable != at)
        {
            secondLevel = DiskLayout.ReadAt(source, at, 8 << l2Bits);
            currentTable = at;
        }
        int slot = (int)(index & ((1L << l2Bits) - 1));
        return U64(secondLevel!.AsSpan(slot * 8));
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long pos = checked(origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        });
        if (pos < 0)
            throw new IOException("Negative QCOW position.");
        return Position = pos;
    }
    public override void Flush()
    {
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
