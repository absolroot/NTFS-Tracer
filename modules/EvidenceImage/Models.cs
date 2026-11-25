namespace EvidenceImage;

public sealed record ImageProbe(string Format, IReadOnlyList<ImageVolume> Volumes, IReadOnlyList<string> Warnings);
public sealed record ImageVolume(string Id, string Label, long Offset, long Length, IReadOnlyList<string> Artifacts);
public sealed record ExtractionResult(string? JournalPath, string? MftPath, string? LogFilePath, string ManifestPath);
public sealed record ArtifactRequest(string Key, string NtfsPath, string? StreamName, string OutputName)
{
    public ulong? ExpectedFileReference
    {
        get; init;
    }
}

public sealed record ImageFileEntry(string Path, long Length, ulong FileReference);
public sealed record ImageFileSearchResult(IReadOnlyList<ImageFileEntry> Files, int UnreadEntries);
public sealed record ArtifactResult(IReadOnlyDictionary<string, string> Files, string ManifestPath)
{
    public IReadOnlyDictionary<string, string> Sha256ByKey
    {
        get; init;
    } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    internal ExtractionResult ToJournalExtraction() => new(
        Files.GetValueOrDefault("journal"), Files.GetValueOrDefault("mft"), Files.GetValueOrDefault("logFile"), ManifestPath);
}

internal sealed record PublishedArtifacts(ArtifactResult Result, IReadOnlyDictionary<string, string> Sha256ByPath);

/// <summary>Stage is "extract" (logical bytes of this key), "hash" (source file bytes), or "verify" (published file bytes).</summary>
public sealed record ImageReadProgress(string Stage, string Item, long Completed, long Total);
