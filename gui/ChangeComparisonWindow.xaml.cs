using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JournalDesk;

public partial class ChangeComparisonWindow : Window
{
    private const string ParentField = "ParentFileReferenceNumber";
    private static readonly string[] Primary = [UsnHexFields.FileNameLabel, ParentField, UsnHexFields.ReasonLabel];
    private readonly List<ComparisonRow> rows = [];
    private bool refreshingFields;
    private readonly Activity activity;
    public static readonly DependencyProperty IsPathEvidenceOpenProperty = DependencyProperty.Register(
        nameof(IsPathEvidenceOpen), typeof(bool), typeof(ChangeComparisonWindow),
        new PropertyMetadata(false, (window, args) => ((ChangeComparisonWindow)window).PathPanel.Visibility = (bool)args.NewValue ? Visibility.Visible : Visibility.Collapsed));
    public bool IsPathEvidenceOpen
    {
        get => (bool)GetValue(IsPathEvidenceOpenProperty);
        set => SetValue(IsPathEvidenceOpenProperty, value);
    }
    private void TimeZoneChanged(object? sender, PropertyChangedEventArgs e) => SetEventTime();

    private void SetEventTime() => EventStamp.Text = $"{activity.Label}   ·   {activity.Records[0].DetailedTime(DisplayTimeZone.Current.Zone)}";

    protected override void OnClosed(EventArgs e)
    {
        PropertyChangedEventManager.RemoveHandler(DisplayTimeZone.Current, TimeZoneChanged, nameof(DisplayTimeZone.Code));
        base.OnClosed(e);
    }

    private sealed record PathEvidence(string Title, string Path, string Explanation);

    private sealed record ComparisonRow(string Key, string Label, HexField? Before, HexField? After,
        string BeforeText, string AfterText, string BeforeTip, string AfterTip, bool? Equal)
    {
        public bool IsParent => Key == ParentField;
        public string EvidenceTip => BeforeTip == AfterTip ? BeforeTip : L.T("변경 전   {0}\n변경 후   {1}", "Before   {0}\nAfter   {1}", BeforeTip, AfterTip);
        public bool Different => Equal == false;
        public string State => Equal switch
        {
            true => L.T("같음", "Same"),
            false => L.T("다름", "Different"),
            _ => L.T("확인 제한", "Undetermined")
        };
        public override string ToString() => $"{Label} · {BeforeText} → {AfterText} · {State}";
    }

    private sealed record Side(Entry Record, byte[] Bytes, Dictionary<string, HexField> Fields, PathResult? Path)
    {
        internal ReadOnlySpan<byte> Slice(string label)
        {
            if (!Fields.TryGetValue(label, out var field))
                throw new InvalidDataException(L.T("원본 위치 0x{0:X}에서 {1} 필드를 읽을 수 없습니다.", "Could not read {1} at source offset 0x{0:X}.", Record.Position, label));
            return Bytes.AsSpan(field.Start, field.Length);
        }
    }

    internal static bool CanCompare(Activity? activity) => activity is { Records.Count: >= 2 } &&
        Activity.RelatedNames(activity.Records[0], activity.Records[1]);

    internal ChangeComparisonWindow(FileStream source, Activity activity, PathResult? beforePath = null, PathResult? afterPath = null)
        : this(source, activity, beforePath, afterPath, null) { }

