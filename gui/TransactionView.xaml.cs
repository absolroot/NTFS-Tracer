using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using EvidenceImage;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace JournalDesk;

public partial class TransactionView : UserControl
{
    private static string Cache => DeskSettings.IndexStore;
    private readonly DispatcherTimer typing = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private readonly Stack<string> history = [];
    private string? directory;
    private string cursor = "", summary = "", sourceHash = "";
    private TransactionSort sort = TransactionSort.Lsn;
    private bool descending, explicitSort;
    private (string Search, int Filter, RecordSearch Records, TransactionSort Sort, IReadOnlyList<long> Order)? sorted;
    private bool restoringSort;
    private bool more, initialised, loading, closed;
    private Window? host;
    private long selection;
    private CancellationTokenSource? importing, reading, exporting;
    private FileStream? evidence;
    private TransactionQuery? query;
    private double? preferredDetailHeight;
    private bool restoreRecordAfterSource;
    internal bool CanShowSourceInfo => evidence != null && query != null && importing == null;
    internal string? SourcePath => evidence?.Name;
    internal TransactionQuery? LeadQuery => query;

    internal async Task OpenLeadLsnAsync(string lsn)
    {
        if (query == null || importing != null || closed)
            return;
        syncingFilter = true;
        try
        {
            filterBeforeLsn ??= Filter.SelectedIndex;
            Filter.SelectedIndex = 0;
            RecordFields.Reset();
            Query.Text = "0x" + lsn;
        }
        finally { syncingFilter = false; }
        typing.Stop();
        ResetPage();
        pendingSort = pageRefreshPending = false;
        await Load();
    }
    internal string? SourceHash => evidence == null ? null : sourceHash;
    internal event Action? SourceChanged;
    internal bool MatchesSource(string? path) => MainWindow.MatchesSource(evidence, path);
    internal bool CanReplaceSource => !closed && importing == null && exporting == null;

