using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JournalDesk;

internal sealed class NeighborhoodRow(GlobalLogRow item, Entry anchor) : INotifyPropertyChanged
{
    public GlobalLogRow Item { get; private set; } = item;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetTimeZone(JournalTimeZone utc)
    {
        if (Item.TimeZone == utc)
            return;
        Item = Item with
        {
            TimeZone = utc
        };
        PropertyChanged?.Invoke(this, new(nameof(Item)));
    }

    public bool IsAnchor => Item.Position == anchor.Position;
    public string Relative => FormatDistance(false);
    public string RelativeDetail => FormatDistance(true);
    public override string ToString() => $"{Relative} · {Item.TimeText} · {Item.Name} · {Item.Title} · USN {Item.Usn}";

    private string FormatDistance(bool precise)
    {
        if (IsAnchor)
            return L.T("기준", "Reference");
        if (anchor.Timestamp is long center && Item.Timestamp is long time)
        {
            decimal seconds = ((decimal)time - center) / TimeSpan.TicksPerSecond;
            if (seconds == 0)
                return L.T("같은 시각", "Same time");
            if (!precise && Math.Abs(seconds) < 0.001m)
                return seconds < 0 ? L.T("<1ms 이전", "<1 ms before") : L.T("<1ms 이후", "<1 ms after");
            return seconds.ToString(precise ? "+0.0000000;-0.0000000" : "+0.000;-0.000", CultureInfo.InvariantCulture) + L.T("초", "s");
        }
        return (Item.Usn, Item.Position).CompareTo((anchor.Usn, anchor.Position)) < 0 ? L.T("이전 기록", "Earlier records") : L.T("이후 기록", "Later records");
    }
}

public sealed partial class NeighborhoodWindow : Window, IDisposable
{
    private readonly Entry anchor;
    private readonly byte[]? anchorFile;
    private JournalTimeZone utc;
    private readonly FileStream original;
    private readonly NeighborhoodQuery query;
    private int radius;
    private bool ready;
    private CancellationTokenSource? loading;
    private NeighborhoodPage? displayed;

