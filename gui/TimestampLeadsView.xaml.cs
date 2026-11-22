using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JournalDesk;

internal sealed record TimestampLead(long Id, int FieldIndex, string Lsn, long? Slot, long Old, long New, byte[] OldLocations, byte[] NewLocations)
{
    internal static string FieldLabel(int field) => field switch
    {
        0 => L.T("만든 시각", "Created"),
        1 => L.T("내용 수정 시각", "Content modified"),
        2 => L.T("메타데이터 변경 시각", "Metadata changed"),
        _ => L.T("마지막 접근 시각", "Last accessed")
    };

    private static string Stamp(long value) => DisplayTimeZone.Current.Zone.At(DateTimeOffset.UnixEpoch.AddTicks(value))
        .ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture);
    public string Field => FieldLabel(FieldIndex);
    public string LsnText => "0x" + Lsn;
    public string Before => Stamp(Old);
    public string After => Stamp(New);
    public string Difference => (New < Old ? "−" : "+") + TimeSpan.FromTicks(Math.Abs(New - Old)).ToString("c", CultureInfo.InvariantCulture);
}

internal sealed record TimestampFragment(long Offset, int Start, int Length)
{
    public string OffsetText => $"0x{Offset:X}–0x{Offset + Length - 1:X}";
}

internal sealed record TimestampEvidence(string Role, string Lsn, string Field, long FileTime, IReadOnlyList<TimestampFragment> Fragments)
{
    public string PositionText => string.Join(" · ", Fragments.Select(x => x.OffsetText));

    internal static TimestampEvidence Create(TimestampLead lead, bool undo)
    {
        var offsets = undo ? lead.OldLocations : lead.NewLocations;
        List<TimestampFragment> fragments = [];
        for (int i = 0; i < 8; i++)
        {
            long offset = BinaryPrimitives.ReadInt64LittleEndian(offsets.AsSpan(i * 8, 8));
            if (fragments.Count > 0 && offset == fragments[^1].Offset + fragments[^1].Length)
                fragments[^1] = fragments[^1] with
                {
                    Length = fragments[^1].Length + 1
                };
            else
                fragments.Add(new(offset, i, 1));
        }
        long stamp = DateTimeOffset.UnixEpoch.AddTicks(undo ? lead.Old : lead.New).ToFileTime();
        return new(undo ? L.T("이전 (Undo)", "Before (Undo)") : L.T("이후 (Redo)", "After (Redo)"),
            lead.LsnText, lead.Field, stamp, fragments);
    }
}

public partial class TimestampLeadsView : UserControl
{
    private const int PageSize = 200;
    private readonly Func<string, Task> openLsn;
    private readonly Stack<(long Id, int Field)> history = new();
    private TransactionQuery? query;
    private string? path;
    private string? hash;
    private CancellationTokenSource? reading;
    private (long Id, int Field) cursor;
    private TimestampLead? next;

    internal TimestampLeadsView(Func<string, Task> openLsn)
    {
        this.openLsn = openLsn;
        InitializeComponent();
        Loaded += (_, _) => DisplayTimeZone.Current.PropertyChanged += TimeZoneChanged;
        Unloaded += (_, _) => DisplayTimeZone.Current.PropertyChanged -= TimeZoneChanged;
        RefreshLanguage();
    }

    internal void SetSource(TransactionQuery? backend, string? source, string? digest)
    {
        if (path == source && hash == digest && (query == null) == (backend == null))
            return;
        reading?.Cancel();
        reading = null;
        query = backend;
        path = source;
        hash = digest;
        history.Clear();
        cursor = default;
        next = null;
        Candidates.ItemsSource = null;
        Range.Text = "";
        Source.Text = source ?? "";
        Source.ToolTip = source == null ? null : $"{source}\nSHA-256   {digest}";
        UpdateCommands();
        if (IsVisible)
            _ = Load();
    }

    private void TimeZoneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Candidates.Items.Refresh();

    internal void CloseWithOwner() => reading?.Cancel();