    public TransactionView()
    {
        InitializeComponent();
        Identifiers.SearchRequested += SearchRecordId;
        PropertyChangedEventManager.AddHandler(DisplayTimeZone.Current, (_, _) =>
        {
            AdditionalFields.Items.Refresh();
            TimestampRows.Items.Refresh();
            TimestampTitle.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        }, nameof(DisplayTimeZone.Code));
        PropertyChangedEventManager.AddHandler(JournalPathPreference.Current, PathDisplayChanged, nameof(JournalPathPreference.Enabled));
        UpdatePathColumn();
        Splitter.DragCompleted += (_, e) =>
        {
            if (!e.Canceled && DetailHeight.Height.IsAbsolute)
                preferredDetailHeight = DetailHeight.Height.Value;
        };
        Layout.SizeChanged += (_, e) => { if (e.HeightChanged && Inspector.Visibility == Visibility.Visible) FitDetail(); };
        Rows.MouseLeftButtonUp += (_, e) =>
        {
            if ((Inspector.Visibility == Visibility.Collapsed || SourceInfo.IsVisible) && e.OriginalSource is DependencyObject target &&
                ItemsControl.ContainerFromElement(Rows, target) is DataGridRow { IsSelected: true } && Rows.SelectedItem is TxRow row)
                ShowRecordPanel(row);
        };
        typing.Tick += async (_, _) => { typing.Stop(); await Load(); };
        Loaded += (_, _) => BindHost();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _ = RefreshPreparedSourceAsync();
        };
        PreviewKeyDown += Shortcut;
        initialised = true;
        UpdateSortIndicator();
        ClearDetails();
        Buttons();
    }
    public void SetDirectory(string? dir) => directory = dir;

    private async void PathDisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdatePathColumn();
        if (initialised && sort is TransactionSort.Name or TransactionSort.Path or TransactionSort.PathSource)
        {
            var nextSort = JournalPathPreference.Current.Enabled ? TransactionSort.Path : TransactionSort.Name;
            if (nextSort != sort)
            {
                sort = nextSort;
                await ReloadSort();
            }
        }
    }
    private void UpdatePathColumn()
    {
        if (NameColumn == null)
            return;
        bool show = JournalPathPreference.Current.Enabled;
        NameColumn.Header = L.Caption(show ? "Path" : "RecordedFileNames");
        NameColumn.Binding = new Binding(show ? nameof(TxRow.PathText) : nameof(TxRow.Name));
        NameColumn.ElementStyle = (Style)FindResource(show ? "TransactionPathCell" : "Desk.LogText");
        PathSourceColumn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool detailRefreshPending;
    internal async Task RefreshLanguage()
    {
        UpdatePathColumn();
        RefreshSummary();
        detailRefreshPending = detail != null;
        sourceDescription = null;
        if (query == null)
            return;
        await ReloadSort();
    }

    private void RefreshSummary()
    {
        summary = evidence is { } input && sourceStats is { } info
            ? L.T("{0}   ·   레코드 {1:N0}건", "{0}   ·   Records: {1:N0}", Path.GetFileName(input.Name), info.Records) : "";
        if (sourceStats?.Warnings > 0)
            summary += L.T("   ·   해석 중 확인할 구간 {0:N0}개", "   ·   Regions requiring review: {0:N0}", sourceStats.Warnings);
        State.Text = summary;
    }

    private void UpdateSortIndicator()
    {
        var active = sort switch
        {
            TransactionSort.Lsn => LsnColumn,
            TransactionSort.TransactionId => TransactionIdColumn,
            TransactionSort.Operation => OperationColumn,
            TransactionSort.PathSource => PathSourceColumn,
            _ => NameColumn
        };
        foreach (var column in Rows.Columns)
            column.SortDirection = explicitSort && column == active ? (descending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
    }
    private async void SortRows(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (query == null || importing != null)
            return;
        var chosen = e.Column == LsnColumn ? TransactionSort.Lsn : e.Column == TransactionIdColumn ? TransactionSort.TransactionId :
            e.Column == OperationColumn ? TransactionSort.Operation : e.Column == PathSourceColumn ? TransactionSort.PathSource :
            JournalPathPreference.Current.Enabled ? TransactionSort.Path : TransactionSort.Name;
        var direction = SortCycle.Next(explicitSort && sort == chosen, descending);
        explicitSort = direction.HasValue;
        descending = direction ?? false;
        sort = explicitSort ? chosen : TransactionSort.Lsn;
        UpdateSortIndicator();
        await ReloadSort();
    }
    private bool pendingSort;
    private Task ReloadSort()
    {
        var anchor = Rows.SelectedItem as TxRow;
        typing.Stop();
        ResetPage(anchor != null);
        if (!IsVisible)
        {
            pendingSort = true;
            return Task.CompletedTask;
        }
        return Load(anchor);
    }

    private void BindHost()
    {
        var next = Window.GetWindow(this);
        if (next == null || next == host)
            return;
        if (host != null)
            host.Closed -= CloseSource;
        host = next;
        host.Closed += CloseSource;
    }
    private void CloseSource(object? sender, EventArgs e)
    {
        if (closed)
            return;
        closed = true;
        typing.Stop();
        importing?.Cancel();
        reading?.Cancel();
        exporting?.Cancel();
        describing?.Cancel();
        selection++;
        evidence?.Dispose();
        evidence = null;
        if (host != null)
        {
            host.Closed -= CloseSource;
            host = null;
        }
    }
    internal void CloseWithOwner() => CloseSource(this, EventArgs.Empty);
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // WPF also unloads this view when a different tab is selected.
        if (typing.IsEnabled)
        {
            typing.Stop();
            pageRefreshPending = true;
        }
    }
    private void Shortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && Query.IsKeyboardFocused && Query.Text.Length > 0)
        {
            Query.Clear();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && Inspector.IsVisible && !Filter.IsDropDownOpen)
        {
            if ((Raw.IsKeyboardFocusWithin && Raw.SelectionLength > 1) || (Payload.IsKeyboardFocusWithin && Payload.SelectionLength > 1))
                return;
            CloseInspectorClick(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Rows.IsKeyboardFocusWithin && Rows.SelectedItem is TxRow row)
        {
            ShowRecordPanel(row);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        if (e.Key == Key.O && OpenLog.IsEnabled)
        {
            e.Handled = true;
            Pick(sender, e);
        }
        if (e.Key == Key.F && Query.IsEnabled)
        {
            e.Handled = true;
            Query.Focus();
            Query.SelectAll();
        }
    }
    private async void Pick(object sender, RoutedEventArgs e) => await PickSourceAsync(Window.GetWindow(this));
    internal async Task PickSourceAsync(Window? owner, Action? onPicked = null)
    {
        if (importing != null || exporting != null || owner is MainWindow { IsPreparingSources: true })
            return;
        var dialog = new OpenFileDialog { Title = L.T("추출한 NTFS 트랜잭션 로그($LogFile) 열기", "Open extracted NTFS transaction log ($LogFile)"), Filter = L.T("추출 파일|*.*", "Extracted files|*.*") };
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
            if (File.Exists(Path.Combine(directory, "$LogFile")))
                dialog.FileName = "$LogFile";
        }
        if (dialog.ShowDialog(owner) == true)
        {
            onPicked?.Invoke();
            await OpenSourceAsync(dialog.FileName);
        }
    }
    private void Stop(object sender, RoutedEventArgs e)
    {
        importing?.Cancel();
        exporting?.Cancel();
    }

    private bool pageRefreshPending;

    private TxSummary? sourceStats;
    private CancellationTokenSource? describing;
    private string? sourceDescription;
    internal sealed record PreparedSource(TransactionQuery Query, TxSummary Stats);

    internal static async Task<PreparedSource> PrepareSourceAsync(FileStream input, Action<string>? progress, CancellationToken token, string? verifiedHash = null)
    {
        progress?.Invoke(L.T("트랜잭션 로그의 SHA-256을 계산하고 있습니다…", "Computing the SHA-256 hash of the transaction log…"));
        string hash = verifiedHash ?? await EvidenceHash.Sha256Async(input, token);
        Directory.CreateDirectory(Cache);
        string db = Path.Combine(Cache, hash + ".log4.sqlite");
        var stats = await IndexCache.OpenAsync(db, path =>
        {
            progress?.Invoke(L.T("트랜잭션 기록과 작업 데이터를 읽고 있습니다…", "Reading transaction records and operation data…"));
            return Build(input.Name, path, token);
        }, path => new TransactionQuery(path).Summary(), summary => Verify(summary, hash, input.Length), token);
        token.ThrowIfCancellationRequested();
        return new(new TransactionQuery(db), stats);
    }
    internal void SetPreparedSource(FileStream? input, PreparedSource? prepared)
    {
        BindHost();
        typing.Stop();
        sorted = null;
        evidence?.Dispose();
        evidence = input;
        describing?.Cancel();
        describing = null;
        sourceDescription = null;
        sourceStats = prepared?.Stats;
        query = prepared?.Query;
        sourceHash = prepared?.Stats.Hash ?? "";
        directory = input == null ? directory : Path.GetDirectoryName(input.Name);
        RefreshSummary();
        ViewSource.ToolTip = input == null ? null : $"{input.Name}\nSHA-256   {sourceHash}";
        SourceInfo.Text = "";
        SourceInfo.Visibility = Visibility.Collapsed;
        restoreRecordAfterSource = false;
        syncingFilter = true;
        try
        {
            filterBeforeLsn = null;
            Query.Clear();
            RecordFields.Reset();
            Filter.SelectedIndex = 0;
        }
        finally { syncingFilter = false; }
        ResetPage();
        pendingSort = false;
        pageRefreshPending = query != null;
        SourceChanged?.Invoke();
    }

    internal void SetMftPaths(PathResolver? resolver, string? sourcePath, string? hash)
    {
        if (query is not { } backend)
            return;
        TransactionMft? attachment = resolver != null && sourcePath != null && hash != null
            ? new(resolver, sourcePath, hash) : null;
        if (attachment == backend.Mft)
            return;
        query = backend.WithMft(attachment);
        sorted = null;
        sourceDescription = null;
        detailRefreshPending = Rows.SelectedItem is TxRow;
        selection++;
        if (!pageRefreshPending)
            _ = ReloadSort();
    }
    internal Task RefreshPreparedSourceAsync()
    {
        if ((!pageRefreshPending && !pendingSort) || !IsVisible || importing != null || closed)
            return Task.CompletedTask;
        var anchor = pendingSort && !pageRefreshPending ? Rows.SelectedItem as TxRow : null;
        pendingSort = pageRefreshPending = false;
        return Load(anchor);
    }
    public async Task OpenSourceAsync(string path)
    {
        if (!CanReplaceSource)
            return;
        BindHost();
        using var stop = new CancellationTokenSource();
        importing = stop;
        typing.Stop();
        reading?.Cancel();
        Buttons();
        FileStream? opened = null;
        try
        {
            opened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var prepared = await PrepareSourceAsync(opened, text => State.Text = text, stop.Token);
            SetPreparedSource(opened, prepared);
            opened = null;
        }
        catch (OperationCanceledException) { if (!closed) State.Text = L.T("트랜잭션 로그 열기를 취소했습니다.  ", "Opening the transaction log was cancelled.  ") + summary; return; }
        catch (Exception ex) { if (!closed) State.Text = L.T("트랜잭션 로그를 열지 못했습니다.  ", "Could not open the transaction log.  ") + ex.Message; return; }
        finally
        {
            opened?.Dispose();
            importing = null;
            Buttons();
        }
        await RefreshPreparedSourceAsync();
    }
    private static void Verify(TxSummary stats, string hash, long length)
    {
        if (!string.Equals(stats.Hash, hash, StringComparison.OrdinalIgnoreCase) || stats.Bytes != length)
            throw new InvalidDataException(L.T("분석 색인의 원본 해시 또는 파일 크기가 추출본과 다릅니다.", "The source hash or file size in the index does not match the extracted file."));
    }
    private static Task Build(string path, string target, CancellationToken token) =>
        EngineProcess.RunAsync("logfile", path, target, token);
    private void Buttons()
    {
        bool idle = importing == null && exporting == null;
        PageLimit.IsEnabled = idle && !loading && query != null;
        bool welcome = query == null && importing == null;
        Welcome.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
        TransactionCommands.Visibility = welcome ? Visibility.Collapsed : Visibility.Visible;
        Results.Visibility = query == null ? Visibility.Collapsed : Visibility.Visible;
        OpenLog.IsEnabled = idle;
        StopLog.Visibility = importing == null ? Visibility.Collapsed : Visibility.Visible;
        CancelExport.Visibility = exporting == null ? Visibility.Collapsed : Visibility.Visible;
        ViewSource.IsEnabled = CanShowSourceInfo;
        ViewSource.Visibility = query != null ? Visibility.Visible : Visibility.Collapsed;
        Query.IsEnabled = query != null && idle;
        ClearQuery.IsEnabled = Query.IsEnabled;
        RecordFields.IsEnabled = Query.IsEnabled;
        Filter.IsEnabled = query != null && idle && TransactionQuery.ExactLsn(Query.Text) == null;
        ExportLog.IsEnabled = query != null && idle && !loading && RecordFields.Criteria != null && Rows.Items.Count > 0;
        Prev.IsEnabled = !loading && idle && history.Count > 0;
        Next.IsEnabled = !loading && idle && more;
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (query is not { } backend || evidence == null || exporting != null || importing != null || RecordFields.Criteria is not { } records)
            return;
        var dialog = new SaveFileDialog
        {
            InitialDirectory = DeskSettings.InitialExportDirectory,
            Title = L.T("현재 조건의 트랜잭션 기록을 새 CSV 파일로 저장", "Save matching transaction records to a new CSV file"),
            FileName = L.T("트랜잭션 기록.csv", "Transaction records.csv"),
            Filter = L.T("CSV 파일|*.csv", "CSV files|*.csv"),
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        if (File.Exists(dialog.FileName))
        {
            State.Text = L.T("이름이 같은 파일이 있습니다. 새 파일 이름으로 저장해 주세요.", "A file with that name already exists. Choose a new file name.");
            return;
        }
        string search = Query.Text.Trim(), source = evidence.Name, hash = sourceHash;
        int filter = Filter.SelectedIndex;
        bool reverse = descending;
        var order = sorted is { } index && index.Sort == sort && index.Filter == filter && index.Search == search &&
            index.Records == records ? index.Order : null;
        using var stop = new CancellationTokenSource();
        exporting = stop;
        Buttons();
        State.Text = L.T("조건에 맞는 모든 기록을 CSV 파일로 저장하고 있습니다…", "Saving all matching records to CSV…");
        try
        {
            long count = await Task.Run(() => backend.Export(dialog.FileName, search, filter, source, hash, stop.Token, reverse, order, records), stop.Token);
            if (!closed)
                SavedOutput.Show(State, dialog.FileName, L.T("기록 {0:N0}건 저장", "{0:N0} records saved", count));
        }
        catch (OperationCanceledException) { if (!closed) State.Text = L.T("CSV 저장을 취소했습니다.  ", "CSV export cancelled.  ") + summary; }
        catch (Exception ex) { if (!closed) State.Text = L.T("CSV를 저장하지 못했습니다.  ", "Could not save the CSV file.  ") + ex.Message; }
        finally { exporting = null; Buttons(); }
    }
    private void ResetPage(bool keepRecord = false)
    {
        reading?.Cancel();
        history.Clear();
        cursor = "";
        more = false;
        PageStatus.Text = "";
        ResetSearch.Visibility = Visibility.Collapsed;
        if (!keepRecord)
        {
            Rows.ItemsSource = null;
            ClearDetails();
        }
        Buttons();
    }
    private void QueryChanged(object sender, TextChangedEventArgs e)
    {
        if (ClearQuery != null)
            ClearQuery.Visibility = Query.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!initialised || syncingFilter || importing != null || query == null)
            return;
        bool exact = TransactionQuery.ExactLsn(Query.Text) != null;
        if (exact != filterBeforeLsn.HasValue)
        {
            int restore = filterBeforeLsn ?? 0;
            filterBeforeLsn = exact ? Filter.SelectedIndex : null;
            syncingFilter = true;
            try
            {
                Filter.SelectedIndex = exact ? 0 : restore;
            }
            finally { syncingFilter = false; }
        }
        ResetPage();
        typing.Stop();
        typing.Start();
    }
    private void RecordFieldsChanged(object? sender, EventArgs e)
    {
        if (!initialised || syncingFilter || importing != null || query == null)
            return;
        ResetPage();
        typing.Stop();
        typing.Start();
    }
    private int? filterBeforeLsn;
    private bool syncingFilter;
    private void ClearQueryClick(object sender, RoutedEventArgs e)
    {
        Query.Clear();
        Query.Focus();
    }
    private async void ResetSearchClick(object sender, RoutedEventArgs e)
    {
        ResetSearchInputs();
        typing.Stop();
        ResetPage();
        Query.Focus();
        await Load();
    }

    private void ResetSearchInputs(RecordIdChoice? choice = null)
    {
        syncingFilter = true;
        try
        {
            filterBeforeLsn = null;
            Query.Clear();
            RecordFields.Reset();
            Filter.SelectedIndex = 0;
            if (choice != null)
                RecordFields.SetId(choice);
        }
        finally { syncingFilter = false; }
    }

    private async void SearchRecordId(RecordIdChoice choice)
    {
        if (query == null || importing != null || exporting != null)
            return;
        var anchor = Rows.SelectedItem as TxRow;
        ResetSearchInputs(choice);
        typing.Stop();
        ResetPage(anchor != null);
        detailRefreshPending = anchor != null;
        await Load(anchor);
    }
    private async void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialised || syncingFilter || importing != null || query == null)
            return;
        ResetPage();
        typing.Stop();
        await Load();
    }
    private int pageSize = PageSizeOption.Preferred;

    private async void PageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedValue: int size } || size == pageSize)
            return;
        PageSizeOption.Remember(size);
        pageSize = size;
        if (!initialised || query == null)
            return;
        var anchor = Rows.SelectedItem as TxRow;
        ResetPage(anchor != null);
        await Load(anchor);
    }

    private async void Previous(object sender, RoutedEventArgs e)
    {
        if (loading || history.Count == 0)
            return;
        cursor = history.Pop();
        await Load();
    }
    private async void Following(object sender, RoutedEventArgs e)
    {
        if (loading || !more || Rows.Items.Count == 0)
            return;
        history.Push(cursor);
        cursor = ((TxRow)Rows.Items[Rows.Items.Count - 1]).Lsn;
        await Load();
    }
    private void QueryStatus(string text)
    {
        Empty.Text = Rows.HasItems ? "" : text;
        PageStatus.Text = Rows.HasItems ? text : "";
    }
    internal sealed record SavedFilters(RecordSearchFields.SavedFields Fields, TransactionSort Sort, bool Descending, bool ExplicitSort);

    internal SavedFilters CaptureFilters() => new(RecordFields.CaptureFields(), sort, descending, explicitSort);

    internal Task ResumeSearch(string search, int filter, string? lsn, SavedFilters? saved)
    {
        ResetSearchInputs();
        Query.Text = search;
        Filter.SelectedIndex = Math.Clamp(filter, 0, Filter.Items.Count - 1);
        if (saved != null)
        {
            RecordFields.RestoreFields(saved.Fields);
            sort = Enum.IsDefined(saved.Sort) ? saved.Sort : TransactionSort.Lsn;
            descending = saved.Descending;
            explicitSort = saved.ExplicitSort;
            UpdateSortIndicator();
        }
        typing.Stop();
        ResetPage();
        return Load(savedLsn: lsn);
    }

    private async Task Load(TxRow? anchor = null, string? savedLsn = null)
    {
        var backend = query;
        if (closed || backend == null || importing != null)
            return;
        pageRefreshPending = false;
        reading?.Cancel();
        if (RecordFields.Criteria is not { } records)
        {
            ResetPage();
            QueryStatus(L.Caption("ReferenceNumberInvalid"));
            return;
        }
        using var stop = new CancellationTokenSource();
        reading = stop;
        loading = true;
        more = false;
        Buttons();
        if (anchor == null && Rows.ItemsSource != null)
        {
            Rows.ItemsSource = null;
            ClearDetails();
        }
        ResetSearch.Visibility = Visibility.Collapsed;
        QueryStatus(L.T("조회 중…", "Searching…"));
        string search = Query.Text.Trim(), after = cursor;
        int filter = Filter.SelectedIndex;
        try
        {
            var key = sort;
            bool reverse = descending;
            int start = history.Count * pageSize;
            IReadOnlyList<long>? order = null;
            if (key != TransactionSort.Lsn)
            {
                if (sorted is { } prior && prior.Search == search && prior.Filter == filter && prior.Sort == key && prior.Records == records)
                    order = prior.Order;
                else
                {
                    QueryStatus(L.T("검색 결과 전체를 정렬하고 있습니다…", "Sorting all matching records…"));
                    order = await Task.Run(() => backend.BuildOrder(search, filter, key, stop.Token, records), stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    sorted = (search, filter, records, key, order);
                }
            }
            if (anchor != null)
                anchor = Rows.SelectedItem as TxRow;
            var restoreAnchor = anchor;
            if (savedLsn != null && TransactionQuery.ExactLsn(savedLsn) is { } normalized)
            {
                restoreAnchor = (await Task.Run(() => backend.Page(normalized, 0, "", stop.Token), stop.Token)).FirstOrDefault();
                stop.Token.ThrowIfCancellationRequested();
            }
            if (restoreAnchor != null)
            {
                var starts = await Task.Run(() => backend.FindPageStarts(search, filter, reverse, order, restoreAnchor, stop.Token, pageSize, records), stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                history.Clear();
                foreach (var mark in starts.Take(starts.Count - 1))
                    history.Push(mark);
                cursor = after = starts[^1];
                start = history.Count * pageSize;
            }
            var rows = await Task.Run(() => backend.Page(search, filter, after, stop.Token, reverse, order, start, pageSize, records), stop.Token);
            if (stop.IsCancellationRequested)
                return;
            if (anchor != null && (Rows.SelectedItem as TxRow)?.Id != anchor.Id)
            {
                await ReloadSort();
                return;
            }
            more = rows.Count > pageSize;
            if (more)
                rows.RemoveAt(rows.Count - 1);
            var restore = restoreAnchor == null ? null : rows.FirstOrDefault(row => row.Id == restoreAnchor.Id);
            restoringSort = restore != null && Rows.SelectedItem is TxRow priorRow && priorRow.Id == restore.Id;
            try
            {
                Rows.ItemsSource = rows;
                if (restore != null)
                    Rows.SelectedItem = restore;
            }
            finally { restoringSort = false; }
            if (detailRefreshPending)
            {
                detailRefreshPending = false;
                if (restore != null && Inspector.IsVisible && !SourceInfo.IsVisible)
                {
                    RecordTitle.Text = $"{restore.Label}   ·   LSN 0x{restore.Lsn}";
                    await InspectRecord(restore, preserveBytes: detail != null);
                }
            }
            UpdateSortIndicator();
            string? lsn = TransactionQuery.ExactLsn(search);
            Empty.Text = rows.Count != 0 ? "" : lsn != null && records.IsEmpty ? L.T("LSN 0x{0}에 해당하는 기록이 없습니다.", "No records found for LSN 0x{0}.", lsn) : L.T("조건에 맞는 트랜잭션 기록이 없습니다.", "No matching transaction records.");
            if (rows.Count != 0)
                Rows.ScrollIntoView(restore ?? rows[0]);
            if (rows.Count == 1 && lsn != null)
                Rows.SelectedIndex = 0;
            ResetSearch.Visibility = rows.Count == 0 && (search.Length > 0 || filter != 0 || !records.IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
            PageStatus.Text = rows.Count > 0 && (more || history.Count > 0)
                ? L.T("{0:N0}–{1:N0}번째", "Records {0:N0}–{1:N0}", history.Count * pageSize + 1, history.Count * pageSize + rows.Count)
                : L.T("{0:N0}건", "Records: {0:N0}", rows.Count);
            State.Text = summary;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { if (!stop.IsCancellationRequested) { QueryStatus(L.T("기록을 조회하지 못했습니다.", "Could not load records.")); State.Text = ex.Message; } }
        finally { if (ReferenceEquals(reading, stop)) { reading = null; loading = false; Buttons(); } }
    }
    private TxDetail? detail;
    private bool pickingSide, preferUndo;
    private void ChangePayload(object sender, RoutedEventArgs e)
    {
        if (pickingSide || detail == null)
            return;
        preferUndo = sender == UndoChoice;
        DisplayPayload();
    }
    private void SetPayloadChoices(TxDetail record)
    {
        RedoChoice.Content = "Redo";
        UndoChoice.Content = "Undo";
        bool undoOnly = record.Undo.Bytes.Length > 0 && record.Redo.Bytes.Length == 0;
        bool redoOnly = record.Redo.Bytes.Length > 0 && record.Undo.Bytes.Length == 0;
        pickingSide = true;
        try
        {
            (undoOnly || (preferUndo && !redoOnly) ? UndoChoice : RedoChoice).IsChecked = true;
        }
        finally { pickingSide = false; }
        RedoChoice.IsEnabled = UndoChoice.IsEnabled = true;
        DisplayPayload();
    }
    private void DisplayPayload()
    {
        if (detail == null)
            return;
        var data = UndoChoice.IsChecked == true ? detail.Undo : detail.Redo;
        if (detail.NonUpdateType is { } kind)
        {
            PayloadInfo.Text = kind;
            PayloadInfo.ToolTip = kind;
            Payload.ClearView(kind is "Checkpoint" or "RecordType 2"
                ? L.T("체크포인트 레코드에는 Redo/Undo 작업이 없습니다.", "Redo and Undo operations do not apply to checkpoint records.")
                : L.T("이 레코드에서는 Redo/Undo 작업을 판독할 수 없습니다.", "Redo and Undo operations could not be decoded for this record."));
            return;
        }
        PayloadInfo.Text = data.DeclaredLength is long n
            ? L.T("{0}   ·   기록 {1:N0} B / 복원 {2:N0} B", "{0}   ·   Declared {1:N0} B / recovered {2:N0} B", data.Operation, n, data.Bytes.Length)
            : L.T("{0}   ·   길이 미기록", "{0}   ·   Length not recorded", data.Operation);
        PayloadInfo.ToolTip = PayloadInfo.Text + "\n" + L.T("Offset 0은 선택한 Redo 또는 Undo 데이터의 시작입니다.", "Offset 0 is the start of the selected Redo or Undo data.");
        if (data.Bytes.Length == 0)
            Payload.ClearView(data.DeclaredLength switch
            {
                0 => L.T("기록된 데이터 길이가 0바이트입니다.", "The recorded data length is zero."),
                > 0 => L.T("길이가 기록되어 있지만 복원된 바이트가 없습니다.", "A length was recorded, but no bytes were recovered."),
                _ => L.T("이 작업에서 해당 데이터를 확인할 수 없습니다.", "No data for this side could be identified in this operation.")
            });
        else
            Payload.ShowBytes(data.Bytes, 0);
    }
    private void ClearDetails(bool keepPanel = false)
    {
        selection++;
        Info.Text = "";
        HeaderLocation.Text = "";
        OverviewSummary.DataContext = null;
        OverviewSummary.Visibility = Visibility.Collapsed;
        OverviewScroll.ScrollToTop();
        detail = null;
        Raw.ClearView();
        Payload.ClearView();
        PayloadInfo.Text = "";
        RedoChoice.IsChecked = UndoChoice.IsChecked = false;
        RedoChoice.IsEnabled = UndoChoice.IsEnabled = false;
        RedoChoice.Content = "Redo";
        UndoChoice.Content = "Undo";
        if (!keepPanel && SourceInfo.Visibility != Visibility.Visible)
            HideDetail();
        restoreRecordAfterSource = false;
    }
    private void CopyOverviewClick(object sender, RoutedEventArgs e)
    {
        if (detail == null)
            return;
        try
        {
            Clipboard.SetText(detail.CopyText());
            State.Text = L.T("트랜잭션 개요를 복사했습니다.", "Transaction overview copied.");
        }
        catch (System.Runtime.InteropServices.ExternalException) { State.Text = L.T("클립보드를 사용할 수 없습니다.", "Clipboard unavailable."); }
    }
    private void FitDetail()
    {
        double available = Math.Max(90, Layout.ActualHeight - Layout.RowDefinitions[0].ActualHeight - Layout.RowDefinitions[1].MinHeight - Splitter.Height - Layout.RowDefinitions[4].ActualHeight);
        DetailHeight.MaxHeight = available;
        DetailHeight.MinHeight = 90;
        DetailHeight.Height = new(Math.Min(preferredDetailHeight ?? Math.Clamp(available * 0.55, 300, 520), available));
    }
    private void OpenDetail()
    {
        if (Inspector.Visibility == Visibility.Visible)
            return;
        Inspector.Visibility = Splitter.Visibility = Visibility.Visible;
        FitDetail();
    }
    private void HideDetail()
    {
        Inspector.Visibility = Splitter.Visibility = Visibility.Collapsed;
        DetailHeight.MinHeight = 0;
        DetailHeight.Height = new(0);
    }
    private void ShowRecordPanel(TxRow row)
    {
        bool opening = Inspector.Visibility != Visibility.Visible;
        SourceInfo.Visibility = Visibility.Collapsed;
        DetailTabs.Visibility = Visibility.Visible;
        RecordTitle.Text = $"{row.Label}   ·   LSN 0x{row.Lsn}";
        OpenDetail();
        if (opening)
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (ReferenceEquals(Rows.SelectedItem, row) && Inspector.IsVisible)
                    Rows.ScrollIntoView(row);
            });
    }
    private void CloseInspectorClick(object sender, RoutedEventArgs e)
    {
        bool sourceOpen = SourceInfo.Visibility == Visibility.Visible;
        SourceInfo.Visibility = Visibility.Collapsed;
        if (sourceOpen && restoreRecordAfterSource && Rows.SelectedItem is TxRow row)
            ShowRecordPanel(row);
        else
            HideDetail();
        if (sourceOpen)
            ViewSource.Focus();
        else
            Rows.Focus();
    }
    private void SourceClick(object sender, RoutedEventArgs e) => ShowSourceInfo();
    internal async void ShowSourceInfo()
    {
        if (!CanShowSourceInfo || evidence is not { } input || query is not { } backend || sourceStats is not { } stats)
            return;
        if (SourceInfo.Visibility != Visibility.Visible)
            restoreRecordAfterSource = Inspector.IsVisible && Rows.SelectedItem != null;
        SourceInfo.Visibility = Visibility.Visible;
        DetailTabs.Visibility = Visibility.Collapsed;
        RecordTitle.Text = L.T("$LogFile 원본 정보", "$LogFile source information");
        OpenDetail();
        SourceInfo.Focus();
        if (sourceDescription != null)
        {
            SourceInfo.Text = sourceDescription;
            return;
        }
        if (describing != null)
            return;
        using var stop = new CancellationTokenSource();
        describing = stop;
        string path = input.Name;
        SourceInfo.Text = L.T("원본 정보를 읽고 있습니다…", "Loading source information…");
        try
        {
            string description = await Task.Run(() => backend.DescribeSource(path, stats, stop.Token), stop.Token);
            if (!stop.IsCancellationRequested)
                SourceInfo.Text = sourceDescription = description;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            if (!stop.IsCancellationRequested)
                SourceInfo.Text = L.T("원본 정보를 읽지 못했습니다. ", "Could not read source information. ") + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(describing, stop))
                describing = null;
        }
    }
    private async void Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!initialised || restoringSort || !ReferenceEquals(e.OriginalSource, Rows))
            return;
        ClearDetails(keepPanel: Rows.SelectedItem is TxRow);
        if (Rows.SelectedItem is not TxRow row || evidence == null || query == null)
            return;
        ShowRecordPanel(row);
        await InspectRecord(row, preserveBytes: false);
    }

    private async Task InspectRecord(TxRow row, bool preserveBytes)
    {
        if (evidence is not { } source || query is not { } backend)
            return;
        long ticket = selection;
        string file = source.Name;
        Info.Text = L.T("기록 정보를 읽고 있습니다…", "Loading record details…");
        try
        {
            var result = await Task.Run(() => backend.Inspect(row, file));
            if (ticket != selection || closed)
                return;
            detail = result;
            Identifiers.SetIds(result.Record.FileIds, result.Record.ParentIds);
            OverviewSummary.DataContext = result;
            OverviewSummary.Visibility = Visibility.Visible;
            Info.Text = "";
            HeaderLocation.Text = L.T("$LogFile 0x{0:X}   ·   헤더 앞 {1:N0}바이트 (섹터 보정 전)", "$LogFile 0x{0:X}   ·   First {1:N0} header bytes (before fixups)", result.Position, result.Header.Length);
            if (!preserveBytes)
            {
                Raw.ShowBytes(result.Header, result.Position, LogHeaderFields.Record(result.Header, result.Position));
                SetPayloadChoices(result);
            }
        }
        catch (Exception ex) { if (ticket == selection && !closed) Info.Text = L.T("원본 근거를 읽지 못했습니다. ", "Could not read source evidence. ") + ex.Message; }
    }
}
