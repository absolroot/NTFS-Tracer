using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JournalDesk;

internal static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Info
    {
        public IntPtr Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Display;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out Info info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    private static readonly Dictionary<(string, bool), ImageSource?> Cache = new();
    public static ImageSource? For(string name, bool folder)
    {
        var ext = Path.GetExtension(name);
        // Ask for the registered type only. Never resolve an evidence path or shell shortcut.
        if (ext.Length > 32 || ext.Any(c => !char.IsLetterOrDigit(c) && c != '.'))
            ext = "";
        var key = (folder ? "" : ext.ToLowerInvariant(), folder);
        if (Cache.TryGetValue(key, out var image))
            return image;
        SHGetFileInfo(folder ? "folder" : "file" + ext, folder ? 16u : 128u, out var info, (uint)Marshal.SizeOf<Info>(), 0x100 | 0x10);
        if (info.Icon != IntPtr.Zero)
        {
            try
            {
                image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
            }
            finally { DestroyIcon(info.Icon); }
        }
        Cache[key] = image;
        return image;
    }
}
