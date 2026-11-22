namespace EvidenceImage;

internal interface IJournalImageSource : IDisposable
{
    ImageProbe Probe
    {
        get;
    }
    Task<PublishedArtifacts> ExtractAsync(string volume, string root, HashSet<string> selected, IProgress<ImageReadProgress>? progress, CancellationToken ct);
}

public sealed class JournalImageSession : IDisposable
{
    private readonly IJournalImageSource source;
    private readonly object gate = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private Dictionary<string, FileStream> verified = new(PathComparer);
    private Batch? current;
    private Batch? pending;
    private CancellationTokenSource? extracting;
    private bool disposed;

    private sealed record Batch(string Volume, string Root, PublishedArtifacts Publication)
    {
        internal ExtractionResult Files { get; } = Publication.Result.ToJournalExtraction();
        internal ExtractionResult Selected(HashSet<string> selection) => new(
            selection.Contains("journal") ? Files.JournalPath : null,
            selection.Contains("mft") ? Files.MftPath : null,
            selection.Contains("logFile") ? Files.LogFilePath : null, Files.ManifestPath);
        internal bool Covers(string? volume, string root, IReadOnlyCollection<string> keys) =>
            Volume == volume && PathComparer.Equals(Root, root) && keys.Count > 0
            && keys.All(Publication.Result.Files.ContainsKey) && keys.Distinct(StringComparer.Ordinal).Count() == keys.Count;
    }

    private JournalImageSession(string path, IJournalImageSource source) => (ImagePath, this.source) = (path, source);
    public ImageProbe Probe => source.Probe;
    public string ImagePath
    {
        get;
    }
    public string? ExtractedVolume
    {
        get
        {
            lock (gate)
                return current?.Volume;
        }
    }
    public ExtractionResult? Extraction
    {
        get
        {
            lock (gate)
                return current?.Files;
        }
    }

    public static async Task<JournalImageSession> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string absolute = Path.GetFullPath(path);
        var opened = await Task.Run(() => ImageEvidence.OpenJournalSource(absolute, cancellationToken), cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(absolute, opened);
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    public bool CanReuse(string? volume, string root, IReadOnlyCollection<string> keys)
    {
        lock (gate)
            return OperatingSystem.IsWindows() && current?.Covers(volume, NormalizeRoot(root), keys) == true;
    }

    public string? GetVerifiedSha256(FileStream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (gate)
        {
            if (!verified.TryGetValue(input.Name, out var lease) || !OperatingSystem.IsWindows())
                return null;
            if (!SourceIdentity.SameFile(lease, input))
                throw new IOException($"The extracted file identity could not be verified: {input.Name}");
            return current!.Publication.Sha256ByPath[lease.Name];
        }
    }

    public async Task<ExtractionResult> ExtractAsync(string volume, string outputRoot, IReadOnlyCollection<string> keys,
        IProgress<ImageReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selection = ImageEvidence.SelectedKeys(keys);
        string root = NormalizeRoot(outputRoot);
        Batch? batch;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (extracting != null)
                throw new InvalidOperationException("An image extraction is already running.");
            if (OperatingSystem.IsWindows() && current?.Covers(volume, root, selection) == true)
                return current.Selected(selection);
            batch = pending?.Covers(volume, root, selection) == true ? pending : null;
            extracting = work;
        }

        var ct = work.Token;
        var opened = new Dictionary<string, FileStream>(PathComparer);
        try
        {
            if (batch == null)
            {
                var publication = await Task.Run(() => source.ExtractAsync(volume, root, selection, progress, ct), ct).ConfigureAwait(false);
                batch = new(volume, root, publication);
            }
            lock (gate)
            {
                if (!disposed)
                    pending = batch;
                ct.ThrowIfCancellationRequested();
            }
            foreach (var (path, expected) in batch.Publication.Sha256ByPath)
            {
                ct.ThrowIfCancellationRequested();
                var lease = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                opened.Add(path, lease);
                string digest = await EvidenceHash.Sha256Async(lease, progress, ct).ConfigureAwait(false);
                if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The published file does not match the extraction SHA-256: {path}");
            }
            lock (gate)
            {
                ct.ThrowIfCancellationRequested();
                (verified, opened) = (opened, verified);
                current = batch;
                pending = null;
                return batch.Selected(selection);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            lock (gate)
                pending = null;
            throw;
        }
        finally
        {
            foreach (var file in opened.Values)
                file.Dispose();
            lock (gate)
            {
                extracting = null;
                if (disposed)
                    source.Dispose();
            }
        }
    }

    private static string NormalizeRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            current = pending = null;
            foreach (var file in verified.Values)
                file.Dispose();
            verified.Clear();
            if (extracting == null)
                source.Dispose();
            else
                extracting.Cancel();
        }
    }
}
