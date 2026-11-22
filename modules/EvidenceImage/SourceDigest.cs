using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace EvidenceImage;

internal sealed class TransferProgress(IProgress<ImageReadProgress>? observer, string stage, string item, long total)
{
    private long lastTime;
    private long lastCount = -1;
    internal void Update(long count)
    {
        if (observer == null || count == lastCount)
            return;
        if (lastCount >= 0 && count < total && Stopwatch.GetElapsedTime(lastTime).TotalMilliseconds < 150)
            return;
        observer.Report(new(stage, item, count, total));
        lastTime = Stopwatch.GetTimestamp();
        lastCount = count;
    }
}

public static class EvidenceHash
{
    public static Task<string> Sha256Async(FileStream input, CancellationToken cancellationToken = default) =>
        Sha256Async(input, null, cancellationToken);

    internal static Task<string> Sha256Async(FileStream input, IProgress<ImageReadProgress>? observer, CancellationToken cancellationToken)
        => Task.Run(() => ReadAndRestoreAsync(input, observer, cancellationToken), cancellationToken);

    private static async Task<string> ReadAndRestoreAsync(FileStream input, IProgress<ImageReadProgress>? observer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        long position = input.Position;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            input.Position = 0;
            return await SourceDigest.Sha256Async(input, input.Name, buffer, "verify", observer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            input.Position = position;
        }
    }
}

internal static class SourceDigest
{
    internal static async Task<string> Sha256Async(FileStream source, string path, byte[] buffer, string stage,
        IProgress<ImageReadProgress>? observer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long total = source.Length;
        var progress = new TransferProgress(observer, stage, path, total);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        progress.Update(0);
        while (done < total)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, total - done);
            int length = source.IsAsync
                ? await source.ReadAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false)
                : source.Read(buffer.AsSpan(0, count));
            if (length == 0)
                throw new EndOfStreamException($"Unexpected end of source file: {path}");
            digest.AppendData(buffer, 0, length);
            progress.Update(done += length);
        }
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
}
