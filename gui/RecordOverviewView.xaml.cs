using System.Windows;
using System.Windows.Controls;

namespace JournalDesk;

public partial class RecordOverviewView : UserControl
{
    public RecordOverviewView() => InitializeComponent();

    internal void SetTime(Entry entry, JournalTimeZone utc) => EventTime.Text = entry.DetailedTime(utc);

    internal void SetRecord(Entry entry, JournalTimeZone utc, string reasons, string location, string notes)
    {
        SetTime(entry, utc);
        Reasons.Text = reasons;
        Reasons.ToolTip = entry.RawReason;
        LocationText.Text = location;
        Notes.Text = notes;
        Notes.Visibility = notes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RawName.Text = entry.RecordedName;
        NameLabel.Visibility = RawName.Visibility = entry.Version == 4 ? Visibility.Collapsed : Visibility.Visible;
        SetPath(L.T("경로 확인 중…", "Resolving path…"), "");
        Body.ScrollToTop();
    }

    internal void SetPath(string path, string explanation, string? recordedName = null)
    {
        PathText.Text = path;
        PathBasis.IsExpanded = false;
        PathBasis.Visibility = explanation.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PathReason.Text = explanation;
        if (!string.IsNullOrEmpty(recordedName) && path.EndsWith("\\" + recordedName, StringComparison.Ordinal))
            NameLabel.Visibility = RawName.Visibility = Visibility.Collapsed;
    }
}