    public NeighborhoodWindow(Journal journal, string originalPath, Entry anchor, JournalTimeZone? utc = null, byte[]? anchorFile = null)
    {
        this.anchor = anchor;
        this.anchorFile = anchorFile;
        query = new NeighborhoodQuery(journal.DbPath);
        InitializeComponent();
        original = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Title = L.T("이 사건 전후 보기 · ", "Surrounding records · ") + anchor.RecordedName;
        AnchorTitle.Text = $"{anchor.RecordedName}   ·   {anchor.RecordLabel}";
        SetTimeZone(utc ?? DisplayTimeZone.Current.Zone);
        PropertyChangedEventManager.AddHandler(DisplayTimeZone.Current, TimeZoneChanged, nameof(DisplayTimeZone.Code));
        AnchorInfo.ToolTip = originalPath;
        if (!anchor.Timestamp.HasValue)
        {
            RangeLabel.Text = L.T("앞뒤 기록 수", "Records on each side");
            Range1.Content = L.T("각 5건", "5 on each side");
            Range5.Content = L.T("각 25건", "25 on each side");
            Range30.Content = L.T("각 100건", "100 on each side");
        }
        Range5.IsChecked = true;
        radius = anchor.Timestamp.HasValue ? 5 : 25;
        Loaded += async (_, _) => { ready = true; await LoadPage(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    private void TimeZoneChanged(object? sender, PropertyChangedEventArgs e) => SetTimeZone(DisplayTimeZone.Current.Zone);

    private void SetTimeZone(JournalTimeZone useUtc)
    {
        utc = useUtc;
        string zone = utc.Code;
        TimeColumn.Header = L.T("기록 시각 ({0})", "Record time ({0})", zone);
        string anchorTime = anchor.Timestamp.HasValue
            ? anchor.DetailedTime(utc)
            : L.T("시각이 없어 같은 저널 구간의 기록을 USN 순서로 표시합니다.", "No timestamp. Records from the same journal segment are listed in USN order.");
        AnchorInfo.Text = $"{anchorTime}   ·   USN {anchor.Usn}   ·   {Path.GetFileName(original.Name)} + 0x{anchor.Position:X}";
        foreach (var row in Rows.Items.OfType<NeighborhoodRow>())
            row.SetTimeZone(utc);
    }

    public void Dispose() => Close();

    protected override void OnClosed(EventArgs e)
    {
        ready = false;
        PropertyChangedEventManager.RemoveHandler(DisplayTimeZone.Current, TimeZoneChanged, nameof(DisplayTimeZone.Code));
        loading?.Cancel();
        original.Dispose();
        base.OnClosed(e);
    }

    private async void RangeChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || sender is not RadioButton button)
            return;
        radius = anchor.Timestamp.HasValue
            ? button == Range1 ? 1 : button == Range5 ? 5 : 30
            : button == Range1 ? 5 : button == Range5 ? 25 : 100;
        displayed = null;
        await LoadPage();
    }

    private int pageSize = PageSizeOption.Preferred;

    private async void PageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedValue: int size } || size == pageSize)
            return;
        long ordinal = (displayed?.Number ?? 0) * pageSize + Math.Max(0, Rows.SelectedIndex);
        var selected = Rows.SelectedItem as NeighborhoodRow;
        PageSizeOption.Remember(size);
        pageSize = size;
        if (ready)
            await LoadPage(ordinal / size, selected?.Item.Position);
    }

    private async Task LoadPage(long? page = null, long? selectedPosition = null)
    {
        loading?.Cancel();
        using var request = new CancellationTokenSource();
        loading = request;
        var previousCounts = displayed?.Counts;
        Rows.ItemsSource = null;
        ToAnchor.IsEnabled = Prev.IsEnabled = Next.IsEnabled = OpenOriginal.IsEnabled = false;
        Counts.Text = L.T("주변 기록을 찾고 있습니다…", "Finding surrounding records…");
        try
        {
            var result = await query.ReadAsync(anchor, radius, utc, request.Token, page, previousCounts, pageSize);
            if (!ready || request.IsCancellationRequested)
                return;
            displayed = result;
            var rows = result.Rows.Select(item => new NeighborhoodRow(item.TimeZone == utc ? item : item with { TimeZone = utc }, anchor)).ToArray();
            Rows.ItemsSource = rows;
            var focus = rows.FirstOrDefault(row => row.Item.Position == selectedPosition)
                ?? rows.FirstOrDefault(row => row.IsAnchor) ?? rows.FirstOrDefault();
            Rows.SelectedItem = focus;
            if (focus != null)
                Rows.ScrollIntoView(focus);
            long first = result.Number * pageSize + 1;
            long last = first + rows.Length - 1;
            Counts.Text = L.T("기록 {0:N0}건   ·   파일·폴더 {1:N0}개", "{0:N0} records   ·   {1:N0} files and folders", result.Counts.Records, result.Counts.Files);
            if (result.Counts.Records > pageSize)
                Counts.Text += L.T("   ·   {0:N0}–{1:N0}번째", "   ·   {0:N0}–{1:N0}", first, last);
            Prev.IsEnabled = result.Number > 0;
            Next.IsEnabled = last < result.Counts.Records;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ready && !request.IsCancellationRequested)
            {
                displayed = null;
                Counts.Text = L.T("주변 기록을 열지 못했습니다. ", "Could not open surrounding records. ") + ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(loading, request))
                loading = null;
        }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Rows.SelectedItem as NeighborhoodRow;
        OpenOriginal.IsEnabled = row != null;
        ToAnchor.IsEnabled = displayed != null && Rows.Items.Count != 0 && row?.IsAnchor != true;
        string relation = row == null || row.IsAnchor || anchorFile == null ? "" : row.Item.FileId.AsSpan().SequenceEqual(anchorFile)
            ? L.T("기준 기록과 같은 파일", "Same file as reference") + "   ·   "
            : L.T("기준 기록과 다른 파일", "Different file from reference") + "   ·   ";
        SelectionInfo.Text = row == null ? "" : $"{relation}{row.Item.Name}   ·   {row.Item.Record.RawLocation}";
        SelectionInfo.ToolTip = row == null ? null : SelectionInfo.Text + "\nFileReferenceNumber   " + Convert.ToHexString(row.Item.FileId);
    }

    private async void PreviousPage(object sender, RoutedEventArgs e)
    {
        if (displayed is { Number: > 0 } page)
            await LoadPage(page.Number - 1);
    }

    private async void NextPage(object sender, RoutedEventArgs e)
    {
        if (displayed != null && Next.IsEnabled)
            await LoadPage(displayed.Number + 1);
    }

    private async void BackToAnchor(object sender, RoutedEventArgs e)
    {
        var row = Rows.Items.OfType<NeighborhoodRow>().FirstOrDefault(item => item.IsAnchor);
        if (row == null)
        {
            await LoadPage();
            if (!ready || Rows.SelectedItem is not NeighborhoodRow { IsAnchor: true } selected)
                return;
            row = selected;
        }
        Rows.SelectedItem = row;
        Rows.ScrollIntoView(row);
        Rows.UpdateLayout();
        if (Rows.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)
            container.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OpenBytes(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not NeighborhoodRow row)
            return;
        try
        {
            var entry = row.Item.Record;
            byte[] bytes = UsnEvidence.Read(original, entry, row.Item.FileId);
            new EvidenceWindow(original.Name, entry, bytes) { Owner = this }.Show();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            SelectionInfo.Text = L.T("선택한 기록의 원본을 열지 못했습니다. ", "Could not open the selected source record. ") + ex.Message;
        }
    }

    private void OpenRow(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Rows, source) is DataGridRow)
            OpenBytes(sender, e);
    }

    private void GridKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Rows.SelectedItem != null)
        {
            e.Handled = true;
            OpenBytes(sender, e);
        }
    }
}
