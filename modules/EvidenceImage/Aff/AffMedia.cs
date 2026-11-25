using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace EvidenceImage;

// Read-only AFF1 data pages. Offsets in this stream refer to the logical disk.
internal sealed class AffMedia : Stream
{
    private readonly record struct Page(long Offset, int Stored, uint Flags);
    private const int MaxPageBytes = 64 * 1024 * 1024;
    private readonly FileStream file;
    private readonly Dictionary<long, Page> pages = new();
    private int pageSize;
    private long size, position, cachedIndex = -1;
    private byte[] cached = [];

    private AffMedia(FileStream file) => this.file = file;

    internal static Stream Open(FileStream file, CancellationToken cancellation = default)
    {
        var stream = new AffMedia(file);
        try
        {
            stream.Index(cancellation);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32BigEndian(data);
    private void Index(CancellationToken cancellation)
    {
        Span<byte> head = stackalloc byte[16];
        Span<byte> tail = stackalloc byte[8];
        file.ReadExactly(tail);
        if (!tail.SequenceEqual("AFF10\r\n\0"u8))
            throw new InvalidDataException("Invalid AFF1 header.");
        while (file.Position < file.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            file.ReadExactly(head);
            uint nameSize = U32(head[4..]), stored = U32(head[8..]), flags = U32(head[12..]);
            if (!head[..4].SequenceEqual("AFF\0"u8) || nameSize > 1024 ||
                (long)nameSize + stored + 8 > file.Length - file.Position)
                throw new InvalidDataException("Invalid AFF1 segment bounds.");
            var nameBytes = new byte[nameSize];
            file.ReadExactly(nameBytes);
            string name = Encoding.ASCII.GetString(nameBytes);
            long data = file.Position;
            if (name.EndsWith("/aes256", StringComparison.Ordinal))
                throw new NotSupportedException("Encrypted AFF segments are unsupported.");
            if (name is "pagesize" or "segsize")
            {
                if (flags == 0 || flags > MaxPageBytes || (pageSize != 0 && pageSize != flags))
                    throw new InvalidDataException("Invalid or unsupported AFF page size.");
                pageSize = (int)flags;
            }
            else if (name == "imagesize")
            {
                if (stored != 8)
                    throw new InvalidDataException("Invalid AFF image size.");
                file.ReadExactly(tail);
                long length = checked((long)(((ulong)U32(tail[4..]) << 32) | U32(tail)));
                if (size != 0 && size != length)
                    throw new InvalidDataException("Conflicting AFF image sizes.");
                size = length;
            }
            else if (PageNumber(name) is long number)
            {
                if (stored > MaxPageBytes + 65536 || pages.Count >= 4_000_000 ||
                    !pages.TryAdd(number, new Page(data, (int)stored, flags)))
                    throw new InvalidDataException("Invalid, duplicate or oversized AFF page.");
                if ((flags & ~0x33u) != 0 || ((flags & 1) != 0 && (flags & 0x30) is not (0 or 0x30)))
                    throw new NotSupportedException($"Unsupported AFF page encoding 0x{flags:X}.");
            }
            file.Position = data + stored;
            file.ReadExactly(tail);
            if (!tail[..4].SequenceEqual("ATT\0"u8) || U32(tail[4..]) != 24L + nameSize + stored)
                throw new InvalidDataException("Invalid AFF segment trailer.");
        }
        if (pageSize == 0 || size <= 0 || pages.Count != 1 + ((size - 1) / pageSize) ||
            pages.Keys.Any(p => p > (size - 1) / pageSize))
            throw new InvalidDataException("Incomplete AFF image or missing size metadata.");
    }

    private static long? PageNumber(string name)
    {
        int prefix = name.StartsWith("page", StringComparison.Ordinal) ? 4 :
                     name.StartsWith("seg", StringComparison.Ordinal) ? 3 : 0;
        return prefix != 0 && long.TryParse(name.AsSpan(prefix), NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : null;
    }

    private void LoadPage(long number)
    {
        if (number == cachedIndex)
            return;
        cachedIndex = -1;
        var page = pages[number];
        int expected = (int)Math.Min(pageSize, size - (number * pageSize));
        if (cached.Length != expected)
            cached = new byte[expected];
        file.Position = page.Offset;
        if ((page.Flags & 1) == 0)
        {
            if (page.Stored != expected)
                throw new InvalidDataException("Invalid AFF plain page length.");
            file.ReadExactly(cached);
        }
        else if ((page.Flags & 0x30) == 0x30)
        {
            Span<byte> count = stackalloc byte[4];
            if (page.Stored != 4)
                throw new InvalidDataException("Invalid AFF zero-page segment.");
            file.ReadExactly(count);
            if (U32(count) != expected)
                throw new InvalidDataException("Invalid AFF zero-page length.");
            Array.Clear(cached);
        }
        else
        {
            byte[] compressed = new byte[page.Stored];
            file.ReadExactly(compressed);
            using var input = new MemoryStream(compressed, false);
            using var inflater = new ZLibStream(input, CompressionMode.Decompress);
            inflater.ReadExactly(cached);
            if (inflater.ReadByte() != -1 || compressed.Length < 6 || U32(compressed.AsSpan(^4)) != Adler32(cached))
                throw new InvalidDataException("Invalid AFF zlib page length or checksum.");
        }
        cachedIndex = number;
    }

    private static uint Adler32(ReadOnlySpan<byte> bytes)
    {
        uint a = 1, b = 0;
        while (!bytes.IsEmpty)
        {
            int n = Math.Min(bytes.Length, 5552);
            foreach (byte value in bytes[..n])
            {
                a += value;
                b += a;
            }
            a %= 65521;
            b %= 65521;
            bytes = bytes[n..];
        }
        return b << 16 | a;
    }

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (!buffer.IsEmpty && position < size)
        {
            LoadPage(position / pageSize);
            int at = (int)(position % pageSize);
            int count = Math.Min(buffer.Length, cached.Length - at);
            cached.AsSpan(at, count).CopyTo(buffer);
            buffer = buffer[count..];
            position += count;
            total += count;
        }
        return total;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override long Seek(long offset, SeekOrigin origin)
    {
        long start = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => position,
            SeekOrigin.End => size,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return Position = checked(start + offset);
    }
    public override long Length => size;
    public override long Position
    {
        get => position; set => position = value >= 0 ? value : throw new IOException("Negative image offset.");
    }
    public override bool CanRead => file.CanRead;
    public override bool CanSeek => file.CanSeek;
    public override bool CanWrite => false;
    public override void Flush()
    {
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
