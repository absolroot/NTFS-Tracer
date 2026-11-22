using System.Windows;
using System.Windows.Input;

namespace JournalDesk;

public partial class EvidenceWindow : Window
{
    internal EvidenceWindow(string path, Entry entry, byte[] data, int? cursor = null)
        : this(path, entry, data, cursor, null) { }

    internal EvidenceWindow(string path, Entry entry, byte[] data, int? cursor, int? selectionAnchor)
    {
        InitializeComponent();
        Title = $"{entry.RecordedName} · {entry.RecordLabel} · Hex";
        RecordName.Text = entry.RecordedName;
        RecordLocation.Text = $"{entry.RecordLabel}   ·   USN {entry.Usn}";
        ByteLocation.Text = entry.RawLocation;
        Origin.Text = path;
        UsnEvidence.Display(Viewer, data, entry, cursor);
        if (selectionAnchor is int anchor && cursor is int caret)
            Viewer.SelectRange(anchor, caret);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                e.Handled = true;
                Close();
            }
        };
    }
}