    internal ChangeComparisonWindow(FileStream source, Activity activity, PathResult? beforePath, PathResult? afterPath, byte[]? fileId)
    {
        if (!CanCompare(activity))
            throw new InvalidDataException(L.T("이 활동에는 같은 변경에 속하는 이전 이름 기록과 새 이름 기록이 모두 남아 있지 않습니다.", "This activity does not contain both records from the same rename."));
        this.activity = activity;
        var before = ReadSide(source, activity.Records[0], beforePath, fileId);
        var after = ReadSide(source, activity.Records[1], afterPath, fileId);
        if (!before.Slice("FileReferenceNumber").SequenceEqual(after.Slice("FileReferenceNumber")))
            throw new InvalidDataException(L.T("두 원본의 파일 식별자가 달라 같은 파일의 변경으로 비교할 수 없습니다.", "The file identifiers differ. These records cannot be compared as changes to the same file."));

        InitializeComponent();
        if (beforePath?.Basis == afterPath?.Basis && beforePath?.Explanation == afterPath?.Explanation)
        {
            var evidence = MakeEvidence(L.T("변경 전·후", "Before and after"), beforePath);
            if (beforePath?.Display != afterPath?.Display)
                evidence = evidence with
                {
                    Path = L.T("변경 전   {0}\n변경 후   {1}", "Before   {0}\nAfter   {1}", beforePath?.Display, afterPath?.Display)
                };
            PathNotes.ItemsSource = new[] { evidence };
        }
        else
            PathNotes.ItemsSource = new[] { MakeEvidence(L.T("변경 전", "Before"), beforePath), MakeEvidence(L.T("변경 후", "After"), afterPath) };
        double requiredHeight = 430 + 20 * Math.Ceiling(Math.Max(before.Bytes.Length, after.Bytes.Length) / 16d);
        Height = Math.Min(Math.Clamp(requiredHeight, 570, 735), SystemParameters.WorkArea.Height);
        var newName = after.Fields.GetValueOrDefault(UsnHexFields.FileNameLabel)?.Value;
        Heading.Text = newName ?? L.T("파일 이름을 확인할 수 없는 기록", "File name unavailable");
        Title = L.T("변경 전후 비교 · {0}", "Compare changes · {0}", Heading.Text);
        SetEventTime();
        PropertyChangedEventManager.AddHandler(DisplayTimeZone.Current, TimeZoneChanged, nameof(DisplayTimeZone.Code));
        Source.Text = L.T("원본   {0}", "Source   {0}", source.Name);

        // The shared table selects fields on both sides. The byte panes remain independently navigable.
        BeforeHex.ShowBytes(before.Bytes, before.Record.Position, before.Fields.Values.ToArray());
        AfterHex.ShowBytes(after.Bytes, after.Record.Position, after.Fields.Values.ToArray());
        BeforeRange.ToolTip = RecordLocation(before.Record);
        AfterRange.ToolTip = RecordLocation(after.Record);
        foreach (string key in Primary.Concat(before.Fields.Keys).Concat(after.Fields.Keys).Distinct())
        {
            var left = before.Fields.GetValueOrDefault(key);
            var right = after.Fields.GetValueOrDefault(key);
            bool? same = left == null || right == null ? null : before.Slice(key).SequenceEqual(after.Slice(key));
            var (leftText, leftTip) = Describe(before, key, left);
            var (rightText, rightTip) = Describe(after, key, right);
            rows.Add(new(key, ComparisonLabel(key), left, right,
                leftText, rightText, leftTip, rightTip, same));
        }
        bool moved = !before.Slice(ParentField).SequenceEqual(after.Slice(ParentField));
        SetRows(moved && rows[0].Equal == true ? ParentField : UsnHexFields.FileNameLabel);
        Loaded += (_, _) => { ArrangeWindow(); Fields.Focus(); };
    }

    private static string ComparisonLabel(string key) => key switch
    {
        UsnHexFields.FileNameLabel => L.T("파일 이름", "File name"),
        ParentField => L.T("상위 폴더", "Parent folder"),
        UsnHexFields.ReasonLabel => L.T("변경 사유", "Change reason"),
        _ => key
    };

    private static Side ReadSide(FileStream file, Entry record, PathResult? path, byte[]? fileId)
    {
        byte[] bytes = UsnEvidence.Read(file, record, fileId);
        return new Side(record, bytes, UsnHexFields.Decode(bytes).ToDictionary(field => field.Label), path);
    }

    private static PathEvidence MakeEvidence(string title, PathResult? path)
    {
        string basis = path?.DisplayBasis ?? L.T("폴더 자료 없음", "No folder data");
        return new(L.T("{0} 경로 근거   ·   {1}", "{0} path evidence   ·   {1}", title, basis), path?.Display ?? "",
            path?.Explanation ?? L.T("이 기록에 연결된 경로 자료가 없습니다.", "No path evidence is linked to this record."));
    }
    private static string RecordLocation(Entry record) =>
        L.T("레코드 시작 0x{0:X}   ·   전체 {1}바이트   ·   USN {2}", "Record start 0x{0:X}   ·   {1} bytes   ·   USN {2}", record.Position, record.Size, record.Usn);

    private static (string Value, string Tip) Describe(Side side, string key, HexField? field)
    {
        if (field == null)
            return (L.T("확인할 수 없음", "Unavailable"), L.T("이 기록에서 필드 범위를 확인할 수 없습니다.", "The field's byte range is unavailable in this record."));
        string value = field.Value;
        if (key == ParentField)
        {
            string? path = side.Path?.Display;
            string? name = side.Fields.GetValueOrDefault(UsnHexFields.FileNameLabel)?.Value;
            value = L.T("폴더 이름 미확인", "Unknown folder");
            if (name != null && path != null && path.EndsWith("\\" + name, StringComparison.Ordinal))
            {
                string parent = path[..^(name.Length + 1)];
                if (parent == "")
                    value = L.T("볼륨 최상위 폴더", "Volume root");
                else if (parent != "…")
                    value = side.Path?.IsEstimated == true ? L.T("{0}  (추정)", "{0}  (inferred)", parent) : parent;
            }
        }
        else if (key == UsnHexFields.ReasonLabel)
            value = side.Record.RecordLabel;
        string raw = L.T("원본 위치 0x{0:X}   ·   {1}바이트", "Source offset 0x{0:X}   ·   {1} bytes", side.Record.Position + field.Start, field.Length);
        if (key == ParentField && side.Path != null)
            return (value, side.Path.Display);
        return (value, $"{field.Value}\n{raw}");
    }

