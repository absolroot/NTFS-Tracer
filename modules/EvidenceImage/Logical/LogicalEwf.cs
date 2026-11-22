using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static EvidenceImage.EwfNative;

namespace EvidenceImage;

public static class LogicalEvidence
{
    /// <summary>Extract unique, case-sensitive, absolute paths in a collected EWF catalog.
    /// A stream may be stored as file:stream, file:stream:$DATA, or as a child of a file entry.
    /// All requested entries must exist. Paths are never used as host filesystem output names.</summary>
    [SuppressMessage("Design", "CA1068", Justification = "Preserve the published parameter order for existing extraction clients.")]
    public static Task<ArtifactResult> ExtractArtifactsAsync(string imagePath, IReadOnlyList<ArtifactRequest> requests,
        string outputRoot, CancellationToken ct = default, IProgress<ImageReadProgress>? progress = null)
    {
        var profile = requests.ToArray();
        ImageEvidence.Validate(profile, logical: true);
        return LogicalEwf.ExtractExactAsync(imagePath, profile, outputRoot, ct, progress);
    }
}

internal static class LogicalEwf
{
    private sealed record Match(string Key, string Path, int[] Indices, long Size);
    private sealed record Collection(string Label, List<Match> Matches);
    private const int MaxEntries = 2_000_000;

    internal sealed class Catalog : IJournalImageSource
    {
        private readonly EwfSource image;
        private readonly Dictionary<string, Collection> groups;
        public ImageProbe Probe
        {
            get;
        }

        private Catalog(EwfSource image, CancellationToken ct)
        {
            this.image = image;
            (groups, var warnings) = Find(image, ct);
            var volumes = groups.Select(g => new ImageVolume(g.Key, g.Value.Label, -1, 0,
                g.Value.Matches.Select(m => m.Key).ToList().AsReadOnly())).ToList();
            if (volumes.Count == 0)
                warnings.Add("No collected $J, $MFT or $LogFile entry was identified in this logical image.");
            Probe = new(Format(image), volumes.AsReadOnly(), warnings.AsReadOnly());
        }

        internal static Catalog Open(string imagePath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var image = new EwfSource(imagePath);
            try
            {
                return new Catalog(image, ct);
            }
            catch { image.Dispose(); throw; }
        }

        public Task<PublishedArtifacts> ExtractAsync(string volume, string root, HashSet<string>? selected,
            IProgress<ImageReadProgress>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!groups.TryGetValue(volume, out var group))
                throw new InvalidDataException("The selected logical collection is unavailable.");
            if (selected != null)
            {
                var matches = group.Matches.Where(m => selected.Contains(m.Key)).ToList();
                if (matches.Count != selected.Count)
                    throw new FileNotFoundException("A selected artifact was not found uniquely in this logical collection.");
                group = new Collection(group.Label, matches);
            }
            return WriteAsync(image, group, root, null, ct, progress);
        }

