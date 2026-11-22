using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DiscUtils;
using DiscUtils.Streams;

namespace EvidenceImage;

internal sealed class Media : IDisposable
{
    private readonly List<IDisposable> resources = [];
    private readonly Dictionary<string, FileStream> files = new(StringComparer.OrdinalIgnoreCase);
    private List<object>? verifiedSources;
    internal Stream Disk { get; private set; } = Stream.Null;
    internal string Format { get; private set; } = "";
    internal string Generation => SourceIdentity.Generation(files.Values);
    private T Own<T>(T value) where T : IDisposable
    {
        resources.Add(value);
        return value;
    }
    private FileStream Acquire(string path)
    {
        if (!files.TryGetValue(path, out var stream))
        {
            stream = Own(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            files.Add(path, stream);
        }
        return stream;
    }

    internal static Media Open(string path, CancellationToken ct)
    {
        var media = new Media();
        try
        {
            media.Initialize(Path.GetFullPath(path), ct);
            return media;
        }
        catch { media.Dispose(); throw; }
    }

    private void Initialize(string path, CancellationToken ct)
    {
        var raw = Acquire(path);
        var head = DiskLayout.ReadAt(raw, 0, (int)Math.Min(raw.Length, 512));
        raw.Position = 0;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var signature = ImageFileTypes.Detect(raw, head);
        if (signature == ImageSignature.LogicalEwf)
            throw new NotSupportedException("Logical EWF contains catalog entries, not disk sectors. Use LogicalEvidence.");
        if (signature is ImageSignature.Ewf or ImageSignature.Ewf2)
        {
            var ewf = EwfMedia.Open(path);
            Disk = Own(ewf.Stream);
            foreach (var segment in ewf.Segments)
                files[segment.Name] = segment;
            Format = signature == ImageSignature.Ewf2 ? "EWF2" : "EWF";
        }
        else if (signature == ImageSignature.Aff)
        {
            Format = "AFF1";
            Disk = Own(AffMedia.Open(raw, ct));
        }
        else if (signature == ImageSignature.Qcow)
        {
            if (head.Length < 8)
                throw new InvalidDataException("Incomplete QCOW header.");
            Format = "QCOW" + System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
            Disk = Own(new QcowMedia(raw));
        }
        else if (signature == ImageSignature.Vhdx)
            OpenLayer("VHDX", new DiscUtils.Vhdx.DiskImageFile(raw, Ownership.None));
        else if (signature == ImageSignature.Vmdk)
            OpenLayer("VMDK", new DiscUtils.Vmdk.DiskImageFile(raw, Ownership.None));
        else if (signature == ImageSignature.VmdkDescriptor)
            OpenFlatDescriptor(raw);
        else if (signature == ImageSignature.Vhd)
            OpenLayer("VHD", new DiscUtils.Vhd.DiskImageFile(raw, Ownership.None));
        else if (ImageFileTypes.IsSplitRaw(ext))
            OpenSplit(raw, ext, ct);
        else if (ImageFileTypes.RequiresContainerSignature(ext))
            throw new NotSupportedException("Unsupported or damaged image header. For segmented evidence select the first segment or descriptor.");
        else
        {
            Format = "RAW";
            Disk = raw;
        }
        Disk.Position = 0;
    }

    private void OpenLayer(string format, VirtualDiskLayer layer)
    {
        Own(layer);
        if (layer.NeedsParent)
            throw new NotSupportedException($"{format} differencing images require a verified parent chain.");
        Disk = Own(layer.OpenContent(null!, Ownership.None));
        Format = format;
    }

    private void OpenFlatDescriptor(FileStream descriptor)
    {
        if (descriptor.Length > 1024 * 1024)
            throw new InvalidDataException("VMDK descriptor is too large.");
        string content = Encoding.UTF8.GetString(DiskLayout.ReadAt(descriptor, 0, (int)descriptor.Length)).TrimEnd('\0', '\r', '\n');
        if (!Regex.IsMatch(content, @"(?im)^\s*parentCID\s*=\s*ffffffff\s*$") || Regex.IsMatch(content, @"(?im)^\s*parentFileNameHint"))
            throw new NotSupportedException("VMDK snapshots require a verified parent chain.");
        var extents = new List<SparseStream>();
        string directory = SourceIdentity.ResolvedDirectory(descriptor);
        foreach (string line in content.Split('\n').Select(s => s.Trim()))
        {
            if (line.Length == 0 || line.StartsWith('#') || line.Contains('=') && !Regex.IsMatch(line, "^(RW|RDONLY|NOACCESS)\\s"))
                continue;
            var match = Regex.Match(line, """^(?:RW|RDONLY)\s+(\d+)\s+FLAT\s+"([^"]+)"(?:\s+(\d+))?$""");
            if (!match.Success)
                throw new NotSupportedException("Only same-directory FLAT extents are supported in a VMDK descriptor.");
            string name = match.Groups[2].Value;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':']) >= 0 || name != Path.GetFileName(name)
                || name.EndsWith('.') || name.EndsWith(' ') || Regex.IsMatch(name, @"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new NotSupportedException("VMDK extent is outside the descriptor directory.");
            string part = Path.Combine(Path.GetDirectoryName(descriptor.Name)!, name);
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("VMDK extent links are not supported.");
            long length = checked(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 512);
            long offset = match.Groups[3].Success ? checked(long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) * 512) : 0;
            var file = Acquire(part);
            if (!SourceIdentity.ResolvedDirectory(file).Equals(directory, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("VMDK extent resolved outside the descriptor directory.");
            if (length <= 0 || offset > file.Length - length)
                throw new InvalidDataException("Incomplete VMDK extent.");
            extents.Add(Own(new SubStream(file, offset, length)));
        }
        if (extents.Count == 0)
            throw new InvalidDataException("VMDK descriptor contains no extents.");
        Format = "VMDK";
        Disk = Own(new ConcatStream(Ownership.None, extents));
    }

    private void OpenSplit(FileStream first, string ext, CancellationToken ct)
    {
        if (ext != ".000" && ext != ".001")
            throw new InvalidDataException("Select the first split RAW segment.");
        var parts = new SortedDictionary<int, string>();
        string stem = Path.GetFileNameWithoutExtension(first.Name);
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(first.Name)!))
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase))
                continue;
            string suffix = Path.GetExtension(file);
            if (suffix.Length == 4 && int.TryParse(suffix[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                parts.Add(number, file);
        }
        int next = int.Parse(ext[1..], CultureInfo.InvariantCulture);
        var slices = new List<SparseStream>();
        foreach (var (num, name) in parts)
        {
            if (num != next++)
                throw new InvalidDataException("Select the first split RAW segment and supply a contiguous set.");
            slices.Add(Own(SparseStream.FromStream(Acquire(name), Ownership.None)));
        }
        Format = "RAW (split)";
        Disk = Own(new ConcatStream(Ownership.None, slices));
    }

    internal async Task<List<object>> HashSourcesAsync(byte[] transferBuffer, IProgress<ImageReadProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (verifiedSources != null)
            return verifiedSources;
        var list = new List<object>();
        foreach (var (path, file) in files)
        {
            long position = file.Position;
            string sha256;
            try
            {
                file.Position = 0;
                sha256 = await SourceDigest.Sha256Async(file, path, transferBuffer, "hash", progress, ct).ConfigureAwait(false);
            }
            finally { file.Position = position; }
            list.Add(new
            {
                path,
                length = file.Length,
                sha256
            });
        }
        if (OperatingSystem.IsWindows())
            verifiedSources = list;
        return list;
    }

    public void Dispose()
    {
        for (int i = resources.Count - 1; i >= 0; i--)
            resources[i].Dispose();
    }
}