    internal void RefreshLanguage()
    {
        FieldColumn.Header = L.T("시각 필드", "Timestamp field");
        BeforeColumn.Header = L.T("이전 값 (Undo)", "Before (Undo)");
        AfterColumn.Header = L.T("이후 값 (Redo)", "After (Redo)");
        DifferenceColumn.Header = L.T("차이", "Difference");
        OpenHex.Content = L.T("Hex 열기", "Open Hex");
        OpenLsn.Content = L.T("LSN 열기", "Open LSN");
        AutomationProperties.SetName(Candidates, L.T("파일 시각 변경 후보", "File timestamp changes"));
        AutomationProperties.SetName(Evidence, L.T("시각 변경 근거", "Timestamp evidence"));
        AutomationProperties.SetName(Fragments, L.T("원본 바이트 범위", "Source byte range"));
        Criterion.Text = L.T("파일에 저장된 시각이 바뀌었고, 이전 값과 이후 값을 모두 읽을 수 있을 때 표시합니다. 차이는 이후 값에서 이전 값을 뺀 값입니다. 어떤 종류의 파일 속성인지는 기록 안의 위치와 값으로 추정합니다.",
            "Shows a file timestamp when the log contains two different values for it. The difference is the new value minus the old value. The type of file attribute is inferred from its location and values.");
        Candidates.Items.Refresh();
        UpdateEvidence();
    }

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && query != null && Candidates.ItemsSource == null)
            _ = Load();
    }

    private async Task Load()
    {
        if (query is not { } backend || reading != null)
            return;
        using var stop = new CancellationTokenSource();
        reading = stop;
        UpdateCommands();
        Empty.Text = L.T("파일 시각 변경을 읽고 있습니다…", "Reading file timestamp changes…");
        try
        {
            var rows = await Task.Run(() => backend.TimestampLeads(cursor.Id, cursor.Field, PageSize + 1, stop.Token), stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            next = rows.Count > PageSize ? rows[PageSize - 1] : null;
            if (next != null)
                rows.RemoveAt(PageSize);
            Candidates.ItemsSource = rows;
            int start = history.Count * PageSize;
            Range.Text = rows.Count > 0 ? $"{start + 1:N0}–{start + rows.Count:N0}" : "";
            Candidates.SelectedIndex = rows.Count == 0 ? -1 : 0;
            Empty.Text = rows.Count == 0
                ? L.T("전후 값을 모두 확인할 수 있는 시각 변경이 없습니다.", "No timestamp changes with both values available.") : "";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!stop.IsCancellationRequested)
                Empty.Text = L.T("시각 변경을 읽지 못했습니다. ", "Could not read timestamp changes. ") + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(reading, stop))
            {
                reading = null;
                UpdateCommands();
            }
        }
    }

    private async void PreviousClick(object sender, RoutedEventArgs e)
    {
        if (history.TryPop(out cursor))
            await Load();
    }

    private async void NextClick(object sender, RoutedEventArgs e)
    {
        if (next is not { } mark)
            return;
        history.Push(cursor);
        cursor = (mark.Id, mark.FieldIndex);
        await Load();
    }

    private void UpdateCommands()
    {
        Previous.IsEnabled = reading == null && history.Count > 0;
        Next.IsEnabled = reading == null && next != null;
        OpenLsn.IsEnabled = OpenHex.IsEnabled = reading == null && Evidence.SelectedItem is TimestampEvidence;
    }

    private void CandidateChanged(object sender, SelectionChangedEventArgs e) => UpdateEvidence();

    private void UpdateEvidence()
    {
        int selected = Evidence.SelectedIndex;
        Evidence.ItemsSource = Candidates.SelectedItem is TimestampLead lead
            ? new[] { TimestampEvidence.Create(lead, true), TimestampEvidence.Create(lead, false) } : null;
        if (Evidence.Items.Count > 0)
            Evidence.SelectedIndex = Math.Max(selected, 0);
        UpdateCommands();
    }

    private void EvidenceChanged(object sender, SelectionChangedEventArgs e)
    {
        var evidence = Evidence.SelectedItem as TimestampEvidence;
        Fragments.ItemsSource = evidence?.Fragments;
        Fragments.Visibility = evidence?.Fragments.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Fragments.SelectedIndex = evidence == null ? -1 : 0;
        if (evidence == null)
        {
            Raw.ClearView();
            HexLabel.Text = "";
        }
        UpdateCommands();
    }

    private void FragmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (path == null || Evidence.SelectedItem is not TimestampEvidence evidence || Fragments.SelectedItem is not TimestampFragment fragment)
            return;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            file.Position = fragment.Offset;
            byte[] bytes = new byte[fragment.Length];
            file.ReadExactly(bytes);
            Span<byte> expected = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(expected, evidence.FileTime);
            if (!bytes.AsSpan().SequenceEqual(expected.Slice(fragment.Start, fragment.Length)))
                throw new InvalidDataException(L.T("원본 바이트와 색인 값이 일치하지 않습니다.", "Source bytes do not match the index."));
            HexLabel.Text = $"$LogFile · {evidence.Role} · {fragment.OffsetText}";
            IReadOnlyList<HexField> fields = bytes.Length == 8
                ? [new(0, bytes.Length, evidence.Field, evidence.FileTime.ToString(CultureInfo.InvariantCulture))]
                : [];
            Raw.ShowBytes(bytes, fragment.Offset, fields);
            Raw.SelectRange(0, bytes.Length - 1);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Raw.ClearView(ex.Message);
            HexLabel.Text = "";
        }
    }

    private async void OpenLsnClick(object sender, RoutedEventArgs e)
    {
        if (Candidates.SelectedItem is TimestampLead lead)
            await openLsn(lead.Lsn);
    }

    internal void FocusEvidence()
    {
        EvidenceTab.IsSelected = true;
        Evidence.Focus();
    }

    private void OpenHexClick(object sender, RoutedEventArgs e) => Raw.FocusHexColumn();

    private void OpenHexFromKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Evidence.SelectedItem != null)
        {
            Raw.FocusHexColumn();
            e.Handled = true;
        }
    }

    private void OpenHexFromMouse(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Evidence, e.OriginalSource as DependencyObject) is DataGridRow)
            Raw.FocusHexColumn();
    }
}

