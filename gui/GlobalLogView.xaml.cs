using System.ComponentModel;
using System.IO;
using System.Globalization;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace JournalDesk;

public partial class GlobalLogView : UserControl
{
    private int pageSize = PageSizeOption.Preferred;
    private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(240);
    private readonly DispatcherTimer debounce = new(DispatcherPriority.Background);
    private GlobalLogQuery? query;
    private Journal? source;
    private JournalPathSource? rowPaths;
    private PathResolver? paths;
    private string? original, digest, pairWarning, mftHash, mftPath;
    private CancellationTokenSource? export;
    private GlobalLogFilter? displayedFilter;
    private bool editing, descending = true;
    private JournalTimeZone utc;
    private JournalSort sort = JournalSort.Time;
    private bool explicitSort;
    private (GlobalLogFilter Filter, JournalSort Sort, IReadOnlyList<long> Order)? sorted;
    private readonly Dictionary<JournalSort, (GlobalLogFilter Filter, IReadOnlyList<long> Order)> labelOrders = [];
    private (IReadOnlyList<long> Paths, IReadOnlyList<long> Sources)? allPathOrders;
    private (GlobalLogFilter Filter, IReadOnlyList<long> Paths, IReadOnlyList<long> Sources)? pathOrders;
    private bool restoringSort;
    private bool composingSearch, focusPending;
    private bool startDayOnly = true, endDayOnly = true;
    private long reasonMask;
    private Control? invalidFilter;
    private long startResetRevision, endResetRevision;
    private CancellationTokenSource? request;
    private long selectionRevision;
    private bool dirty = true, loading, ready;
    private GlobalLogCursor? cursor, next;
    private readonly Stack<GlobalLogCursor?> previous = [];
    private GlobalLogRow? selected;
    private GlobalLogRow? rawRequested;
    private double preferredDetailHeight = 264;
    private byte[]? rawData;
    private void FitDetail()
    {
        if (Inspector.Visibility != Visibility.Visible)
            return;
        var grid = (Grid)Split.Parent;
        double available = Math.Max(90, grid.ActualHeight - grid.RowDefinitions[0].ActualHeight - grid.RowDefinitions[1].MinHeight - Split.Height);
        DetailHeight.MaxHeight = available;
        DetailHeight.Height = new(Math.Min(preferredDetailHeight, available));
    }

    public event Action<byte[]>? FileRequested;
    public bool IsCarvedSource
    {
        get; set;
    }
    public long? SelectedPosition => selected?.Position;

