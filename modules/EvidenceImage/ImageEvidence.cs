using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using DiscUtils.Ntfs;
using DiscUtils.Streams;

namespace EvidenceImage;

/// <summary>Read-only image access. Each operation owns its streams. Offsets are in decoded disk bytes.</summary>
public static partial class ImageEvidence
{
    internal static readonly ArtifactRequest[] JournalArtifacts = [
        new("journal", @"\$Extend\$UsnJrnl", "$J", "$J"),
        new("mft", @"\$MFT", null, "$MFT"),
        new("logFile", @"\$LogFile", null, "$LogFile")
    ];

    public static Task<ImageProbe> ProbeAsync(string imagePath, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            using var guard = Guard(imagePath);
            if (IsLogical(guard))
                return await LogicalEwf.ProbeAsync(guard.Name, ct).ConfigureAwait(false);
            using var media = Media.Open(guard.Name, ct);
            return Probe(media, ct);
        }, ct);

    internal static IJournalImageSource OpenJournalSource(string path, CancellationToken ct)
    {
        using var guard = Guard(path);
        if (IsLogical(guard))
            return LogicalEwf.Catalog.Open(guard.Name, ct);
        var media = Media.Open(guard.Name, ct);
        try
        {
            return new DiskJournalSource(media, ct);
        }
        catch { media.Dispose(); throw; }
    }

    private sealed class DiskJournalSource : IJournalImageSource
    {
        private readonly Media media;
        private readonly Dictionary<string, VolumeRange?> volumes = [];
        internal DiskJournalSource(Media media, CancellationToken ct)
        {
            this.media = media;
            Probe = ImageEvidence.Probe(media, ct, volumes);
        }
        public ImageProbe Probe
        {
            get;
        }
        public Task<PublishedArtifacts> ExtractAsync(string volume, string root, HashSet<string> selected,
            IProgress<ImageReadProgress>? progress, CancellationToken ct)
        {
            if (!volumes.TryGetValue(volume, out var range) || range == null)
                throw new InvalidDataException("The selected NTFS volume is ambiguous or unavailable.");
            var requests = JournalArtifacts.Where(a => selected.Contains(a.Key)).ToArray();
            return ExtractDiskAsync(media, volume, root, requests, true, ct, progress, range);
        }
        public void Dispose() => media.Dispose();
    }

    [SuppressMessage("Design", "CA1068", Justification = "Preserve the published parameter order for existing extraction clients.")]
    public static Task<ExtractionResult> ExtractAsync(string imagePath, string volumeId, string outputRoot, CancellationToken ct = default,
        IProgress<ImageReadProgress>? progress = null) =>
        AsJournalExtractionAsync(ExtractPresetAsync(imagePath, volumeId, outputRoot, null, ct, progress));

    /// <summary>Extract only the chosen preset keys. All selected streams must exist in this volume or logical collection.</summary>
    [SuppressMessage("Design", "CA1068", Justification = "Preserve the published parameter order for existing extraction clients.")]
    public static Task<ExtractionResult> ExtractSelectedAsync(string imagePath, string volumeId, string outputRoot,
        IReadOnlyCollection<string> keys, CancellationToken ct = default, IProgress<ImageReadProgress>? progress = null)
        => AsJournalExtractionAsync(ExtractPresetAsync(imagePath, volumeId, outputRoot, SelectedKeys(keys), ct, progress));

    private static async Task<ExtractionResult> AsJournalExtractionAsync(Task<PublishedArtifacts> extraction) =>
        (await extraction.ConfigureAwait(false)).Result.ToJournalExtraction();

    internal static HashSet<string> SelectedKeys(IReadOnlyCollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var selected = new HashSet<string>(keys, StringComparer.Ordinal);
        if (selected.Count == 0 || selected.Count != keys.Count || selected.Except(JournalArtifacts.Select(a => a.Key)).Any())
            throw new ArgumentException("Select one or more distinct keys: journal, mft, logFile.", nameof(keys));
        return selected;
    }

    private static Task<PublishedArtifacts> ExtractPresetAsync(string imagePath, string id, string outputRoot,
        HashSet<string>? selected, CancellationToken ct, IProgress<ImageReadProgress>? progress) =>
        Task.Run(async () =>
        {
            using var guard = Guard(imagePath);
            if (IsLogical(guard))
                return await LogicalEwf.ExtractAsync(guard.Name, id, outputRoot, selected, progress, ct).ConfigureAwait(false);
            using var media = Media.Open(guard.Name, ct);
            var requests = selected == null ? JournalArtifacts : JournalArtifacts.Where(a => selected.Contains(a.Key)).ToArray();
            return await ExtractDiskAsync(media, id, outputRoot, requests, selected != null, ct, progress).ConfigureAwait(false);
        }, ct);

    /// <summary>Extract all requested current NTFS files/streams from one selected volume, or publish no output.</summary>
    [SuppressMessage("Design", "CA1068", Justification = "Preserve the published parameter order for existing extraction clients.")]
    public static Task<ArtifactResult> ExtractArtifactsAsync(string imagePath, string volumeId, string outputRoot,
        IReadOnlyList<ArtifactRequest> requests, CancellationToken ct = default, IProgress<ImageReadProgress>? progress = null)
    {
        var profile = requests.ToArray();
        Validate(profile);
        return Task.Run(async () =>
        {
            using var guard = Guard(imagePath);
            if (IsLogical(guard))
                throw new NotSupportedException("For a logical EWF catalog, use LogicalEvidence.ExtractArtifactsAsync with collected entry paths.");
            using var media = Media.Open(guard.Name, ct);
            return (await ExtractDiskAsync(media, volumeId, outputRoot, profile, true, ct, progress).ConfigureAwait(false)).Result;
        }, ct);
    }

    private static FileStream Guard(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static bool IsLogical(FileStream file)
    {
        Span<byte> head = stackalloc byte[4];
        return file.ReadAtLeast(head, 4, false) == 4 && ImageFileTypes.Header(head) == ImageSignature.LogicalEwf;
    }

    private static ImageProbe Probe(Media media, CancellationToken ct, Dictionary<string, VolumeRange?>? selectedRanges = null)
    {
        var warnings = new List<string>();
        var volumes = new List<ImageVolume>();
        string generation = media.Generation;
        foreach (var candidate in DiskLayout.Read(media.Disk, warnings, ct))
        {
            ct.ThrowIfCancellationRequested();
            byte[] boot = DiskLayout.ReadAt(media.Disk, candidate.Offset, 512);
            if (DiskLayout.IsBitLocker(boot))
            {
                warnings.Add($"BitLocker volume at byte {candidate.Offset} is locked.");
                continue;
            }
            if (!DiskLayout.IsNtfs(boot))
                continue;
            try
            {
                using var slice = new SubStream(media.Disk, candidate.Offset, candidate.Length);
                using var fs = new NtfsFileSystem(slice);
                var artifacts = JournalArtifacts.Where(a => fs.FileExists(StreamPath(a))).Select(a => a.Key).ToList().AsReadOnly();
                string id = VolumeId(generation, candidate, boot);
                if (selectedRanges?.TryAdd(id, candidate) == false)
                    selectedRanges[id] = null;
                volumes.Add(new(id, VolumeLabel(fs), candidate.Offset, candidate.Length, artifacts));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                warnings.Add($"NTFS volume at byte {candidate.Offset}: {ex.Message}");
            }
        }
        if (volumes.Count == 0 && warnings.Count == 0)
            warnings.Add("No readable NTFS volume was found.");
        return new(media.Format, volumes.AsReadOnly(), warnings.Distinct().ToList().AsReadOnly());
    }

    private static string VolumeId(string generation, VolumeRange volume, byte[] boot)
    {
        ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(0x48));
        return $"{generation}:{volume.Offset:X}:{volume.Length:X}:{serial:X16}";
    }

    private static string VolumeLabel(NtfsFileSystem fs)
    {
        string label = fs.VolumeLabel?.TrimEnd('\0') ?? "";
        return label.Length > 0 ? label : "NTFS";
    }

    private static VolumeRange ResolveSelectedVolume(Media media, string id, CancellationToken ct)
    {
        string generation = media.Generation;
        VolumeRange? match = null;
        foreach (var candidate in DiskLayout.Read(media.Disk, [], ct))
        {
            ct.ThrowIfCancellationRequested();
            byte[] boot = DiskLayout.ReadAt(media.Disk, candidate.Offset, 512);
            if (!DiskLayout.IsNtfs(boot) || VolumeId(generation, candidate, boot) != id)
                continue;
            if (match != null)
                throw new InvalidDataException("Ambiguous NTFS volume identity in the partition table.");
            match = candidate;
        }
        return match ?? throw new InvalidDataException("The selected volume changed or is unavailable. Open the image again.");
    }

    private static async Task<PublishedArtifacts> ExtractDiskAsync(Media media, string id, string root, ArtifactRequest[] requests, bool requireAll,
        CancellationToken ct, IProgress<ImageReadProgress>? progress, VolumeRange? selectedRange = null)
    {
        var volume = selectedRange ?? ResolveSelectedVolume(media, id, ct);
        using var slice = new SubStream(media.Disk, volume.Offset, volume.Length);
        using var fs = new NtfsFileSystem(slice);
        var refs = new List<object>();
        using var batch = new ExtractionBatch(root, requests.ToDictionary(r => r.Key, r => r.OutputName));
        foreach (var artifact in requests)
        {
            ct.ThrowIfCancellationRequested();
            string path = StreamPath(artifact);
            SparseStream opened;
            try
            {
                opened = fs.OpenFile(path, FileMode.Open, FileAccess.Read);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (requireAll)
                    throw new FileNotFoundException($"Stream not found on the selected NTFS volume: {path}", path, ex);
                continue;
            }
            using var input = opened;
            ulong reference = unchecked((ulong)fs.GetFileId(artifact.NtfsPath));
            if (artifact.ExpectedFileReference is { } expected && reference != expected)
                throw new InvalidDataException($"The NTFS file reference changed: {path}");
            refs.Add(new
            {
                artifact.Key,
                artifact.NtfsPath,
                artifact.StreamName,
                fileReference = reference.ToString("X16", CultureInfo.InvariantCulture),
                mftEntry = reference & 0xffffffffffff,
                sequence = reference >> 48
            });
            await batch.AddAsync(artifact.Key, path, input, input.Length, ct, progress).ConfigureAwait(false);
        }
        var sources = await media.HashSourcesAsync(batch.TransferBuffer, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return batch.CommitArtifacts(new
        {
            provider = media.Format,
            fileSystem = "NTFS",
            volumeId = id,
            volumeLabel = VolumeLabel(fs),
            volumeOffsetBytes = volume.Offset,
            volumeLengthBytes = volume.Length,
            sectorSize = fs.SectorSize,
            clusterSize = fs.ClusterSize,
            sourceFiles = sources,
            streamReferences = refs
        });
    }

    private static string StreamPath(ArtifactRequest a) => a.StreamName is null ? a.NtfsPath : a.NtfsPath + ":" + a.StreamName;
    internal static void Validate(ArtifactRequest[] requests, bool logical = false)
    {
        if (requests.Length == 0)
            throw new ArgumentException("Select at least one artifact.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        foreach (var a in requests)
        {
            if (string.IsNullOrWhiteSpace(a.Key) || !keys.Add(a.Key))
                throw new ArgumentException("Artifact keys must be unique.");
            if (string.IsNullOrWhiteSpace(a.NtfsPath) || !a.NtfsPath.StartsWith('\\') || a.NtfsPath[1..].Split('\\').Any(p => p.Length == 0 || p is "." or "..")
                || a.NtfsPath.IndexOfAny(['/', '\0']) >= 0 || (!logical && a.NtfsPath.Contains(':'))
                || (a.StreamName != null && (a.StreamName.Length == 0 || a.StreamName.IndexOfAny(['/', '\\', ':', '\0']) >= 0)))
                throw new ArgumentException("Specify an absolute volume-relative NTFS path and a separate stream name.");
            string n = a.OutputName;
            if (string.IsNullOrWhiteSpace(n) || n.EndsWith('.') || n.EndsWith(' ') || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n.IndexOfAny(['/', '\\', ':']) >= 0
                || Regex.IsMatch(n, @"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || !names.Add(n))
                throw new ArgumentException("Artifact output must be a unique local filename.");
        }
    }
}