        public void Dispose() => image.Dispose();
    }

    internal static Task<ImageProbe> ProbeAsync(string imagePath, CancellationToken ct) => Task.Run(() =>
    {
        using var catalog = Catalog.Open(imagePath, ct);
        return catalog.Probe;
    }, ct);

    internal static Task<PublishedArtifacts> ExtractAsync(string imagePath, string volumeId, string outputRoot, HashSet<string>? selected,
        IProgress<ImageReadProgress>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        using var catalog = Catalog.Open(imagePath, ct);
        return await catalog.ExtractAsync(volumeId, outputRoot, selected, progress, ct).ConfigureAwait(false);
    }, ct);

    internal static Task<ArtifactResult> ExtractExactAsync(string imagePath, ArtifactRequest[] profile, string outputRoot,
        CancellationToken ct, IProgress<ImageReadProgress>? progress) => Task.Run(async () =>
    {
        using var image = new EwfSource(imagePath);
        var paths = new Dictionary<string, (string Key, bool FileParent)>(StringComparer.Ordinal);
        foreach (var request in profile)
        {
            string path = request.NtfsPath[1..];
            if (request.StreamName == null)
                paths.Add(path, (request.Key, false));
            else
            {
                paths.Add(path + ":" + request.StreamName, (request.Key, false));
                paths.Add(path + ":" + request.StreamName + ":$DATA", (request.Key, false));
                paths.Add(path + "\\" + request.StreamName, (request.Key, true));
            }
        }
        var (groups, warnings) = Find(image, ct, paths);
        var matches = groups.Values.SelectMany(c => c.Matches).ToList();
        var missing = profile.Where(r => !matches.Any(m => m.Key == r.Key)).ToList();
        if (warnings.Count > 0)
            throw new InvalidDataException(string.Join(" ", warnings));
        if (missing.Count > 0)
            throw new FileNotFoundException("No collected entry for " + string.Join(", ", missing.Select(r => r.NtfsPath + (r.StreamName == null ? "" : ":" + r.StreamName))));
        return (await WriteAsync(image, new("\\", matches), outputRoot, profile.ToDictionary(a => a.Key, a => a.OutputName),
            ct, progress).ConfigureAwait(false)).Result;
    }, ct);

    private static async Task<PublishedArtifacts> WriteAsync(EwfSource image, Collection group, string outputRoot,
        IReadOnlyDictionary<string, string>? filenames, CancellationToken ct, IProgress<ImageReadProgress>? progress)
    {
        using var batch = new ExtractionBatch(outputRoot, filenames);
        var catalogHashes = new Dictionary<string, object>();
        foreach (var match in group.Matches)
        {
            ct.ThrowIfCancellationRequested();
            var entry = Root(image);
            try
            {
                foreach (int index in match.Indices)
                {
                    IntPtr next = Child(entry, index);
                    ReleaseEntry(ref entry);
                    entry = next;
                }
                using var data = new EwfReadStream(image, entry, match.Size, false);
                var recorded = CatalogHash(entry);
                if (recorded == null)
                    await batch.AddAsync(match.Key, match.Path, data, match.Size, ct, progress).ConfigureAwait(false);
                else
                {
                    using var algorithm = CreateRecordedHashVerifier(recorded.Value.Type);
                    using var checkedBytes = new CryptoStream(data, algorithm, CryptoStreamMode.Read, true);
                    await batch.AddAsync(match.Key, match.Path, checkedBytes, match.Size, ct, progress).ConfigureAwait(false);
                    if (checkedBytes.ReadByte() != -1 || !Convert.ToHexStringLower(algorithm.Hash!).Equals(recorded.Value.Digest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"The collected entry does not match its recorded {recorded.Value.Type}: {match.Path}");
                    catalogHashes.Add(match.Key, new
                    {
                        algorithm = recorded.Value.Type,
                        digest = recorded.Value.Digest
                    });
                }
            }
            finally { ReleaseEntry(ref entry); }
        }
        var segments = new List<object>();
        foreach (var file in image.SegmentFiles)
        {
            file.Position = 0;
            segments.Add(new
            {
                path = file.Name,
                length = file.Length,
                sha256 = await SourceDigest.Sha256Async(file, file.Name, batch.TransferBuffer, "hash", progress, ct).ConfigureAwait(false)
            });
        }
        ct.ThrowIfCancellationRequested();
        return batch.CommitArtifacts(new
        {
            format = Format(image),
            kind = "logical collection",
            collection = group.Label,
            reader = "libewf " + Marshal.PtrToStringAnsi(libewf_get_version()),
            segments,
            catalogHashes
        });
    }

    [SuppressMessage("Security", "CA5350", Justification = "Verify SHA-1 recorded by the acquisition tool. ExtractionBatch separately computes SHA-256.")]
    [SuppressMessage("Security", "CA5351", Justification = "Verify MD5 recorded by the acquisition tool. ExtractionBatch separately computes SHA-256.")]
    private static HashAlgorithm CreateRecordedHashVerifier(string digestType) => digestType switch
    {
        "SHA-1" => SHA1.Create(),
        "MD5" => MD5.Create(),
        _ => throw new NotSupportedException($"Unsupported catalog hash: {digestType}")
    };

    private static (string Type, string Digest)? CatalogHash(IntPtr entry)
    {
        var text = new byte[41];
        if (Check(libewf_file_entry_get_utf8_hash_value_sha1(entry, text, (nuint)text.Length, out var error), error) == 1)
            return ("SHA-1", System.Text.Encoding.ASCII.GetString(text).TrimEnd('\0'));
        if (Check(libewf_file_entry_get_utf8_hash_value_md5(entry, text, (nuint)text.Length, out error), error) == 1)
            return ("MD5", System.Text.Encoding.ASCII.GetString(text).TrimEnd('\0'));
        return null;
    }

    private static string Format(EwfSource image) => image.IsLx01 ? "EWF logical (Lx01)" : "EWF logical (L01)";

    private static IntPtr Root(EwfSource image)
    {
        if (Check(libewf_handle_get_root_file_entry(image.Handle, out var root, out var error), error) == 0)
            throw new InvalidDataException("The EWF contains no readable logical file catalog.");
        return root;
    }
    private static IntPtr Child(IntPtr entry, int index)
    {
        Check(libewf_file_entry_get_sub_file_entry(entry, index, out var child, out var error), error);
        return child;
    }
    private static string Name(IntPtr entry)
    {
        Check(libewf_file_entry_get_utf16_name_size(entry, out var size, out var error), error);
        if (size > 32768)
            throw new InvalidDataException("Invalid logical entry name length.");
        if (size == 0)
            return "";
        var name = new ushort[(int)size];
        Check(libewf_file_entry_get_utf16_name(entry, name, size, out error), error);
        return new string(MemoryMarshal.Cast<ushort, char>(name.AsSpan()).TrimEnd('\0'));
    }
    private static (Dictionary<string, Collection>, List<string>) Find(EwfSource image, CancellationToken ct,
        Dictionary<string, (string Key, bool FileParent)>? exactPaths = null)
    {
        var groups = new Dictionary<string, Collection>();
        var warnings = new List<string>();
        var names = new List<string>();
        var route = new List<int>();
        int visited = 0;
        string generation = image.Generation;
        void Visit(IntPtr parent, bool parentIsFile)
        {
            ct.ThrowIfCancellationRequested();
            Check(libewf_file_entry_get_number_of_sub_file_entries(parent, out int count, out var error), error);
            if (count < 0 || count > MaxEntries - visited || route.Count > 128)
                throw new InvalidDataException("Logical catalog exceeds supported traversal limits.");
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > MaxEntries)
                    throw new InvalidDataException("Too many logical entries.");
                IntPtr entry = Child(parent, i);
                try
                {
                    string name = Name(entry);
                    names.Add(name.Replace("%", "%25").Replace("\\", "%5C").Replace("/", "%2F"));
                    route.Add(i);
                    Check(libewf_file_entry_get_type(entry, out byte type, out error), error);
                    (string Key, int Tail)? match = null;
                    if (type == 'f')
                    {
                        if (exactPaths == null)
                            match = Identify(names);
                        else if (exactPaths.TryGetValue(string.Join("\\", names), out var request) && (!request.FileParent || parentIsFile))
                            match = (request.Key, names.Count);
                    }
                    if (match is { } hit)
                    {
                        int groupDepth = names.Count - hit.Tail;
                        string id = generation + ":collection/" + string.Join("/", route.Take(groupDepth));
                        var label = string.Join("\\", names.Take(groupDepth));
                        if (!groups.TryGetValue(id, out var collection))
                            groups.Add(id, collection = new(label.Length == 0 ? "\\" : label, []));
                        Check(libewf_file_entry_get_size(entry, out ulong size, out error), error);
                        collection.Matches.Add(new(hit.Key, string.Join("\\", names), [.. route], checked((long)size)));
                    }
                    Visit(entry, type == 'f'); // ADS may be represented below a file entry.
                    names.RemoveAt(names.Count - 1);
                    route.RemoveAt(route.Count - 1);
                }
                finally { ReleaseEntry(ref entry); }
            }
        }
        IntPtr root = Root(image);
        try
        {
            Visit(root, false);
        }
        finally { ReleaseEntry(ref root); }
        foreach (var (id, group) in groups.ToArray())
        {
            var duplicates = group.Matches.GroupBy(m => m.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            if (duplicates.Count > 0)
            {
                warnings.Add($"Ambiguous entries in {group.Label}: {string.Join(", ", duplicates)}.");
                group.Matches.RemoveAll(m => duplicates.Contains(m.Key));
            }
            if (group.Matches.Count == 0)
                groups.Remove(id);
        }
        return (groups, warnings);
    }
    private static (string Key, int Tail)? Identify(List<string> names)
    {
        string leaf = names[^1].ToUpperInvariant();
        bool ParentIs(int offset, string value) => names.Count > offset && names[^(offset + 1)].Equals(value, StringComparison.OrdinalIgnoreCase);
        if (leaf == "$MFT")
            return ("mft", 1);
        if (leaf == "$LOGFILE")
            return ("logFile", 1);
        if (leaf == "$J" && ParentIs(1, "$UsnJrnl"))
            return ("journal", ParentIs(2, "$Extend") ? 3 : 2);
        if (leaf is "$USNJRNL:$J" or "$USNJRNL_$J" or "$USNJRNL-$J" or "$USNJRNL:$J:$DATA")
            return ("journal", ParentIs(1, "$Extend") ? 2 : 1);
        return leaf == "$J" ? ("journal", 1) : null;
    }
}