    public GlobalLogView()
    {
        InitializeComponent();
        OverviewPanel.Identifiers.SearchRequested += SearchRecordId;
        utc = DisplayTimeZone.Current.Zone;
        PropertyChangedEventManager.AddHandler(DisplayTimeZone.Current, ZoneChanged, nameof(DisplayTimeZone.Code));
        UpdateExportHeaders();
        TextCompositionManager.AddPreviewTextInputStartHandler(QueryText, (_, _) => { composingSearch = true; debounce.Stop(); });
        QueryText.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler((_, _) => EndComposition()), true);
        QueryText.LostKeyboardFocus += (_, _) => { EndComposition(); focusPending = false; };
        PropertyChangedEventManager.AddHandler(JournalPathPreference.Current, PathColumnChanged, nameof(JournalPathPreference.Enabled));
        PathColumnChanged(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(JournalPathPreference.Enabled)));
        NameColumn.CopyingCellClipboardContent += (_, e) =>
        {
            if (JournalPathPreference.Current.Enabled && e.Item is GlobalLogRow row && rowPaths?.CachedPath(row.Position) is { } path)
                e.Content = path;
        };
        GlobalPathSourceColumn.CopyingCellClipboardContent += (_, e) =>
        {
            if (e.Item is GlobalLogRow row)
                e.Content = rowPaths?.CachedSource(row.Position) ?? "";
        };
        foreach (var option in Entry.ReasonOptions)
        {
            var check = new CheckBox
            {
                Content = option.Label,
                Tag = option.Mask,
                ToolTip = "USN_REASON_" + option.Native,
                MinHeight = 25,
                Margin = new Thickness(3, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            check.Checked += ReasonChanged;
            check.Unchecked += ReasonChanged;
            ReasonOptions.Children.Add(check);
        }
        debounce.Tick += (_, _) => { debounce.Stop(); LoadPage(); };
        ready = true;
        Split.DragCompleted += (_, e) =>
        {
            if (!e.Canceled && DetailHeight.Height.IsAbsolute)
                preferredDetailHeight = DetailHeight.Height.Value;
        };
        FilterPanel.SizeChanged += (_, e) => { if (e.HeightChanged) FitDetail(); };
        Records.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged && selected is { } active)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (Records.IsVisible && Records.SelectedItem is GlobalLogRow row && row.Position == active.Position)
                        Records.ScrollIntoView(row);
                });
        };
        SizeChanged += (_, _) =>
        {
            bool wrap = ActualWidth < 660;
            Grid.SetColumnSpan(SearchGroup, wrap ? 2 : 1);
            Grid.SetColumnSpan(FilterCommands, wrap ? 2 : 1);
            Grid.SetColumn(FilterCommands, wrap ? 0 : 1);
            Grid.SetRow(FilterCommands, wrap ? 1 : 0);
            FilterCommands.Margin = wrap ? new Thickness(0, 4, 0, 0) : new Thickness(6, 0, 0, 0);
            FitDetail();
        };
        Loaded += (_, _) =>
        {
            ListenForTypedDate(StartDate);
            ListenForTypedDate(EndDate);
            GoFile.Visibility = FileRequested is null ? Visibility.Collapsed : Visibility.Visible;
            Buttons();
        };
        Unloaded += (_, _) => { debounce.Stop(); request?.Cancel(); };
        Buttons();
        Empty.Text = L.T("연결된 USN Journal 없음", "No USN Journal attached");
    }

    private void UpdateExportHeaders()
    {
        ExportResultsCsv.Header = L.T("레코드 CSV…", "Record CSV…");
        ExportPathsCsv.Header = L.T("경로와 근거를 포함한 CSV…", "CSV with paths and evidence…");
    }

    internal void RefreshLanguage(JournalPathSource? pathsForLanguage)
    {
        UpdateExportHeaders();
        PathColumnChanged(this, new(nameof(JournalPathPreference.Enabled)));
        var captions = Entry.ReasonOptions.ToDictionary(option => option.Mask, option => option.Label);
        foreach (var check in ReasonChecks)
            check.Content = captions[(long)check.Tag];
        SearchReasons(ReasonSearch, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
        if (source != null)
        {
            rowPaths = pathsForLanguage ?? new JournalPathSource(source, paths, pairWarning, IsCarvedSource);
            JournalPathText.SetSource(Records, rowPaths);
            query = new GlobalLogQuery(source.DbPath, rowPaths);
            sorted = null;
            labelOrders.Clear();
            allPathOrders = null;
            pathOrders = null;
            if (selected is { } row)
            {
                RefreshRecordText(row);
                _ = UpdateRecordPath(row.Record, row.FileId, selectionRevision);
            }
            ReloadSort();
        }
        else
            Buttons();
    }

    private void PathColumnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (NameColumn != null)
        {
            NameColumn.Header = L.Caption(JournalPathPreference.Current.Enabled ? "RecordedPath" : "RecordedFileName");
            if (GlobalPathSourceColumn != null)
                GlobalPathSourceColumn.Visibility = JournalPathPreference.Current.Enabled ? Visibility.Visible : Visibility.Collapsed;
            if (ready && sort is JournalSort.Name or JournalSort.Path or JournalSort.PathSource)
            {
                var nextSort = JournalPathPreference.Current.Enabled ? (sort == JournalSort.PathSource ? sort : JournalSort.Path) : JournalSort.Name;
                if (nextSort != sort)
                {
                    sort = nextSort;
                    ReloadSort();
                }
            }
        }
    }

    public void FocusSearch()
    {
        QueryText.Focus();
        QueryText.SelectAll();
    }

    private void EndComposition()
    {
        if (!composingSearch)
            return;
        composingSearch = false;
        if (dirty && IsVisible && query != null)
            ScheduleSearch(fileNameChanged: true);
    }
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (composingSearch || e.Key is not (Key.Enter or Key.Down) || Keyboard.Modifiers != ModifierKeys.None || query == null)
            return;
        e.Handled = true;
        if (!dirty && !loading)
        {
            if (Records.HasItems)
                FocusSelectedRecord();
            return;
        }
        focusPending = true;
        if (!loading)
        {
            debounce.Stop();
            LoadPage();
        }
    }
    internal void FocusSelectedRecord() => EvidencePaneNavigation.FocusRow(Records);

    public void SetSource(Journal? journal, PathResolver? resolver, string? original = null, string? pairWarning = null,
        string? mftHash = null, string? mftPath = null) =>
        SetSource(journal, resolver, original, pairWarning, mftHash, mftPath, null);

    internal void SetSource(Journal? journal, PathResolver? resolver, string? original, string? pairWarning,
        string? mftHash, string? mftPath, JournalPathSource? sharedPaths, string? journalHash = null)
    {
        bool same = ReferenceEquals(journal, source);
        bool changedDetails = !ReferenceEquals(paths, resolver) || this.original != original || this.pairWarning != pairWarning;
        source = journal;
        paths = resolver;
        this.original = original;
        digest = journalHash;
        this.pairWarning = pairWarning;
        this.mftHash = mftHash;
        this.mftPath = mftPath;
        if (!same || changedDetails)
        {
            sorted = null;
            labelOrders.Clear();
            allPathOrders = null;
            pathOrders = null;
            if (!same)
                Records.ItemsSource = null;
            rowPaths = journal == null ? null : sharedPaths ?? new JournalPathSource(journal, resolver, pairWarning, IsCarvedSource);
            JournalPathText.SetSource(Records, rowPaths);
            query = journal == null ? null : new GlobalLogQuery(journal.DbPath, rowPaths);
        }
        if (same)
        {
            if (changedDetails && (sort is JournalSort.Path or JournalSort.PathSource || PathFilters.Criteria is { Path.Length: > 0 }))
                ReloadSort();
            if (changedDetails && selected != null)
                _ = UpdateRecordPath(selected.Record, selected.FileId, selectionRevision);
            return;
        }
        debounce.Stop();
        export?.Cancel();
        export = null;
        ExportMessage("");
        ClearInputs();
        Restart();
        if (IsVisible)
            LoadPage();
    }

    private void Restart(bool keepRecord = false)
    {
        ClearFilterFeedback();
        request?.Cancel();
        displayedFilter = null;
        dirty = true;
        loading = false;
        cursor = null;
        next = null;
        previous.Clear();
        if (!keepRecord)
        {
            Records.ItemsSource = null;
            ShowRecord(null);
        }
        QueryStatus(query == null ? L.T("연결된 USN Journal 없음", "No USN Journal attached") : L.T("기록을 불러오고 있습니다…", "Loading records…"));
        Buttons();
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!ready)
            return;
        if (!IsVisible)
            Kind.IsChecked = false;
        if (IsVisible && dirty)
        {
            debounce.Stop();
            LoadPage(selected);
        }
        else if (!IsVisible && loading)
        {
            request?.Cancel();
            dirty = true;
            loading = false;
        }
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        focusPending = false;
        RefreshSearch(fileNameChanged: true);
    }
    private void RecordFieldsChanged(object? sender, EventArgs e) => RefreshSearch();
    internal void SearchRecordId(RecordIdChoice choice)
    {
        if (query == null)
            return;
        var anchor = selected;
        ClearInputs();
        editing = true;
        try
        {
            PathFilters.SetId(choice);
        }
        finally { editing = false; }
        debounce.Stop();
        Restart(anchor != null);
        LoadPage(anchor);
    }

    private void RefreshSearch(bool fileNameChanged = false)
    {
        if (!ready || editing)
            return;
        Restart();
        if (IsVisible && query != null && !composingSearch)
            ScheduleSearch(fileNameChanged);
        else
            debounce.Stop();
    }

    private void ScheduleSearch(bool fileNameChanged)
    {
        debounce.Stop();
        bool needsPathResolution = PathFilters.Criteria is { Path.Length: > 0 };
        debounce.Interval = fileNameChanged && !needsPathResolution && sort is (JournalSort.Time or JournalSort.Usn)
            ? TimeSpan.Zero
            : FilterDelay;
        debounce.Start();
    }
    private void ScopeChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || editing)
            return;
        debounce.Stop();
        Restart();
        if (IsVisible)
            LoadPage();
    }
    private IEnumerable<CheckBox> ReasonChecks => ReasonOptions.Children.OfType<CheckBox>();
    private void ReasonChanged(object sender, RoutedEventArgs e)
    {
        if (editing)
            return;
        var check = (CheckBox)sender;
        long bit = (long)check.Tag;
        reasonMask = check.IsChecked == true ? reasonMask | bit : reasonMask & ~bit;
        RefreshSearch();
    }
    private void ResetReasonChecks()
    {
        reasonMask = 0;
        foreach (var check in ReasonChecks)
            check.IsChecked = false;
    }
    private void ResetReasons(object sender, RoutedEventArgs e)
    {
        editing = true;
        try
        {
            ResetReasonChecks();
        }
        finally { editing = false; }
        ReasonSearch.Focus();
        RefreshSearch();
    }
    private void SearchReasons(object sender, TextChangedEventArgs e)
    {
        if (!ready)
            return;
        string term = ReasonSearch.Text.Trim();
        bool found = false;
        foreach (var check in ReasonChecks)
        {
            string related = (long)check.Tag switch
            {
                0x100 => L.T("파일 생성", "File creation"),
                0x200 => L.T("파일 삭제", "File deletion"),
                0x1000 or 0x2000 => L.T("파일 이름 변경 전후", "File rename old name new name"),
                1 or 2 or 4 => L.T("파일 내용 변경", "File content change"),
                0x10 or 0x20 or 0x40 => L.T("ADS 내용 변경", "ADS content change"),
                _ => ""
            };
            bool matches = check.Content.ToString()!.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                related.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                check.ToolTip.ToString()!.Contains(term, StringComparison.OrdinalIgnoreCase);
            check.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            found |= matches;
        }
        NoReasons.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ReasonsOpened(object sender, EventArgs e)
    {
        ReasonSearch.Clear();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (KindPopup.IsOpen) ReasonSearch.Focus(); });
    }
    private void CloseReasons(object sender, RoutedEventArgs e)
    {
        Kind.IsChecked = false;
        Kind.Focus();
    }
    private void ReasonKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseReasons(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Up or Key.Down))
            return;
        var choices = ReasonChecks.Where(check => check.Visibility == Visibility.Visible).ToList();
        if (choices.Count == 0)
            return;
        int index = Keyboard.FocusedElement is CheckBox active ? choices.IndexOf(active) : -1;
        int next = index < 0 ? (e.Key == Key.Down ? 0 : choices.Count - 1) : Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, choices.Count - 1);
        choices[next].Focus();
        choices[next].BringIntoView();
        e.Handled = true;
    }
    private void UpdateSortLabel()
    {
        TimeColumn.Header = L.T("기록 시각 ({0})", "Record time ({0})", utc.Code);
        var active = sort switch
        {
            JournalSort.Time => TimeColumn,
            JournalSort.Usn => UsnColumn,
            JournalSort.Reason => ReasonColumn,
            JournalSort.PathSource => (DataGridColumn)GlobalPathSourceColumn,
            _ => NameColumn
        };
        foreach (var column in Records.Columns)
            column.SortDirection = explicitSort && column == active ? (descending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
    }
    private void SortRecords(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (query == null)
            return;
        var nextSort = e.Column == TimeColumn ? JournalSort.Time : e.Column == UsnColumn ? JournalSort.Usn :
            e.Column == ReasonColumn ? JournalSort.Reason : e.Column == GlobalPathSourceColumn ? JournalSort.PathSource :
            JournalPathPreference.Current.Enabled ? JournalSort.Path : JournalSort.Name;
        var direction = SortCycle.Next(explicitSort && nextSort == sort, descending);
        explicitSort = direction.HasValue;
        descending = direction ?? true;
        sort = explicitSort ? nextSort : JournalSort.Time;
        ReloadSort();
    }
    private void ReloadSort()
    {
        var anchor = selected;
        debounce.Stop();
        Restart(anchor != null);
        LoadPage(anchor);

    }
    private void ClearInputs()
    {
        ++startResetRevision;
        ++endResetRevision;
        editing = true;
        try
        {
            QueryText.Clear();
            ExactUsn.Clear();
            StartTime.Clear();
            EndTime.Clear();
            PathFilters.Reset();
            SetDate(StartDate, null);
            SetDate(EndDate, null);
            ResetReasonChecks();
            NameScope.IsChecked = true;
            startDayOnly = endDayOnly = true;
        }
        finally { editing = false; }
    }
    private void ClearQueryClick(object sender, RoutedEventArgs e)
    {
        QueryText.Clear();
        QueryText.Focus();
    }
    private void ClearClick(object sender, RoutedEventArgs e)
    {
        ClearInputs();
        debounce.Stop();
        Restart();
        LoadPage();
    }
    private static TextBox? DateEditor(DatePicker picker) =>
        picker.Template?.FindName("PART_TextBox", picker) as TextBox;

    private void ListenForTypedDate(DatePicker picker)
    {
        picker.ApplyTemplate();
        if (DateEditor(picker) is not { } editor)
            return;
        editor.ApplyTemplate();
        if (editor.Template?.FindName("PART_Watermark", editor) is ContentControl hint)
            hint.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("[DatePickerPrompt]") { Source = L.Text });
        if (picker.Template?.FindName("PART_Button", picker) is Button button)
        {
            string name = picker == StartDate ? L.T("시작일 달력 열기", "Open start date calendar") : L.T("종료일 달력 열기", "Open end date calendar");
            AutomationProperties.SetName(button, name);
            button.ToolTip = name;
        }
        editor.TextChanged -= SearchChanged;
        editor.TextChanged += SearchChanged;
    }

    private static void SetDate(DatePicker picker, DateTime? value)
    {
        picker.SelectedDate = value;
        if (!value.HasValue)
        {
            picker.Text = string.Empty;
            DateEditor(picker)?.Clear();
        }
    }

    private void DateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || editing)
            return;
        if (sender == StartDate)
            startDayOnly = string.IsNullOrWhiteSpace(StartTime.Text);
        else
            endDayOnly = string.IsNullOrWhiteSpace(EndTime.Text);
        RefreshSearch();
    }

    private void ClearBound(object sender, RoutedEventArgs e)
    {
        bool start = sender == ClearStart;
        if (start)
            ++startResetRevision;
        else
            ++endResetRevision;
        editing = true;
        try
        {
            SetDate(start ? StartDate : EndDate, null);
            (start ? StartTime : EndTime).Clear();
            if (start)
                startDayOnly = true;
            else
                endDayOnly = true;
        }
        finally { editing = false; }
        (start ? StartDate : EndDate).Focus();
        RefreshSearch();
    }

    private void DateInvalid(object sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        // DatePicker discards invalid input on blur. Keep it visible for correction
        // so an invalid constraint cannot silently become an earlier, valid query.
        var picker = (DatePicker)sender;
        string invalid = e.Text;
        var previous = picker.SelectedDate;
        long revision = picker == StartDate ? startResetRevision : endResetRevision;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (revision == (picker == StartDate ? startResetRevision : endResetRevision) && picker.SelectedDate == previous && DateEditor(picker) is { } editor && !picker.IsDropDownOpen)
            {
                editor.Text = invalid;
                RefreshSearch();
            }
        });
    }

    private void OpenCalendar(object sender, RoutedEventArgs e)
    {
        var picker = (DatePicker)sender;
        if (picker.SelectedDate.HasValue)
            return;
        var other = picker == StartDate ? EndDate : StartDate;
        if (other.SelectedDate is DateTime partner)
        {
            picker.DisplayDate = partner;
            return;
        }
        long? stamp = selected?.Timestamp ?? Records.Items.OfType<GlobalLogRow>().FirstOrDefault(row => row.Timestamp.HasValue)?.Timestamp;
        if (stamp is long value && DateTime.TryParseExact(GlobalLogFilter.TimeText(value, utc, "yyyy-MM-dd"),
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor))
            picker.DisplayDate = anchor;
    }

    private void ClockChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || editing)
            return;
        var clock = (TextBox)sender;
        string text = clock.Text.Trim();
        if (text.Length >= 10 && text[4] == '-' && text[7] == '-' && GlobalLogFilter.TryTime(text, utc, out long stamp))
        {
            editing = true;
            try
            {
                SetDate(clock == StartTime ? StartDate : EndDate, DateTime.ParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture));
                clock.Text = text.Length == 16 || text.Length == 19 || text.Length > 20
                    ? text[11..] : GlobalLogFilter.TimeText(stamp, utc, "HH:mm:ss");
                clock.CaretIndex = clock.Text.Length;
            }
            finally { editing = false; }
        }
        if (clock == StartTime)
            startDayOnly = string.IsNullOrWhiteSpace(clock.Text);
        else
            endDayOnly = string.IsNullOrWhiteSpace(clock.Text);
        RefreshSearch();
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy-M-d", "d"];
    private static bool TryBoundary(DatePicker picker, TextBox clock, bool upper, JournalTimeZone useUtc, out long? result, out Control problem)
    {
        result = null;
        problem = picker;
        string dateText = (DateEditor(picker)?.Text ?? picker.Text ?? "").Trim();
        string time = clock.Text.Trim();
        if (dateText.Length == 0)
            return time.Length == 0;
        if (!DateTime.TryParseExact(dateText, DateFormats, picker.Language.GetSpecificCulture(), DateTimeStyles.AllowWhiteSpaces, out var date))
            return false;
        string boundary = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (time.Length == 0 ? "" : " " + time);
        bool valid = upper ? GlobalLogFilter.TryUpperTime(boundary, useUtc, out long value) : GlobalLogFilter.TryTime(boundary, useUtc, out value);
        if (!valid)
        {
            if (time.Length > 0)
                problem = clock;
            return false;
        }
        result = value;
        return true;
    }

    private static void ConvertBound(DatePicker picker, TextBox clock, bool upper, JournalTimeZone oldUtc, JournalTimeZone newUtc, bool dayOnly)
    {
        if (!TryBoundary(picker, clock, upper, oldUtc, out var stamp, out _) || !stamp.HasValue)
            return;
        string format = clock.Text.Trim().Length switch
        {
            0 when !upper => "HH:mm:ss",
            5 => "HH:mm",
            8 => "HH:mm:ss",
            >= 10 and <= 16 and var length => "HH:mm:ss." + new string('f', length - 9),
            _ => "HH:mm:ss.fffffff"
        };
        string text = GlobalLogFilter.TimeText(stamp.Value - (upper ? 1 : 0), newUtc, "yyyy-MM-dd HH:mm:ss.fffffff");
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return;
        SetDate(picker, date.Date);
        clock.Text = dayOnly && date.TimeOfDay.Ticks == (upper ? TimeSpan.TicksPerDay - 1 : 0)
            ? "" : date.ToString(format, CultureInfo.InvariantCulture);
    }

    private void ZoneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ready)
            return;
        JournalTimeZone oldUtc = utc;
        var picked = DisplayTimeZone.Current.Zone;
        if (picked == utc)
            return;
        utc = picked;
        UpdateSortLabel();
        editing = true;
        try
        {
            ConvertBound(StartDate, StartTime, false, oldUtc, utc, startDayOnly);
            ConvertBound(EndDate, EndTime, true, oldUtc, utc, endDayOnly);
        }
        finally { editing = false; }
        if (displayedFilter != null && Records.ItemsSource is IReadOnlyList<GlobalLogRow> oldRows)
        {
            long? position = SelectedPosition;
            var newRows = oldRows.Select(row => row with { TimeZone = utc }).ToList();
            var replacement = newRows.FirstOrDefault(row => row.Position == position);
            restoringSort = replacement != null;
            try
            {
                Records.ItemsSource = newRows;
                if (replacement != null)
                {
                    Records.SelectedItem = selected = replacement;
                    if (rawRequested?.Position == replacement.Position)
                        rawRequested = replacement;
                    OverviewPanel.SetTime(replacement.Record, utc);
                }
            }
            finally { restoringSort = false; }
        }
        else if (IsVisible && query != null)
        {
            debounce.Stop();
            LoadPage();
        }
        Buttons();
    }
    private (GlobalLogFilter? Value, Control? Invalid, string Message) Filter()
    {
        if (PathFilters.Criteria is not { } recordFields)
            return (null, null, "");
        if (!TryBoundary(StartDate, StartTime, false, utc, out var from, out var startIssue))
            return (null, startIssue, startIssue == StartDate ? L.T("시작일을 달력에서 선택하거나 연도-월-일로 입력하세요.", "Select a start date or enter it as YYYY-MM-DD.") : L.T("시작 시각을 14:30 또는 14:30:00.125 형식으로 입력하세요.", "Enter the start time as 14:30 or 14:30:00.125."));
        if (!TryBoundary(EndDate, EndTime, true, utc, out var until, out var endIssue))
            return (null, endIssue, endIssue == EndDate ? L.T("종료일을 달력에서 선택하거나 연도-월-일로 입력하세요.", "Select an end date or enter it as YYYY-MM-DD.") : L.T("종료 시각을 15:00 또는 15:00:00.125 형식으로 입력하세요.", "Enter the end time as 15:00 or 15:00:00.125."));
        if (from.HasValue && until.HasValue && from >= until)
            return (null, string.IsNullOrWhiteSpace(EndTime.Text) ? EndDate : EndTime, L.T("종료 날짜나 시각을 조정하세요. 시작보다 앞서 있습니다.", "The end is before the start. Adjust the end date or time."));
        long? usn = null;
        if (!string.IsNullOrWhiteSpace(ExactUsn.Text))
        {
            if (!GlobalLogFilter.TryUsn(ExactUsn.Text, out long number))
                return (null, ExactUsn, L.T("USN은 10진수 또는 0x로 시작하는 16진수로 입력하세요.", "Enter the USN in decimal, or in hexadecimal with a 0x prefix."));
            usn = number;
        }
        return (new(QueryText.Text.Trim(), NameScope.IsChecked == true, reasonMask, usn, from, until)
        {
            RecordFields = recordFields
        }, null, "");
    }

    private void ClearFilterFeedback()
    {
        if (invalidFilter != null)
        {
            invalidFilter.ClearValue(Control.BorderBrushProperty);
            invalidFilter.ClearValue(AutomationProperties.HelpTextProperty);
            invalidFilter = null;
        }
        RecoverFilter.Visibility = Visibility.Collapsed;
    }

    private void OfferRecovery(string action)
    {
        RecoverFilter.Content = action;
        RecoverFilter.Visibility = Visibility.Visible;
    }

    private void RecoverFilterClick(object sender, RoutedEventArgs e)
    {
        if (invalidFilter is { } field)
        {
            More.IsChecked = true;
            UpdateLayout();
            var editor = field is DatePicker picker ? DateEditor(picker) : field as TextBox;
            if (editor != null)
            {
                editor.Focus();
                editor.SelectAll();
            }
            else
                field.Focus();
            return;
        }
        if (!string.IsNullOrWhiteSpace(QueryText.Text))
            ClearQueryClick(sender, e);
        else
        {
            ClearClick(sender, e);
            FocusSearch();
        }
    }
    private void PageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedValue: int size } || size == pageSize)
            return;
        PageSizeOption.Remember(size);
        pageSize = size;
        if (ready && query != null)
            ReloadSort();
    }

    private void PrevClick(object sender, RoutedEventArgs e)
    {
        if (loading || previous.Count == 0)
            return;
        cursor = previous.Pop();
        LoadPage();
    }
    private void NextClick(object sender, RoutedEventArgs e)
    {
        if (loading || next == null)
            return;
        previous.Push(cursor);
        cursor = next;
        LoadPage();
    }
    private static bool HasBound(DatePicker picker, TextBox clock) =>
        !string.IsNullOrWhiteSpace(DateEditor(picker)?.Text ?? picker.Text) || !string.IsNullOrWhiteSpace(clock.Text);
    private void Buttons()
    {
        PageLimit.IsEnabled = !loading && query != null;
        Prev.IsEnabled = !loading && previous.Count != 0;
        Next.IsEnabled = !loading && next != null;
        FilterPanel.IsEnabled = query != null;
        UpdateSortLabel();
        bool hasStart = HasBound(StartDate, StartTime), hasEnd = HasBound(EndDate, EndTime);
        ClearQuery.Visibility = QueryText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearStart.Visibility = hasStart ? Visibility.Visible : Visibility.Hidden;
        ClearEnd.Visibility = hasEnd ? Visibility.Visible : Visibility.Hidden;
        int boundCount = (hasStart ? 1 : 0) + (hasEnd ? 1 : 0)
            + (string.IsNullOrWhiteSpace(ExactUsn.Text) ? 0 : 1);
        bool hasUsn = !string.IsNullOrWhiteSpace(ExactUsn.Text);
        bool canReset = !string.IsNullOrWhiteSpace(QueryText.Text) || NameScope.IsChecked != true || reasonMask != 0 || boundCount > 0 || PathFilters.HasInput;
        ClearFilters.Visibility = canReset ? Visibility.Visible : Visibility.Collapsed;
        string start = (DateEditor(StartDate)?.Text ?? StartDate.Text ?? "").Trim();
        string end = (DateEditor(EndDate)?.Text ?? EndDate.Text ?? "").Trim();
        if (hasStart && start.Length == 0)
            start = L.T("시작일 미입력", "Start date missing");
        if (hasEnd && end.Length == 0)
            end = L.T("종료일 미입력", "End date missing");
        string range = (hasStart, hasEnd) switch
        {
            (true, true) when start == end && start.Length > 0 => start,
            (true, true) => $"{start} ~ {end}",
            (true, false) => L.T("{0}부터", "From {0}", start),
            (false, true) => L.T("{0}까지", "Through {0}", end),
            _ => ""
        };
        PeriodLabel.Text = boundCount == 0 ? L.T("기간 필터", "Date filter") : range.Length == 0 ? $"USN {ExactUsn.Text.Trim()}" :
            hasUsn ? $"{range} · USN" : range;
        var bounds = new List<string>();
        if (hasStart)
            bounds.Add(L.T("시작  {0} {1}", "Start  {0} {1}", DateEditor(StartDate)?.Text, string.IsNullOrWhiteSpace(StartTime.Text) ? "00:00" : StartTime.Text));
        if (hasEnd)
            bounds.Add(L.T("종료  {0} {1}", "End  {0} {1}", DateEditor(EndDate)?.Text, string.IsNullOrWhiteSpace(EndTime.Text) ? "23:59" : EndTime.Text));
        if (hasUsn)
            bounds.Add($"USN  {ExactUsn.Text}");
        if (hasStart || hasEnd)
            bounds.Add(utc.Code);
        More.ToolTip = boundCount == 0 ? L.T("시작일, 종료일 또는 USN으로 검색합니다.", "Filter by start date, end date, or USN.") : string.Join("\n", bounds);
        AutomationProperties.SetItemStatus(More, boundCount == 0 ? L.T("조건 없음", "No active filters") : More.ToolTip.ToString());
        var reasons = ReasonChecks.Where(check => check.IsChecked == true).Select(check => check.Content.ToString()).ToArray();
        KindLabel.Text = reasons.Length switch
        {
            0 => L.T("변경 사유", "Change reason"),
            1 => reasons[0],
            _ => L.T("변경 사유 ({0})", "Change reasons ({0})", reasons.Length)
        };
        Kind.ToolTip = reasons.Length == 0 ? L.T("선택한 사유 중 하나라도 포함한 기록을 표시합니다.", "Show records that match any selected reason.") : string.Join(" · ", reasons);
        AutomationProperties.SetItemStatus(Kind, reasons.Length == 0 ? L.T("모든 사유", "All reasons") : Kind.ToolTip.ToString());
        ClearReasons.IsEnabled = reasons.Length > 0;
        ClearFilters.IsEnabled = query != null && canReset;
        SaveCsv.IsEnabled = export == null && displayedFilter != null && Records.Items.Count > 0;
        ResultsExportMenu.Visibility = export == null ? Visibility.Visible : Visibility.Collapsed;
        CancelCsv.Visibility = export == null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void QueryStatus(string text)
    {
        Empty.Text = Records.HasItems ? "" : text;
        PageStatus.Text = Records.HasItems ? text : "";
    }
    internal sealed record SavedFilters(long Reasons, string Usn, DateTime? Start, DateTime? End, string StartClock,
        string EndClock, bool StartDay, bool EndDay, JournalTimeZone TimeZone, RecordSearchFields.SavedFields Fields,
        JournalSort Sort, bool Descending);

    internal SavedFilters CaptureFilters() => new(reasonMask, ExactUsn.Text, StartDate.SelectedDate, EndDate.SelectedDate,
        StartTime.Text, EndTime.Text, startDayOnly, endDayOnly, utc, PathFilters.CaptureFields(), sort, descending);

    internal void ResumeSearch(string search, bool includePreviousNames, long? position, SavedFilters? saved)
    {
        ClearInputs();
        editing = true;
        try
        {
            QueryText.Text = search;
            NameScope.IsChecked = includePreviousNames;
            if (saved != null)
            {
                foreach (var check in ReasonChecks)
                    check.IsChecked = (saved.Reasons & (long)check.Tag) != 0;
                reasonMask = ReasonChecks.Where(check => check.IsChecked == true).Aggregate(0L, (mask, check) => mask | (long)check.Tag);
                ExactUsn.Text = saved.Usn ?? "";
                SetDate(StartDate, saved.Start);
                SetDate(EndDate, saved.End);
                StartTime.Text = saved.StartClock ?? "";
                EndTime.Text = saved.EndClock ?? "";
                startDayOnly = saved.StartDay;
                endDayOnly = saved.EndDay;
                if (saved.TimeZone != utc && saved.TimeZone.OffsetMinutes is >= -840 and <= 840)
                {
                    ConvertBound(StartDate, StartTime, false, saved.TimeZone, utc, startDayOnly);
                    ConvertBound(EndDate, EndTime, true, saved.TimeZone, utc, endDayOnly);
                }
                PathFilters.RestoreFields(saved.Fields);
                sort = Enum.IsDefined(saved.Sort) ? saved.Sort : JournalSort.Time;
                descending = saved.Descending;
            }
        }
        finally { editing = false; }
        debounce.Stop();
        Restart();
        UpdateSortLabel();
        LoadPage(savedPosition: position);
    }

    private async void LoadPage(GlobalLogRow? anchor = null, long? savedPosition = null)
    {
        if (!ready || !IsVisible)
            return;
        request?.Cancel();
        dirty = false;
        var backend = query;
        if (backend == null)
        {
            Buttons();
            return;
        }
        using var active = new CancellationTokenSource();
        request = active;
        loading = true;
        displayedFilter = null;
        next = null;
        Buttons();
        if (anchor == null)
        {
            Records.ItemsSource = null;
            ShowRecord(null);
        }
        ClearFilterFeedback();
        QueryStatus(L.T("기록을 불러오고 있습니다…", "Loading records…"));
        try
        {
            var (filter, invalid, error) = Filter();
            if (filter == null && invalid != null)
            {
                invalidFilter = invalid;
                invalid.SetResourceReference(Control.BorderBrushProperty, "Desk.WarnText");
                AutomationProperties.SetHelpText(invalid, error);
                Empty.Text = error;
                string field = invalid == StartDate ? L.T("시작일", "Start date") : invalid == EndDate ? L.T("종료일", "End date") : invalid == StartTime ? L.T("시작 시각", "Start time") : invalid == EndTime ? L.T("종료 시각", "End time") : "USN";
                OfferRecovery(L.T("{0} 수정", "Edit {0}", field));
                return;
            }
            if (filter == null)
            {
                Empty.Text = PageStatus.Text = "";
                return;
            }
            IReadOnlyList<long>? order = null;
            if (sort is not (JournalSort.Time or JournalSort.Usn))
            {
                if (sorted is { } prior && prior.Filter == filter && prior.Sort == sort)
                    order = prior.Order;
                else
                {
                    var key = sort;
                    QueryStatus(L.T("검색 결과 전체를 정렬하고 있습니다…", "Sorting all matching records…"));
                    if (key is JournalSort.Path or JournalSort.PathSource)
                    {
                        if (pathOrders is not { } cache || cache.Filter != filter)
                        {
                            var journal = source!;
                            var resolver = paths;
                            var warning = pairWarning;
                            bool carved = IsCarvedSource;
                            var progress = new Progress<int>(count =>
                            {
                                if (ReferenceEquals(request, active) && !active.IsCancellationRequested && displayedFilter == null)
                                    QueryStatus(L.T("경로 확인 중 · {0:N0}건", "Resolving paths · {0:N0} records", count));
                            });
                            var pair = allPathOrders is { } all
                                ? filter.IsAllRecords ? all : await backend.FilterPathOrdersAsync(filter, all, active.Token)
                                : await backend.ReusablePathOrdersAsync(filter,
                                    row => journal.ResolveListValues(row.Record, row.FileId, row.Name, resolver, warning, carved),
                                    resolver?.MftIndexPath, carved, warning != null, active.Token, progress);
                            active.Token.ThrowIfCancellationRequested();
                            if (filter.IsAllRecords)
                                allPathOrders = pair;
                            cache = (filter, pair.Paths, pair.Sources);
                            pathOrders = cache;
                        }
                        order = key == JournalSort.Path ? cache.Paths : cache.Sources;
                    }
                    else if (labelOrders.TryGetValue(key, out var cached) && cached.Filter == filter)
                        order = cached.Order;
                    else
                    {
                        order = await backend.BuildLabelOrderAsync(filter, key == JournalSort.Reason, active.Token);
                        active.Token.ThrowIfCancellationRequested();
                        labelOrders[key] = (filter, order);
                    }
                    active.Token.ThrowIfCancellationRequested();
                    sorted = (filter, key, order);
                }
            }
            if (anchor != null)
                anchor = selected;
            if ((savedPosition ?? anchor?.Position) is long position)
            {
                var starts = await backend.FindPageStarts(filter, sort, descending, order, position, pageSize, active.Token);
                active.Token.ThrowIfCancellationRequested();
                previous.Clear();
                foreach (var start in starts.Take(starts.Count - 1))
                    previous.Push(start);
                cursor = starts[^1];
            }
            JournalTimeZone requestedUtc = utc;
            var page = await backend.ReadAsync(filter, descending, requestedUtc, cursor, pageSize, active.Token, sort, order);
            if (active.IsCancellationRequested || !ReferenceEquals(request, active))
                return;
            if (anchor != null && selected?.Position != anchor.Position)
            {
                ReloadSort();
                return;
            }
            displayedFilter = filter;
            next = page.Next;
            if (requestedUtc != utc)
                page = page with
                {
                    Items = page.Items.Select(row => row with { TimeZone = utc }).ToList()
                };
            var restorePosition = savedPosition ?? anchor?.Position;
            var restore = restorePosition == null ? null : page.Items.FirstOrDefault(row => row.Position == restorePosition);
            restoringSort = restore != null && selected?.Position == restore.Position;
            try
            {
                Records.ItemsSource = page.Items;
                if (restore != null)
                    Records.SelectedItem = restore;
            }
            finally { restoringSort = false; }
            int n = page.Items.Count;
            Empty.Text = n != 0 ? "" : L.T("검색 조건에 맞는 기록이 없습니다.", "No records match your search.");
            if (n == 0 && !filter.IsAllRecords)
                OfferRecovery(filter.Name.Length != 0 ? L.T("파일 이름 검색 지우기", "Clear file name search") : L.T("필터 초기화", "Clear filters"));
            long first = (long)previous.Count * pageSize + 1;
            PageStatus.Text = n == 0 ? "" : previous.Count == 0 && next == null ? L.T("{0:N0}건", "Records: {0:N0}", n) : L.T("{0:N0}–{1:N0}번째 기록", "Records {0:N0}–{1:N0}", first, first + n - 1);
            if (n != 0)
            {
                if ((filter.Usn.HasValue || !filter.RecordFields.IsEmpty) && n == 1 && previous.Count == 0 && next == null)
                    Records.SelectedItem = page.Items[0];
                if (focusPending && QueryText.IsKeyboardFocusWithin)
                    FocusSelectedRecord();
                else
                    Records.ScrollIntoView(restore ?? page.Items[0]);
            }
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!active.IsCancellationRequested)
                QueryStatus(L.T("전체 기록을 열지 못했습니다. ", "Could not load journal records. ") + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(request, active))
            {
                request = null;
                loading = false;
                focusPending = false;
                Buttons();
            }
        }
    }

    private void ExportMessage(string text)
    {
        ExportStatus.Text = text;
        ExportStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void CancelExport(object sender, RoutedEventArgs e) => export?.Cancel();

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (export != null)
            return;
        var backend = query;
        var filter = displayedFilter;
        if (backend == null || filter == null)
            return;
        bool includePaths = sender == ExportPathsCsv;
        var dialog = new SaveFileDialog
        {
            InitialDirectory = DeskSettings.InitialExportDirectory,
            Title = L.T("검색 결과 전체를 새 CSV 파일로 저장", "Save all matching records to a new CSV file"),
            Filter = L.T("CSV 파일 (*.csv)|*.csv", "CSV files (*.csv)|*.csv"),
            FileName = includePaths ? L.T("저널_검색_결과_경로.csv", "Journal search results with paths.csv") : L.T("저널_검색_결과.csv", "Journal search results.csv"),
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = false
        };
        var owner = Window.GetWindow(this);
        if (dialog.ShowDialog(owner) != true)
            return;
        using var active = new CancellationTokenSource();
        export = active;
        Buttons();
        void CancelOnClose(object? source, EventArgs args) => active.Cancel();
        if (owner != null)
            owner.Closed += CancelOnClose;
        ExportMessage(L.T("전체 검색 결과를 저장하고 있습니다…", "Saving all matching records…"));
        var progress = new Progress<long>(count => { if (ReferenceEquals(active, export)) ExportMessage(L.T("{0:N0}건을 저장하고 있습니다…", "Records written: {0:N0}…", count)); });
        try
        {
            var journal = source;
            var resolver = paths;
            var warning = pairWarning;
            bool carved = IsCarvedSource;
            Func<GlobalLogRow, PathResult>? resolve = includePaths && journal != null
                ? row => journal.ResolveRecordPath(row.Record, row.FileId, resolver, warning, carved) : null;
            long count = await backend.ExportAsync(dialog.FileName, original ?? "", filter, descending, utc, active.Token, progress, carved, resolve, mftHash, mftPath, sort,
                sorted is { } index && index.Filter == filter && index.Sort == sort ? index.Order : null);
            if (ReferenceEquals(active, export))
                SavedOutput.Show(ExportStatus, dialog.FileName, L.T("기록 {0:N0}건 저장", "{0:N0} records saved", count));
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested)
        {
            if (ReferenceEquals(active, export))
                ExportMessage(L.T("CSV 저장을 취소했습니다.", "CSV export cancelled."));
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(active, export))
                ExportMessage(L.T("CSV 저장 실패 · ", "CSV export failed · ") + ex.Message);
        }
        finally
        {
            if (owner != null)
                owner.Closed -= CancelOnClose;
            if (ReferenceEquals(active, export))
            {
                export = null;
                Buttons();
            }
        }
    }

    private void RecordChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && !restoringSort && ReferenceEquals(e.OriginalSource, Records))
            ShowRecord(Records.SelectedItem as GlobalLogRow);
    }
    private async void ShowRecord(GlobalLogRow? row)
    {
        long revision = ++selectionRevision;
        selected = row;
        rawRequested = null;
        rawData = null;
        RecordJson.Clear();
        Detach.IsEnabled = false;
        if (row == null)
        {
            Inspector.Visibility = Split.Visibility = Visibility.Collapsed;
            DetailHeight.MinHeight = 0;
            DetailHeight.Height = new(0);
            Raw.ClearView();
            return;
        }
        if (Inspector.Visibility != Visibility.Visible)
        {
            Inspector.Visibility = Split.Visibility = Visibility.Visible;
            DetailHeight.MinHeight = 90;
            FitDetail();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (selectionRevision == revision && Inspector.IsVisible)
                    Records.ScrollIntoView(row);
            });
        }
        NearbyGlobal.Visibility = !IsCarvedSource && original != null ? Visibility.Visible : Visibility.Collapsed;
        RefreshRecordText(row);
        Raw.ClearView();
        if (OriginalBytesTab.IsSelected)
            LoadRaw();
        await UpdateRecordPath(row.Record, row.FileId, revision);
    }

    private void RefreshRecordText(GlobalLogRow row)
    {
        var entry = row.Record;
        RecordTitle.Text = entry.Version == 4 ? L.T("변경 범위 기록", "Changed extents record") : entry.RecordedName;
        var fields = UsnEvidence.RecordFields(entry, row.FileId);
        if (original != null)
            fields.Add(new(L.T("원본 파일", "Source file"), original));
        RecordFields.SetFields(fields);
        if (JsonTab.IsSelected)
            ShowRecordJson();
        var notes = new List<string>();
        if (entry.Version == 4)
            notes.Add(entry.ExtentSummary);
        if (entry.Resynced)
            notes.Add(L.T("손상된 구간 직후에 형식을 다시 확인한 첫 기록입니다.", "First valid record after a damaged region."));
        if (entry.Escaped)
            notes.Add(L.T("읽을 수 없는 파일명 부분은 원래 UTF-16 코드값으로 표시했습니다.", "Undecodable name characters are displayed as their original UTF-16 code units."));
        string location = original == null ? entry.RawLocation : Path.GetFileName(original) + "   ·   " + entry.RawLocation;
        OverviewPanel.SetRecord(entry, row.FileId, utc, row.ReasonsText, location, string.Join(Environment.NewLine, notes));
    }

    private async Task UpdateRecordPath(Entry entry, byte[] file, long revision)
    {
        if (rowPaths is not { } pathSource)
        {
            OverviewPanel.SetPath(L.T("경로를 확인할 수 없습니다.", "Path unavailable."), "");
            return;
        }
        try
        {
            var result = await pathSource.ReadAsync(entry, file, CancellationToken.None);
            if (selectionRevision != revision || !ReferenceEquals(pathSource, rowPaths))
                return;
            OverviewPanel.SetPath(result.Display, result.Explanation, entry.Name);
        }
        catch (Exception ex)
        {
            if (selectionRevision == revision && ReferenceEquals(pathSource, rowPaths))
                OverviewPanel.SetPath(L.T("경로를 읽지 못했습니다.", "Could not resolve the path."), ex.Message);
        }
    }
    private void EvidenceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || !ReferenceEquals(e.OriginalSource, Evidence))
            return;
        if (Inspector.IsVisible && OriginalBytesTab.IsSelected)
            LoadRaw();
        else if (Inspector.IsVisible && JsonTab.IsSelected)
            ShowRecordJson();
    }
    private void ShowRecordJson()
    {
        if (RecordJson.Text.Length == 0 && selected is { } row)
            RecordJson.Text = UsnEvidence.RecordJson(row.Record, row.FileId, original, digest);
    }

    private async void LoadRaw()
    {
        var row = selected;
        var path = original;
        long revision = selectionRevision;
        if (row == null)
            return;
        if (ReferenceEquals(rawRequested, row))
            return;
        if (path == null)
        {
            Raw.ClearView(L.T("원본 변경 저널을 열면 바이트를 확인할 수 있습니다.", "Open the source USN Journal to inspect the bytes."));
            return;
        }
        rawRequested = row;
        Raw.ClearView(L.T("원본 바이트를 읽고 있습니다…", "Reading source bytes…"));
        try
        {
            var data = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return UsnEvidence.Read(stream, row.Record, row.FileId);
            });
            if (selectionRevision != revision)
                return;
            UsnEvidence.Display(Raw, data, row.Record);
            rawData = data;
            Detach.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (selectionRevision == revision)
            {
                rawRequested = null;
                Raw.ClearView(L.T("원본에서 읽지 못했습니다. ", "Could not read from the source. ") + ex.Message);
            }
        }
    }
    private void DetachEvidence(object sender, RoutedEventArgs e)
    {
        if (rawData != null && selected != null && original != null)
            new EvidenceWindow(original, selected.Record, rawData, Raw.SelectedOffset, Raw.SelectionAnchor) { Owner = Window.GetWindow(this) }.Show();
    }
    private void OpenNeighborhood(object sender, RoutedEventArgs e)
    {
        if (selected == null || source == null || original == null || IsCarvedSource)
            return;
        try
        {
            new NeighborhoodWindow(source, original, selected.Record, utc, selected.FileId) { Owner = Window.GetWindow(this) }.Show();
        }
        catch (Exception ex) { ExportMessage(L.T("주변 기록을 열지 못했습니다. ", "Could not open nearby records. ") + ex.Message); }
    }
    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Inspector.Visibility = Split.Visibility = Visibility.Collapsed;
        DetailHeight.MinHeight = 0;
        DetailHeight.Height = new(0);
        FocusSelectedRecord();
    }
    private void ReopenRecord(object sender, MouseButtonEventArgs e)
    {
        if (!Inspector.IsVisible && selected != null && e.OriginalSource is DependencyObject target &&
            ItemsControl.ContainerFromElement(Records, target) is DataGridRow { IsSelected: true })
            RevealRecord();
    }
    private void RevealRecord()
    {
        Inspector.Visibility = Split.Visibility = Visibility.Visible;
        DetailHeight.MinHeight = 90;
        FitDetail();
    }
    private void OpenFile(object sender, RoutedEventArgs e)
    {
        if (selected != null)
            FileRequested?.Invoke(selected.FileId);
    }
    private void DoubleClickRow(object sender, MouseButtonEventArgs e)
    {
        if (FileRequested != null && e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(Records, origin) is DataGridRow)
        {
            e.Handled = true;
            OpenFile(sender, e);
        }
    }
    private void GridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || selected == null)
            return;
        if (e.OriginalSource is DependencyObject focus && focus != Records &&
            ItemsControl.ContainerFromElement(Records, focus) is not DataGridRow)
            return;
        if (!Inspector.IsVisible)
        {
            RevealRecord();
            e.Handled = true;
        }
        else if (FileRequested != null)
        {
            e.Handled = true;
            OpenFile(sender, e);
        }
    }
    private void ControlKeyDown(object sender, KeyEventArgs e)
    {
        if (EvidencePaneNavigation.MoveFocus(e, Records, Inspector, Evidence))
            return;
        if (KindPopup.IsOpen && e.Key == Key.Escape)
        {
            CloseReasons(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && (StartDate.IsDropDownOpen || EndDate.IsDropDownOpen))
            return;
        if (e.Key == Key.Escape && Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true } or ComboBoxItem or MenuItem)
            return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            FocusSearch();
        }
        else if (e.Key == Key.Escape && Inspector.IsVisible && selected != null)
        {
            if (Raw.IsKeyboardFocusWithin && Raw.SelectionLength > 1)
                return;
            e.Handled = true;
            CloseClick(sender, e);
        }
    }
}
