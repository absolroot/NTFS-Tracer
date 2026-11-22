using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace JournalDesk;

public partial class TrackerResultsWindow : Window
{
    private int pageSize = PageSizeOption.Preferred;
    private readonly string path;
    private readonly CancellationToken lifetime;
    private CancellationTokenSource? reading;
    private TrackerResult? result;
    private int page;
    private bool saving;
    private TrackerOrder? sort;
    private readonly HashSet<Task> active = [];
    private readonly DispatcherTimer typing = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public TrackerResultsWindow(string path)
    {
        InitializeComponent();
        lifetime = WindowLifetime.UntilClosed(this);
        this.path = Path.GetFullPath(path);
        Title = Path.GetFileName(path) + L.T(" · Tracker 결과", " · Tracker results");
        SourceTitle.Text = Path.GetFileName(path);
        SourceTitle.ToolTip = this.path;
        typing.Tick += async (_, _) => { typing.Stop(); await Refresh(); };
        Loaded += Load;
        Closed += async (_, _) =>
        {
            typing.Stop();
            reading?.Cancel();
            try
            {
                await Task.WhenAll(active.ToArray());
            }
            catch (Exception) { }
            result?.Dispose();
        };
    }
    private async Task<T> Work<T>(Func<T> job, CancellationToken token)
    {
        var task = Task.Run(job, token);
        active.Add(task);
        try
        {
            return await task;
        }
        finally { active.Remove(task); }
    }
    private async void Load(object sender, RoutedEventArgs e)
    {
        var token = lifetime;
        try
        {
            var loaded = await Work(() => TrackerResult.Load(path, token), token);
            if (token.IsCancellationRequested)
            {
                loaded.Dispose();
                return;
            }
            result = loaded;
            Origin.ToolTip = L.T("{0}   ·   읽기 전용   ·   시각과 경로는 Tracker에 저장된 값입니다. 원본 로그와 대조하려면 추출 파일을 열어 주세요.", "{0}   ·   Read only   ·   Times and paths come from Tracker. Open the extracted files to verify them against the source logs.", result.Format);
            Origin.Text = result.Format + L.T(" · 읽기 전용", " · Read only");
            Tables.ItemsSource = result.Tables;
            Tables.SelectedIndex = 0;
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) { Origin.Text = L.T("열기 실패", "Open failed"); Status.Text = ex.Message; Search.IsEnabled = Tables.IsEnabled = false; } }
    }
    private void Shortcuts(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Search.Focus();
            Search.SelectAll();
            e.Handled = true;
        }
    }
    private sealed record FieldValue(string Name, TrackerCell Cell, string Storage);

    private async void TableChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tables.SelectedItem is not TrackerTable table)
            return;
        sort = null;
        Rows.Columns.Clear();
        for (int i = 0; i < table.Headers.Length; i++)
        {
            string item = $"[{i}]";
            var style = new Style(typeof(TextBlock), (Style)FindResource("Desk.LogText"));
            style.Setters.Add(new Setter(TextBlock.ToolTipProperty, new Binding(item + ".Display")));
            var marker = new DataTrigger { Binding = new Binding(item + ".IsPlaceholder"), Value = true };
            marker.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
            style.Triggers.Add(marker);
            var col = new DataGridTextColumn
            {
                Header = table.DisplayHeaders[i],
                SortMemberPath = item,
                Binding = new Binding(item + ".Preview"),
                ClipboardContentBinding = new Binding(item + ".Value"),
                ElementStyle = style,
                Width = DataGridLength.Auto,
                MinWidth = 80,
                MaxWidth = 360
            };
            Rows.Columns.Add(col);
        }
        typing.Stop();
        page = 0;
        await Refresh();
    }
    private void ClearTrackerSearch(object sender, RoutedEventArgs e)
    {
        Search.Clear();
        Search.Focus();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (result == null)
            return;
        reading?.Cancel();
        Rows.ItemsSource = null;
        Prev.IsEnabled = Next.IsEnabled = Save.IsEnabled = false;
        page = 0;
        typing.Stop();
        typing.Start();
    }
    private async void PageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedValue: int size } || size == pageSize)
            return;
        long ordinal = (long)page * pageSize + Math.Max(0, Rows.SelectedIndex);
        PageSizeOption.Remember(size);
        pageSize = size;
        page = checked((int)(ordinal / size));
        await Refresh(retainedOrdinal: ordinal);
    }

    private async void Previous(object sender, RoutedEventArgs e)
    {
        if (page > 0)
        {
            page--;
            await Refresh();
        }
    }
    private async void Following(object sender, RoutedEventArgs e)
    {
        page++;
        await Refresh();
    }
    private async void SortRows(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        int index = Rows.Columns.IndexOf(e.Column);
        var previous = sort;
        long? ordinal = Rows.SelectedIndex < 0 ? null : (long)page * pageSize + Rows.SelectedIndex;
        var direction = SortCycle.Next(previous?.Column == index, previous?.Descending == true);
        sort = direction.HasValue ? new TrackerOrder(index, direction.Value) : null;
        page = 0;
        await Refresh(previous, ordinal);
    }
    private async Task Refresh(TrackerOrder? previous = null, long? ordinal = null, long? retainedOrdinal = null)
    {
        if (result is not { } data || Tables.SelectedItem is not TrackerTable table)
            return;
        reading?.Cancel();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        reading = cancel;
        int requested = page;
        string term = Search.Text;
        var order = sort;
        Rows.ItemsSource = null;
        Prev.IsEnabled = Next.IsEnabled = Save.IsEnabled = false;
        Status.Text = L.T("결과를 읽고 있습니다…", "Reading results…");
        try
        {
            long selected = retainedOrdinal ?? 0;
            if (ordinal is long pos && order != null)
            {
                selected = await Work(() => data.LocateAfterSort(table, term, pos, previous, order, cancel.Token), cancel.Token);
                if (cancel.IsCancellationRequested)
                    return;
                requested = page = checked((int)(selected / pageSize));
            }
            var got = await Work(() => data.Read(table, term, requested, pageSize, cancel.Token, order), cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            Rows.ItemsSource = got.Rows;
            Prev.IsEnabled = requested > 0;
            Next.IsEnabled = got.HasMore;
            Save.IsEnabled = !saving && got.Rows.Count > 0;
            Status.Text = got.Rows.Count == 0 ? L.T("검색 조건에 맞는 행이 없습니다.", "No matching rows.") : L.T("{0:N0}–{1:N0}번째 행", "Rows {0:N0}–{1:N0}", (long)requested * pageSize + 1, (long)requested * pageSize + got.Rows.Count);
            if (got.Rows.Count > 0)
            {
                Rows.SelectedIndex = (int)(selected % pageSize);
                if (ordinal.HasValue || retainedOrdinal.HasValue)
                    Rows.ScrollIntoView(Rows.SelectedItem);
            }
            foreach (var col in Rows.Columns)
                col.SortDirection = order?.Column == Rows.Columns.IndexOf(col)
                ? order.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending : null;
        }
        catch (Exception ex) { if (!cancel.IsCancellationRequested) Status.Text = L.T("결과를 읽지 못했습니다. ", "Could not read results. ") + ex.Message; }
        finally { if (ReferenceEquals(reading, cancel)) reading = null; }
    }
    private void RowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Details == null)
            return;
        Details.ItemsSource = Rows.SelectedItem is TrackerCell[] values && Tables.SelectedItem is TrackerTable table
            ? values.Select((cell, i) => new FieldValue(table.DisplayHeaders[i], cell, result?.IsTextFile == true ? L.T("CSV/TSV 텍스트", "CSV/TSV text") : cell.Storage)).ToArray() : null;
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (saving || result is not { } data || Tables.SelectedItem is not TrackerTable table)
            return;
        var picker = new SaveFileDialog { InitialDirectory = DeskSettings.InitialExportDirectory, Title = L.T("현재 검색 결과 전체 저장", "Save all matching rows"), Filter = L.T("CSV 파일|*.csv", "CSV files|*.csv"), FileName = Path.GetFileNameWithoutExtension(path) + L.T("_검색결과.csv", "_results.csv") };
        if (picker.ShowDialog(this) != true)
            return;
        string term = Search.Text, file = picker.FileName;
        var token = lifetime;
        var order = sort;
        saving = true;
        Save.IsEnabled = false;
        Status.Text = L.T("검색 결과를 저장하고 있습니다…", "Saving search results…");
        try
        {
            var count = await Work(() => data.Export(table, term, file, token, order), token);
            if (!token.IsCancellationRequested)
            {
                string summary = L.T("검색 결과 {0:N0}행 저장", "{0:N0} matching rows saved", count);
                SavedOutput.Show(Status, file, data.IsTextFile ? summary : table.Name + " · " + summary);
            }
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) Status.Text = L.T("결과를 저장하지 못했습니다. ", "Could not save results. ") + ex.Message; }
        finally { saving = false; if (!token.IsCancellationRequested && reading == null) Save.IsEnabled = Rows.Items.Count > 0; }
    }
}
