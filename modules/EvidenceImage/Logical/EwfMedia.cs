using System.Runtime.InteropServices;
using System.Buffers.Binary;
using static EvidenceImage.EwfNative;

namespace EvidenceImage;

internal sealed class EwfSource : IDisposable
{
    private IntPtr handle;
    private readonly List<FileStream> leases = [];
    internal IntPtr Handle => handle != IntPtr.Zero ? handle : throw new ObjectDisposedException(nameof(EwfSource));
    internal IReadOnlyList<FileStream> SegmentFiles => leases;
    internal bool IsLx01
    {
        get;
    }
    internal string Generation => SourceIdentity.Generation(leases);
    internal EwfSource(string file)
    {
        string path = Path.GetFullPath(file);
        IntPtr names = IntPtr.Zero;
        int count = 0;
        try
        {
            Check(libewf_glob_wide(path, (nuint)path.Length, 0, out names, out count, out var error), error);
            if (count is < 1 or > 100_000)
                throw new InvalidDataException("Invalid EWF segment count.");
            byte[]? first = null;
            var header = new byte[32];
            for (int i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size)) ?? throw new InvalidDataException("Missing segment name.");
                if (i == 0 && !Path.GetFullPath(name).Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Select the first EWF segment.");
                leases.Add(new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.Read));
                leases[^1].ReadExactly(header);
                first ??= (byte[])header.Clone();
                bool v2 = header.AsSpan(0, 4).SequenceEqual("EVF2"u8) || header.AsSpan(0, 4).SequenceEqual("LEF2"u8);
                uint number = v2 ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) : BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(9));
                if (number != i + 1 || !header.AsSpan(0, 8).SequenceEqual(first.AsSpan(0, 8)) ||
                    (v2 && !header.AsSpan(16, 16).SequenceEqual(first.AsSpan(16, 16))))
                    throw new InvalidDataException("EWF segment headers do not form one complete, ordered set. Select the first segment.");
            }
            IsLx01 = first.AsSpan().StartsWith("LEF2"u8);
            Check(libewf_handle_initialize(out handle, out error), error);
            Check(libewf_handle_open_wide(handle, names, count, 1, out error), error);
            if (Check(libewf_handle_segment_files_encrypted(handle, out error), error) != 0)
                throw new NotSupportedException("Encrypted EWF images are unsupported.");
            if (Check(libewf_handle_segment_files_corrupted(handle, out error), error) != 0)
                throw new InvalidDataException("The EWF segment set is damaged or incomplete.");
            Check(libewf_handle_get_number_of_acquiry_errors(handle, out int errors, out error), error);
            if (errors != 0)
                throw new InvalidDataException("This EWF records unreadable acquisition ranges; extraction has been withheld.");
        }
        catch { Dispose(); throw; }
        finally { if (names != IntPtr.Zero) ReportReleaseFailure(libewf_glob_wide_free(names, count, IntPtr.Zero), "segment list"); }
    }
    internal void VerifyRead()
    {
        Check(libewf_handle_get_number_of_checksum_errors(Handle, out int errors, out var error), error);
        if (errors != 0)
            throw new InvalidDataException("EWF chunk checksum verification failed.");
    }
    public void Dispose()
    {
        if (handle != IntPtr.Zero)
            ReportReleaseFailure(libewf_handle_free(ref handle, IntPtr.Zero), "image handle");
        foreach (var lease in leases)
            lease.Dispose();
        leases.Clear();
    }
}

internal static class EwfMedia
{
    internal static (Stream Stream, IReadOnlyList<FileStream> Segments) Open(string path)
    {
        var source = new EwfSource(path);
        try
        {
            Check(libewf_handle_get_media_size(source.Handle, out ulong length, out var error), error);
            return (new EwfReadStream(source, IntPtr.Zero, checked((long)length), true), source.SegmentFiles);
        }
        catch { source.Dispose(); throw; }
    }
}

internal sealed class EwfReadStream(EwfSource source, IntPtr entry, long length, bool ownsSource) : Stream
{
    private bool disposed;
    private long position;
    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => position;
        set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => checked(position + offset),
        SeekOrigin.End => checked(length + offset),
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateBufferArguments(buffer, offset, count);
        if (position >= length || count == 0)
            return 0;
        count = (int)Math.Min(count, length - position);
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var pointer = pin.AddrOfPinnedObject() + offset;
            IntPtr error;
            var read = entry == IntPtr.Zero ? libewf_handle_read_buffer_at_offset(source.Handle, pointer, (nuint)count, position, out error)
                : libewf_file_entry_read_buffer_at_offset(entry, pointer, (nuint)count, position, out error);
            int size = checked((int)Check(read, error));
            if (size <= 0 || size > count)
                throw new EndOfStreamException("Incomplete EWF data.");
            source.VerifyRead();
            position += size;
            return size;
        }
        finally { pin.Free(); }
    }
    public override void Flush()
    {
    }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsSource && !disposed)
            source.Dispose();
        disposed = true;
        base.Dispose(disposing);
    }
}
