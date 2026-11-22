namespace EvidenceImage;

internal enum ImageSignature
{
    Unknown, Ntfs, Ewf, Ewf2, LogicalEwf, Aff, Qcow, Vhd, Vhdx, Vmdk, VmdkDescriptor
}

public static class ImageFileTypes
{
    public const string PickerPatterns = "*.raw;*.dd;*.img;*.000;*.001;*.e01;*.ex01;*.l01;*.lx01;*.qcow;*.qcow2;*.qcow3;*.vhd;*.vhdx;*.vmdk;*.aff";
    private static readonly HashSet<string> PickerExtensions = PickerPatterns.Split(';')
        .Select(pattern => pattern[1..]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(FileStream file, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(file);
        string extension = Path.GetExtension(file.Name);
        return PickerExtensions.Contains(extension) || extension.Equals(".aff4", StringComparison.OrdinalIgnoreCase)
            || SegmentFamily(extension) != null || Detect(file, header) != ImageSignature.Unknown;
    }

    public static string? FirstSelectedSegment(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0 || string.IsNullOrEmpty(paths[0]))
            return null;
        string extension = Path.GetExtension(paths[0]);
        string? family = SegmentFamily(extension);
        if (family == null)
            return null;
        string stem = paths[0][..^extension.Length];
        string? first = null;
        foreach (string path in paths)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            string suffix = Path.GetExtension(path);
            if (!string.Equals(family, SegmentFamily(suffix), StringComparison.OrdinalIgnoreCase) ||
                !path.AsSpan(0, path.Length - suffix.Length).Equals(stem, StringComparison.OrdinalIgnoreCase))
                return null;
            bool startsSet = family == "." ? suffix == ".001" : suffix.EndsWith("01", StringComparison.Ordinal);
            if (suffix == ".000" || first == null && startsSet)
                first = path;
        }
        return first;
    }

    internal static bool IsSplitRaw(string extension) => extension.Length == 4 && extension[0] == '.' &&
        char.IsAsciiDigit(extension[1]) && char.IsAsciiDigit(extension[2]) && char.IsAsciiDigit(extension[3]);

    internal static bool RequiresContainerSignature(string extension) =>
        extension is ".aff" or ".afd" or ".afm" or ".aff4" or ".vhd" or ".vhdx" or ".vmdk" or ".qcow" or ".qcow2" or ".qcow3" ||
        System.Text.RegularExpressions.Regex.IsMatch(extension, @"^\.[el]x?[0-9a-z]{2}$");

    private static string? SegmentFamily(string extension)
    {
        if (IsSplitRaw(extension))
            return ".";
        if (extension.Length is not (4 or 5) || !char.IsAsciiDigit(extension[^1]) || !char.IsAsciiDigit(extension[^2]))
            return null;
        string family = extension[..^2].ToLowerInvariant();
        return family is ".e" or ".ex" or ".l" or ".lx" ? family : null;
    }

    internal static ImageSignature Header(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("LVF"u8) || data.StartsWith("LEF2"u8))
            return ImageSignature.LogicalEwf;
        if (data.StartsWith("EVF2"u8))
            return ImageSignature.Ewf2;
        if (data.StartsWith("EVF"u8))
            return ImageSignature.Ewf;
        if (data.StartsWith("AFF10\r\n\0"u8))
            return ImageSignature.Aff;
        if (data.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data) == 0x514649fb)
            return ImageSignature.Qcow;
        if (data.StartsWith("vhdxfile"u8))
            return ImageSignature.Vhdx;
        if (data.StartsWith("KDMV"u8))
            return ImageSignature.Vmdk;
        if (data.StartsWith("# Disk DescriptorFile"u8))
            return ImageSignature.VmdkDescriptor;
        if (data.StartsWith("conectix"u8))
            return ImageSignature.Vhd;
        return data.Length >= 512 && data[510] == 0x55 && data[511] == 0xaa && DiskLayout.IsNtfs(data)
            ? ImageSignature.Ntfs : ImageSignature.Unknown;
    }

    internal static ImageSignature Detect(FileStream file, ReadOnlySpan<byte> header)
    {
        var type = Header(header);
        if (type is not (ImageSignature.Unknown or ImageSignature.Ntfs) || file.Length < 512)
            return type;
        long original = file.Position;
        try
        {
            file.Position = file.Length - 512;
            Span<byte> cookie = stackalloc byte[8];
            return file.ReadAtLeast(cookie, cookie.Length, false) == cookie.Length && cookie.SequenceEqual("conectix"u8)
                ? ImageSignature.Vhd : type;
        }
        finally { file.Position = original; }
    }
}
