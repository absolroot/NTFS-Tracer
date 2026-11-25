using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DiscUtils.Streams;
using Microsoft.Win32.SafeHandles;

namespace EvidenceImage;

internal sealed class ExtractionBatch : IDisposable
{
    private readonly string staging, destination;
    private readonly Dictionary<string, Artifact> artifacts = new();
    private bool committed;
    private readonly IReadOnlyDictionary<string, string> filenames;
    private const uint FsctlSetSparse = 0x900C4;
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    internal byte[] TransferBuffer { get; } = new byte[1024 * 1024];
    private sealed record Artifact(string SourceName, string FileName, long Length, string Sha256);

    public ExtractionBatch(string outputRoot, IReadOnlyDictionary<string, string>? filenames = null)
    {
        this.filenames = filenames ?? ImageEvidence.JournalArtifacts.ToDictionary(a => a.Key, a => a.OutputName);
        string root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        string token = Guid.NewGuid().ToString("N");
        staging = Path.Combine(root, ".extract-" + token);
        destination = Path.Combine(root, "evidence-" + token);
        Directory.CreateDirectory(staging);
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inBytes,
        IntPtr output, uint outBytes, out uint returned, IntPtr overlapped);

    public async Task AddAsync(string key, string sourceName, Stream source, long length, CancellationToken ct, IProgress<ImageReadProgress>? observer = null)
    {
        string filename = filenames[key];
        if (length < 0 || artifacts.ContainsKey(key))
            throw new InvalidDataException("Invalid evidence stream.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(Path.Combine(staging, filename), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (OperatingSystem.IsWindows())
            DeviceIoControl(file.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        byte[] buffer = TransferBuffer;
        long position = 0;
        var progress = new TransferProgress(observer, "extract", key, length);
        progress.Update(0);
        IEnumerable<StreamExtent> ranges = source is SparseStream sparse
            ? sparse.Extents
            : [new StreamExtent(0, length)];
        foreach (var range in ranges)
        {
            ct.ThrowIfCancellationRequested();
            if (range.Start < position || range.Length < 0 || range.Start > length - range.Length)
                throw new InvalidDataException($"Invalid logical extent in {sourceName}.");
            if (range.Start > position)
            {
                AppendHole(range.Start);
                source.Position = range.Start;
            }
            long end = range.Start + range.Length;
            while (position < end)
            {
                ct.ThrowIfCancellationRequested();
                int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position)), ct).ConfigureAwait(false);
                if (count == 0)
                    throw new EndOfStreamException($"Unexpected end of {sourceName}.");
                hash.AppendData(buffer, 0, count);
                if (buffer.AsSpan(0, count).IndexOfAnyExcept((byte)0) < 0)
                    file.Seek(count, SeekOrigin.Current);
                else
                    await file.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                progress.Update(position += count);
            }
        }
        if (position < length)
            AppendHole(length);
        file.SetLength(length);
        artifacts.Add(key, new(sourceName, filename, length, Convert.ToHexStringLower(hash.GetHashAndReset())));

        void AppendHole(long end)
        {
            buffer.AsSpan(0, (int)Math.Min(buffer.Length, end - position)).Clear();
            file.Position = end;
            while (position < end)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, end - position);
                hash.AppendData(buffer, 0, count);
                progress.Update(position += count);
            }
        }
    }

    public PublishedArtifacts CommitArtifacts(object provenance)
    {
        if (artifacts.Count == 0)
            throw new InvalidDataException("No requested artifacts were extracted.");
        const string manifest = "manifest.json";
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                module = "EvidenceImage",
                moduleVersion = typeof(ImageEvidence).Assembly.GetName().Version!.ToString(3),
                provenance,
                artifacts
            },
            ManifestOptions);
        File.WriteAllBytes(Path.Combine(staging, manifest), manifestBytes);
        var files = artifacts.ToDictionary(i => i.Key, i => Path.Combine(destination, i.Value.FileName));
        string manifestPath = Path.Combine(destination, manifest);
        var hashes = artifacts.ToDictionary(i => files[i.Key], i => i.Value.Sha256);
        hashes.Add(manifestPath, Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
        Directory.Move(staging, destination);
        committed = true;
        var result = new ArtifactResult(files, manifestPath)
        {
            Sha256ByKey = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                artifacts.ToDictionary(entry => entry.Key, entry => entry.Value.Sha256))
        };
        return new(result, hashes);
    }

    public void Dispose()
    {
        if (!committed && Directory.Exists(staging))
            Directory.Delete(staging, true);
    }
}