    private void ToggleFields(object sender, RoutedEventArgs e) =>
        SetRows((Fields?.SelectedItem as ComparisonRow)?.Key ?? UsnHexFields.FileNameLabel);

    private void SetRows(string preferred)
    {
        if (rows.Count == 0)
            return;
        bool all = AllFields.IsChecked == true;
        var shown = all ? rows : rows.Where(row => Primary.Contains(row.Key)).ToList();
        TableHeight.Height = new GridLength(all ? Math.Clamp(ActualHeight * 0.34, 160, 245) : 106);
        var previous = Fields.SelectedItem;
        var next = shown.Find(row => row.Key == preferred) ?? shown[0];
        refreshingFields = true;
        try
        {
            Fields.ItemsSource = shown;
            Fields.SelectedItem = next;
        }
        finally { refreshingFields = false; }
        if (!ReferenceEquals(previous, next))
            ApplySelection();
        Fields.ScrollIntoView(Fields.SelectedItem);
    }

    private void FieldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshingFields && ReferenceEquals(e.OriginalSource, Fields))
            ApplySelection();
    }
    private void RestoreField(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target && ItemsControl.ContainerFromElement(Fields, target) is DataGridRow)
            ApplySelection();
    }
    private void FieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySelection();
            e.Handled = true;
        }
    }
    private void ApplySelection()
    {
        if (Fields.SelectedItem is not ComparisonRow row)
            return;
        ShowRange(BeforeHex, BeforeMissing, BeforeRange, BeforeField, L.T("변경 전", "Before"), row.Label, row.Before);
        ShowRange(AfterHex, AfterMissing, AfterRange, AfterField, L.T("변경 후", "After"), row.Label, row.After);
    }
    private void ShowRange(HexEvidenceView view, TextBlock missing, CopyText range, CopyText title, string side, string label, HexField? field)
    {
        view.Visibility = field == null ? Visibility.Hidden : Visibility.Visible;
        missing.Visibility = field == null ? Visibility.Visible : Visibility.Collapsed;
        range.Text = field == null ? "" : L.T("원본 위치 0x{0:X}", "Source offset 0x{0:X}", view.BaseOffset + field.Start);
        string fieldName = (field?.Label == ParentField) ? L.T("상위 폴더 식별자", "Parent folder ID") : label;
        title.Text = field == null ? $"{side} · {fieldName}" : L.T("{0} {1}   ·   저장 길이 {2}바이트", "{0} {1}   ·   Stored length {2} bytes", side, fieldName, field.Length);
        title.ToolTip = field == null ? null : field.Label == UsnHexFields.FileNameLabel
            ? L.T("{0}\n파일 이름을 UTF-16LE로 저장한 {1}바이트가 강조되어 있습니다.", "{0}\nThe highlighted {1} bytes store the file name in UTF-16LE.", field.Value, field.Length)
            : L.T("{0}\n이 필드의 값을 저장한 {1}바이트가 강조되어 있습니다.", "{0}\nThe highlighted {1} bytes store this field.", field.Value, field.Length);
        if (field != null)
            view.CompareField(field, InspectBytes.IsChecked == true);
    }
    private void InspectBytesChanged(object sender, RoutedEventArgs e)
    {
        if (BeforeHex == null || AfterHex == null)
            return;
        BeforeHex.SetComparisonDetails(InspectBytes.IsChecked == true);
        AfterHex.SetComparisonDetails(InspectBytes.IsChecked == true);
    }

    private void SideChanged(object sender, RoutedEventArgs e) => ArrangeWindow();
    private void WindowResized(object sender, SizeChangedEventArgs e) => ArrangeWindow();
    private void ArrangeWindow()
    {
        if (AfterHex == null || Panes.ActualWidth <= 0)
            return;
        double tableWidth = Math.Max(BeforeHex.RequiredByteViewportWidth, AfterHex.RequiredByteViewportWidth);
        double paneInsets = Math.Max(BeforePanel.Margin.Left + BeforePanel.Margin.Right, AfterPanel.Margin.Left + AfterPanel.Margin.Right);
        bool narrow = Panes.ActualWidth < 2 * Math.Ceiling(tableWidth + paneInsets) + 1;
        SidePicker.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        BeforePanel.Visibility = !narrow || ShowBefore.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AfterPanel.Visibility = !narrow || ShowAfter.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(AfterPanel, narrow ? 0 : 2);
        RightWidth.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Gutter.Width = new GridLength(narrow ? 0 : 1);
        if (AllFields.IsChecked == true)
            TableHeight.Height = new GridLength(Math.Clamp(ActualHeight * 0.34, 160, 245));
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled)
            return;
        if (IsPathEvidenceOpen)
            IsPathEvidenceOpen = false;
        else
            Close();
        e.Handled = true;
    }
}
