using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EvidenceImage;

public static class SourceIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        internal uint Flags, CreationLow, CreationHigh, AccessLow, AccessHigh, ModifiedLow, ModifiedHigh,
            Volume, SizeHigh, SizeLow, Links, IdHigh, IdLow;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId
    {
        internal ulong Volume;
        internal Guid Id;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileId id, uint size);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool SameFile(FileStream first, FileStream second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        const int fileIdInfo = 18;
        uint size = (uint)Marshal.SizeOf<FileId>();
        if (GetFileInformationByHandleEx(first.SafeFileHandle, fileIdInfo, out var left, size) &&
            GetFileInformationByHandleEx(second.SafeFileHandle, fileIdInfo, out var right, size) && left.Id != Guid.Empty)
            return (left.Volume, left.Id) == (right.Volume, right.Id);
        return GetFileInformationByHandle(first.SafeFileHandle, out var a) && GetFileInformationByHandle(second.SafeFileHandle, out var b)
            && (a.IdHigh != 0 || a.IdLow != 0) && (a.Volume, a.IdHigh, a.IdLow) == (b.Volume, b.IdHigh, b.IdLow);
    }
    [DllImport("kernel32", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint chars, uint flags);

    internal static string ResolvedDirectory(FileStream file)
    {
        if (!OperatingSystem.IsWindows())
            return Path.GetDirectoryName(file.Name)!;
        var path = new char[32768];
        uint count = GetFinalPathNameByHandle(file.SafeFileHandle, path, (uint)path.Length, 0);
        if (count == 0 || count >= path.Length)
            throw new IOException("Cannot verify the VMDK extent directory.");
        return Path.GetDirectoryName(new string(path, 0, checked((int)count))) ?? throw new IOException("Invalid VMDK extent path.");
    }

    // This inexpensive probe/selection token is separate from the full segment SHA-256 in the extraction manifest.
    internal static string Generation(IEnumerable<FileStream> sources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in sources)
        {
            string metadata = $"{f.Name}\0{f.Length}\0{File.GetLastWriteTimeUtc(f.Name).Ticks}\0";
            if (OperatingSystem.IsWindows() && GetFileInformationByHandle(f.SafeFileHandle, out var info))
                metadata += $"{info.Volume:X8}{info.IdHigh:X8}{info.IdLow:X8}{info.CreationHigh:X8}{info.CreationLow:X8}\0";
            hash.AppendData(Encoding.UTF8.GetBytes(metadata));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
