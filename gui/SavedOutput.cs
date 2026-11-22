using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace JournalDesk;

internal static class SavedOutput
{
    /// <summary>The status is replaced as a unit when the owning view next writes Text.</summary>
    internal static void Show(TextBlock status, string path, string summary)
    {
        string name = Path.GetFileName(path);
        string command = L.T("폴더에서 보기", "Show in folder");
        var reveal = new Hyperlink(new Run(command)) { ToolTip = path, Focusable = true };
        reveal.SetResourceReference(TextElement.ForegroundProperty, "Desk.Link");
        AutomationProperties.SetName(reveal, command + " · " + name);
        reveal.Click += (_, _) =>
        {
            try
            {
                ShowInFolder(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or ArgumentException)
            {
                status.Text = L.T("파일 위치를 열 수 없습니다. {0}", "Could not show the file location. {0}", path);
            }
        };
        status.Inlines.Clear();
        status.Inlines.Add(reveal);
        status.Inlines.Add(new Run("   " + summary + "  ·  " + name) { ToolTip = path });
        status.Visibility = Visibility.Visible;
    }

    // A single absolute shell item and zero children selects the file in its parent folder.
    internal static void ShowInFolder(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(null, path);
        Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.GetFullPath(path), IntPtr.Zero, out var item, 0, out _));
        try
        {
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
        }
        finally { Marshal.FreeCoTaskMem(item); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string path, IntPtr context, out IntPtr item, uint attributes, out uint result);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
}
