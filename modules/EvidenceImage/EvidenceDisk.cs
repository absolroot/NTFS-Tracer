using System.Diagnostics.CodeAnalysis;

namespace EvidenceImage;

/// <summary>Seekable, read-only logical disk bytes for independent filesystem or partition parsers.
/// Dispose the stream to release the container and its component files. Use one reader per task.</summary>
[SuppressMessage("Naming", "CA1710", Justification = "EvidenceDisk is the public disk-stream type used by existing filesystem parsers.")]
public sealed class EvidenceDisk : Stream
{
    private readonly Media media;
    private bool disposed;
    private EvidenceDisk(Media media) => this.media = media;

    public static EvidenceDisk OpenRead(string imagePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return new EvidenceDisk(Media.Open(imagePath, ct));
    }
    public string Format => media.Format;
    private Stream Disk => !disposed ? media.Disk : throw new ObjectDisposedException(nameof(EvidenceDisk));
    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length => Disk.Length;
    public override long Position
    {
        get => Disk.Position; set => Disk.Position = value;
    }
    public override int Read(byte[] buffer, int offset, int count) => Disk.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => Disk.Read(buffer);
    [SuppressMessage("Naming", "CA1725", Justification = "Preserve the ct named argument of this published override.")]
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Disk.ReadAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => Disk.Seek(offset, origin);
    public override void Flush()
    {
    }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            media.Dispose();
        }
        base.Dispose(disposing);
    }
}
