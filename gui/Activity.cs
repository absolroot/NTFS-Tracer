namespace JournalDesk;

public sealed class Activity : System.ComponentModel.INotifyPropertyChanged
{
    public List<Entry> Records { get; } = [];
    public string Label { get; private set; } = "";
    public string Description { get; internal set; } = "";
    public string Glyph { get; private init; } = "\uE9D9";
    public string Clock => Records[0].Time;
    public string StartTimeDetail => Records[0].DetailedTime(DisplayTimeZone.Current.Zone);
    public string ClockDetail => string.Join(Environment.NewLine, Records.Select(record =>
        $"{record.DetailedTime(DisplayTimeZone.Current.Zone)}   ·   USN {record.Usn}   ·   {record.RecordLabel}"));
    public string Date => Records[0].Day;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshClock()
    {
        PropertyChanged?.Invoke(this, new(string.Empty));
    }
    public string RecordCount => L.T("{0}건", Records.Count == 1 ? "{0} record" : "{0} records", Records.Count);
    public bool Moved => Records.Count > 1 && !Records[0].Parent.AsSpan().SequenceEqual(Records[1].Parent);
    public Entry? PairedName => Records.Count >= 2 && RelatedNames(Records[0], Records[1]) ? Records[1] : null;
    public Entry? RenamedTo => Records.Count >= 2 && !Moved && RelatedNames(Records[0], Records[1])
        && Records[0].Name != Records[1].Name ? Records[1] : null;
    public bool NameOrLocation => Records.Any(r => (r.Reason & 0x3000) != 0);
    public bool Lifecycle => Records.Any(r => (r.Reason & 0x300) != 0);
    public string DateHeading { get; set; } = "";
    public bool ShowDate => DateHeading.Length != 0;
    public override string ToString() => $"{Clock} {Label} {Description}";

    private static Activity Describe(Entry e)
    {
        if (e.Version == 4)
            return new Activity { Label = L.T("변경된 바이트 범위", "Changed byte ranges"), Description = e.Extents.Length == 0 ? L.T("기록된 범위 없음", "No ranges recorded") : L.T("시작 위치 + 길이   ", "Start + length   ") + e.Extents, Glyph = "\uE70F" };
        var (label, desc, icon) = e.Reason switch
        {
            var r when (r & 0x200) != 0 => (L.T("삭제 기록", "Deletion record"), e.Name, "\uE74D"),
            var r when (r & 0x100) != 0 => (L.T("생성 기록", "Creation record"), e.Name, "\uE7C3"),
            var r when (r & 0x1000) != 0 => (L.T("이전 이름 기록", "Old name record"), e.Name, "\uE8AC"),
            var r when (r & 0x2000) != 0 => (L.T("새 이름 기록", "New name record"), e.Name, "\uE8AC"),
            var r when (r & 7) != 0 => (e.Title, e.Name, "\uE70F"),
            var r when (r & 0x200070) != 0 => (e.Title, e.Name, "\uE8B7"),
            var r when (r & 0x8000) != 0 => (e.Title, e.Name, "\uE946"),
            0x80000000 => (e.Title, e.Name, "\uE8E5"),
            _ => (e.Title, e.Name, "\uE9D9")
        };
        return new Activity { Label = label, Description = desc, Glyph = icon };
    }
    internal static bool RelatedNames(Entry a, Entry b) =>
        a.Version != 4 && b.Version != 4 && a.Segment == b.Segment && a.Reason == 0x1000 && b.Reason == 0x2000
        && a.Timestamp.HasValue && a.Timestamp == b.Timestamp && b.Usn > a.Usn;
    private static bool IsClosingSummary(Entry a, Entry b) =>
        a.Version != 4 && b.Version != 4 && a.Segment == b.Segment && a.Timestamp.HasValue && a.Timestamp == b.Timestamp
        && b.Usn > a.Usn && (a.Reason & 0x80000000) == 0 && b.Reason == (a.Reason | 0x80000000)
        && a.Name == b.Name && a.Parent.AsSpan().SequenceEqual(b.Parent);

    private static string PairLabel(bool moved, bool renamed) =>
        moved ? (renamed ? L.T("폴더 이동 및 이름 변경", "Moved and renamed") : L.T("다른 폴더로 이동", "Moved to another folder")) :
        (renamed ? L.T("파일 이름 변경", "File renamed") : L.T("이름 변경 기록", "Rename record"));

    internal void RefreshLanguage()
    {
        if (PairedName is { } paired)
            Label = PairLabel(Moved, Records[0].Name != paired.Name);
        else
        {
            var translated = Describe(Records[0]);
            Label = translated.Label;
            Description = translated.Description;
        }
        RefreshClock();
    }

    public static List<Activity> From(List<Entry> entries)
    {
        var output = new List<Activity>();
        string day = "";
        for (int i = 0; i < entries.Count; i++)
        {
            var first = entries[i];
            var result = Describe(first);
            if (i + 1 < entries.Count && RelatedNames(first, entries[i + 1]))
            {
                var next = entries[++i];
                bool moved = !first.Parent.AsSpan().SequenceEqual(next.Parent);
                bool renamed = first.Name != next.Name;
                result = new Activity
                {
                    Label = PairLabel(moved, renamed),
                    Description = renamed ? $"{first.Name}   →   {next.Name}" : first.Name,
                    Glyph = moved ? "\uE8DE" : "\uE8AC"
                };
                result.Records.Add(first);
                result.Records.Add(next);
            }
            else
                result.Records.Add(first);
            if (i + 1 < entries.Count && IsClosingSummary(entries[i], entries[i + 1]))
                result.Records.Add(entries[++i]);
            if (day != first.Day)
            {
                day = first.Day;
                result.DateHeading = day;
            }
            output.Add(result);
        }
        return output;
    }
}
