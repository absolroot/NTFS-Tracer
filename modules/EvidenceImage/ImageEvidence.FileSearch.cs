using DiscUtils.Ntfs;
using DiscUtils.Ntfs.Internals;
using DiscUtils.Streams;

namespace EvidenceImage;

public static partial class ImageEvidence
{
    /// <summary>Search known paths, or enumerate allocated MFT names if no paths are specified.</summary>
    public static Task<ImageFileSearchResult> SearchNtfsFilesAsync(
        string imagePath, string volumeId, IReadOnlyCollection<string> extensions,
        IReadOnlyCollection<string>? directories = null, string? profilesDirectory = null,
        IProgress<ImageFileEntry>? firstMatch = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var suffixes = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.Run(() =>
        {
            using var guard = Guard(imagePath);
            using var media = Media.Open(guard.Name, cancellationToken);
            var volume = ResolveSelectedVolume(media, volumeId, cancellationToken);
            using var slice = new SubStream(media.Disk, volume.Offset, volume.Length);
            using var fs = new NtfsFileSystem(slice);
            fs.NtfsOptions.HideHiddenFiles = false;
            fs.NtfsOptions.HideSystemFiles = false;
            var search = new NtfsFileSearch(fs, suffixes, firstMatch, cancellationToken);
            return directories is null
                ? search.ScanMft()
                : search.ScanDirectories(directories, profilesDirectory);
        }, cancellationToken);
    }

    private sealed class NtfsFileSearch(
        NtfsFileSystem fs, HashSet<string> extensions, IProgress<ImageFileEntry>? firstMatch, CancellationToken ct)
    {
        private readonly Dictionary<ulong, ImageFileEntry> files = [];
        private int unread;

        internal ImageFileSearchResult ScanDirectories(IReadOnlyCollection<string> directories, string? profilesDirectory)
        {
            if (profilesDirectory is null)
            {
                foreach (string path in directories)
                    ScanDirectory(path);
            }
            else if (fs.DirectoryExists(profilesDirectory))
            {
                foreach (string profile in fs.GetDirectories(profilesDirectory))
                {
                    ct.ThrowIfCancellationRequested();
                    if ((fs.GetAttributes(profile) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    foreach (string directory in directories)
                        ScanDirectory(NtfsJoin(profile, directory));
                }
            }

            return Finish();
        }

        private void ScanDirectory(string root)
        {
            if (!fs.DirectoryExists(root))
                return;
            ct.ThrowIfCancellationRequested();
            try
            {
                if ((fs.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    return;
                foreach (string path in fs.GetFiles(root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (extensions.Contains(Path.GetExtension(path)))
                        TryAdd(path, expectedSequence: null, expectedIndex: null);
                }
            }
            catch (Exception error) when (ReadIssue(error)) { unread++; }
        }

        internal ImageFileSearchResult ScanMft()
        {
            MasterFileTable table = fs.GetMasterFileTable();
            foreach (MasterFileTableEntry entry in table.GetEntries(EntryStates.InUse))
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Flags & MasterFileTableEntryFlags.IsDirectory) != 0)
                    continue;
                foreach (FileNameAttribute name in Names(entry))
                {
                    if (!extensions.Contains(Path.GetExtension(name.FileName)))
                        continue;

                    // Use the recorded parent reference and the live directory entry.
                    // The installed DiscUtils enumeration's Index can drift after an invalid MFT slot.
                    try
                    {
                        string? directory = ResolveParent(table, name.ParentDirectory);
                        if (directory is null)
                        {
                            unread++;
                            continue;
                        }
                        var baseRecord = entry.BaseRecordReference;
                        int sequence = baseRecord.RecordSequenceNumber != 0
                            ? baseRecord.RecordSequenceNumber : entry.SequenceNumber;
                        long? index = baseRecord.RecordSequenceNumber != 0
                            ? baseRecord.RecordIndex : entry.SelfIndex != 0 ? entry.SelfIndex : null;
                        TryAdd(NtfsJoin(directory, name.FileName), sequence, index);
                    }
                    catch (Exception error) when (ReadIssue(error)) { unread++; }
                }
            }
            return Finish();
        }

        private void TryAdd(string path, int? expectedSequence, long? expectedIndex)
        {
            try
            {
                if (!fs.FileExists(path))
                {
                    unread++;
                    return;
                }
                if ((fs.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return;
                ulong reference = unchecked((ulong)fs.GetFileId(path));
                if ((expectedSequence is { } seq && reference >> 48 != (ulong)seq) ||
                    (expectedIndex is { } index && (reference & 0xffffffffffff) != (ulong)index))
                {
                    unread++;
                    return;
                }
                if (files.ContainsKey(reference))
                    return;
                var file = new ImageFileEntry(path, fs.GetFileLength(path), reference);
                files.Add(reference, file);
                if (files.Count == 1)
                    firstMatch?.Report(file);
            }
            catch (Exception error) when (ReadIssue(error)) { unread++; }
        }

        private ImageFileSearchResult Finish() =>
            new(files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(), unread);

        private string? ResolveParent(MasterFileTable table, MasterFileTableReference parent)
        {
            var visited = new HashSet<long>();
            var parts = new Stack<string>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long index = parent.RecordIndex;
                if (!visited.Add(index) || table[index] is not { } folder ||
                    folder.SequenceNumber != parent.RecordSequenceNumber ||
                    (folder.Flags & (MasterFileTableEntryFlags.InUse | MasterFileTableEntryFlags.IsDirectory)) !=
                    (MasterFileTableEntryFlags.InUse | MasterFileTableEntryFlags.IsDirectory))
                    return null;
                if (index == MasterFileTable.RootDirectoryIndex)
                    return "\\" + string.Join('\\', parts);
                if (Names(folder).FirstOrDefault() is not { } name)
                    return null;
                parts.Push(name.FileName);
                parent = name.ParentDirectory;
            }
        }

        private static IEnumerable<FileNameAttribute> Names(MasterFileTableEntry entry) =>
            entry.EnumerateAttributes(AttributeType.FileName).OfType<FileNameAttribute>()
                .Where(name => (int)name.FileNameNamespace != 2 &&
                    !string.IsNullOrEmpty(name.FileName) && name.FileName is not ("." or "..") &&
                    name.FileName.IndexOfAny(['\\', '/', ':', '\0']) < 0);

        private static bool ReadIssue(Exception error) =>
            error is IOException or ArgumentException or InvalidDataException or NotSupportedException;
    }

    private static string NtfsJoin(string directory, string name) =>
        directory.TrimEnd('\\') + "\\" + name.TrimStart('\\');
}
