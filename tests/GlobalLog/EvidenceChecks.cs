using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using JournalDesk;
using Microsoft.Data.Sqlite;

static partial class Program
{
    static async Task CheckEvidenceOrderAsync(string path)
    {
        var q = new GlobalLogQuery(path);
        var watch = Stopwatch.StartNew();
        var first = await q.ReadAsync("", true, null, 100, CancellationToken.None);
        Console.WriteLine($"First 100 newest: {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        var second = await q.ReadAsync("", true, first.Next, 100, CancellationToken.None);
        Console.WriteLine($"Next 100: {watch.ElapsedMilliseconds} ms");
        Require(!first.Items.Select(x => x.Position).Intersect(second.Items.Select(x => x.Position)).Any(), "overlapping pages");
        watch.Restart();
        var positions = await Traverse(q, "", false, 4096);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT pos FROM records ORDER BY stamp ASC NULLS LAST,usn ASC,pos ASC";
        using var r = c.ExecuteReader();
        int n = 0;
        while (r.Read())
        {
            Require(n < positions.Count && positions[n] == r.GetInt64(0), "missing, duplicate or displaced record");
            n++;
        }
        Require(n == positions.Count, "unexpected trailing rows");
        Console.WriteLine($"Full ascending traversal independently matched all {n} records in {watch.ElapsedMilliseconds} ms.");
    }

    static async Task CheckEvidenceAndViewAsync(string path, string source)
    {
        var q = new GlobalLogQuery(path);
        string csv = Path.Combine(Path.GetTempPath(), "journaldesk-result-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var filter = new GlobalLogFilter(Reasons: 0x3000);
            long count = await q.ExportAsync(csv, source, filter, true, false, CancellationToken.None);
            string hash;
            using (var file = File.OpenRead(source))
                hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT name,reason,hex(file),hex(parent),pos,usn,stamp FROM records WHERE (reason & 12288)!=0 ORDER BY stamp DESC NULLS LAST,usn DESC,pos DESC";
            using var reference = cmd.ExecuteReader();
            using var csvFile = Csv(csv);
            var header = csvFile.ReadFields()!;
            long compared = 0;
            while (reference.Read())
            {
                var actual = csvFile.ReadFields()!;
                Require(actual.Length == header.Length && actual[0] == reference.GetString(0) && actual[3] == $"0x{reference.GetInt64(1):X8}" &&
                    actual[5] == reference.GetString(2) && actual[6] == reference.GetString(3) && actual[7] == reference.GetInt64(4).ToString(CultureInfo.InvariantCulture) &&
                    actual[4] == reference.GetInt64(5).ToString(CultureInfo.InvariantCulture) && actual[9] == reference.GetInt64(6).ToString(CultureInfo.InvariantCulture) && actual[10] == source && actual[11] == hash, "real filtered export and raw source diverged");
                compared++;
            }
            Require(compared > 200 && compared == count && csvFile.EndOfData, "CSV stopped early or contained extra records");
            Console.WriteLine($"Verified {count} filtered CSV rows against independent SQL and original SHA-256 {hash}.");
            string cancelled = csv + ".cancel.csv";
            using var stop = new CancellationTokenSource();
            try
            {
                await q.ExportAsync(cancelled, source, new(), false, true, stop.Token, new InlineProgress(_ => stop.Cancel()));
                throw new InvalidOperationException("running export ignored cancellation");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            Require(!File.Exists(cancelled), "cancelled CSV published a partial result");
            var target = (await q.ReadAsync("", true, null, 1, CancellationToken.None)).Items[0];
            string priorSettings = DeskSettings.DirectoryPath;
            var settings = Directory.CreateTempSubdirectory("journaldesk-log-settings-");
            try
            {
                DeskSettings.DirectoryPath = settings.FullName;
                DisplayTimeZone.Current.Code = "KST";
                await CheckUi(path, source, target);
            }
            finally
            {
                DeskSettings.DirectoryPath = priorSettings;
                settings.Delete(true);
            }
            Console.WriteLine($"WPF verified next / prior page, decimal and hexadecimal USN {target.Usn}, UTC conversion, raw bytes at 0x{target.Position:X}, path and file / parent reference filters, invalid input, and export cancellation.");
        }
        finally { File.Delete(csv); }
    }

    static Task CheckUi(string database, string source, GlobalLogRow target)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            // This host exercises the journal independently of the application's startup window.
            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/NtfsTracer;component/DeskResources.xaml", UriKind.Relative) });
            var view = new GlobalLogView();
            var host = new Window { Content = view, ShowInTaskbar = false, ShowActivated = false, Width = 1050, Height = 600, Left = -16000, Top = -16000 };
            T Find<T>(string name) where T : class => (T)view.FindName(name);
            async Task Until(Func<bool> predicate)
            {
                var elapsed = Stopwatch.StartNew();
                while (!predicate())
                {
                    if (elapsed.ElapsedMilliseconds > 15000)
                        throw new TimeoutException("WPF result did not arrive");
                    await Task.Delay(20);
                }
            }
            host.Loaded += async (_, _) =>
            {
                try
                {
                    view.SetSource(new Journal(database), null, source);
                    var grid = Find<DataGrid>("Records");
                    await Until(() => grid.Items.Count == 200 && Find<Button>("Next").IsEnabled);
                    Require(!Find<Button>("GoFile").IsVisible, "independent view exposes an unavailable file history command");
                    long first = ((GlobalLogRow)grid.Items[0]).Position;
                    DataGridColumnHeadersPresenter? Headers(DependencyObject parent)
                    {
                        if (parent is DataGridColumnHeadersPresenter found)
                            return found;
                        for (int child = 0; child < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); child++)
                            if (Headers(System.Windows.Media.VisualTreeHelper.GetChild(parent, child)) is { } result)
                                return result;
                        return null;
                    }
                    grid.UpdateLayout();
                    var searchBox = Find<HintTextBox>("QueryText");
                    var adorners = System.Windows.Documents.AdornerLayer.GetAdornerLayer(searchBox)?.GetAdorners(searchBox);
                    var hint = adorners?.Select(a => System.Windows.Media.VisualTreeHelper.GetChildrenCount(a) > 0
                        ? System.Windows.Media.VisualTreeHelper.GetChild(a, 0) as TextBlock : null)
                        .SingleOrDefault(label => label?.Text == searchBox.Hint);
                    Require(hint != null && hint.IsVisible, "empty search has no visible input hint");
                    var origin = hint!.TranslatePoint(new Point(), searchBox);
                    var caret = searchBox.GetRectFromCharacterIndex(0);
                    Require(!caret.IsEmpty && Math.Abs(origin.X - caret.X) < 1 && Math.Abs(origin.Y - caret.Y) < 1,
                        "input hint starts at a different position from the insertion caret");
                    var presenter = Headers(grid)!;
                    var timeHeader = (DataGridColumnHeader)presenter.ItemContainerGenerator.ContainerFromIndex(0);
                    Require(timeHeader.Column.CanUserSort && grid.CanUserSortColumns, "sortable timestamp header disabled");
                    void ClickTime() => typeof(DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(timeHeader, null);
                    ClickTime();
                    await Until(() => grid.Items.Count == 200 && ((GlobalLogRow)grid.Items[0]).Position != first);
                    var earliest = (await new GlobalLogQuery(database).ReadAsync("", false, null, 1, CancellationToken.None)).Items[0];
                    Require(((GlobalLogRow)grid.Items[0]).Position == earliest.Position &&
                        timeHeader.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending, "time header only sorted current page");
                    ClickTime();
                    await Until(() => grid.Items.Count == 200 && ((GlobalLogRow)grid.Items[0]).Position == first);
                    Require(timeHeader.Column.SortDirection == System.ComponentModel.ListSortDirection.Descending, "time header lost direction");
                    ClickTime();
                    await Until(() => timeHeader.Column.SortDirection == null && Find<MenuItem>("SaveCsv").IsEnabled);
                    Require(((GlobalLogRow)grid.Items[0]).Position == first, "third header click did not restore default newest-first order");
                    Require(Find<Button>("ClearFilters").Visibility == Visibility.Collapsed, "reset appears without a filter");
                    grid.SelectedIndex = 0;
                    var json = Find<TextBox>("RecordJson");
                    Find<TabItem>("JsonTab").IsSelected = true;
                    using (var record = System.Text.Json.JsonDocument.Parse(json.Text))
                    {
                        Require(record.RootElement.GetProperty("Usn").GetString() == target.Usn.ToString(CultureInfo.InvariantCulture) &&
                            record.RootElement.GetProperty("FileOffset").GetString() == $"0x{first:X}" &&
                            Find<HexEvidenceView>("Raw").ByteCount == 0, "JSON did not match the selected record or it loaded raw bytes");
                    }
                    var hexTab = Find<TabItem>("OriginalBytesTab");
                    hexTab.IsSelected = true;
                    var selectedBytes = Find<HexEvidenceView>("Raw");
                    await Until(() => selectedBytes.BaseOffset == first && selectedBytes.ByteCount >= 8);
                    selectedBytes.SelectRange(7, 2);
                    ClickTime();
                    await Until(() => Find<Button>("Prev").IsEnabled && Find<MenuItem>("SaveCsv").IsEnabled);
                    Require(((GlobalLogRow)grid.SelectedItem).Position == first && view.SelectedPosition == first &&
                        hexTab.IsSelected && selectedBytes.BaseOffset == first && selectedBytes.SelectedOffset == 2 && selectedBytes.SelectionLength == 6,
                        "resorting to a distant page lost selected evidence or reversed Hex selection");
                    ClickTime();
                    await Until(() => !Find<Button>("Prev").IsEnabled && Find<MenuItem>("SaveCsv").IsEnabled);
                    Require(((GlobalLogRow)grid.SelectedItem).Position == first && selectedBytes.SelectedOffset == 2 && selectedBytes.SelectionLength == 6,
                        "sorting back lost the selected record or byte range");
                    ClickTime();
                    await Until(() => timeHeader.Column.SortDirection == null && Find<MenuItem>("SaveCsv").IsEnabled);
                    ClickTime();
                    Require(Find<TextBlock>("Empty").Text.Length == 0 && Find<TextBlock>("PageStatus").Text.Length > 0,
                        "sort progress covers an otherwise readable record");
                    grid.SelectedIndex = 1;
                    long laterSelection = ((GlobalLogRow)grid.SelectedItem).Position;
                    await Until(() => Find<Button>("Prev").IsEnabled && Find<MenuItem>("SaveCsv").IsEnabled);
                    Require(((GlobalLogRow)grid.SelectedItem).Position == laterSelection && view.SelectedPosition == laterSelection,
                        "new manual selection was displaced by a pending sort");
                    ClickTime();
                    await Until(() => !Find<Button>("Prev").IsEnabled && Find<MenuItem>("SaveCsv").IsEnabled);
                    grid.SelectedItem = null;
                    Find<Button>("Next").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => grid.Items.Count == 200 && ((GlobalLogRow)grid.Items[0]).Position != first);
                    Find<Button>("Prev").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => grid.Items.Count == 200 && ((GlobalLogRow)grid.Items[0]).Position == first);
                    var reasons = Find<StackPanel>("ReasonOptions").Children.OfType<CheckBox>().ToList();
                    var create = reasons.Single(box => (long)box.Tag == 0x100);
                    var remove = reasons.Single(box => (long)box.Tag == 0x200);
                    create.IsChecked = true;
                    remove.IsChecked = true;
                    var either = await new GlobalLogQuery(database).ReadAsync(new GlobalLogFilter(Reasons: 0x300), true, false, null, 200, CancellationToken.None);
                    await Until(() => grid.Items.OfType<GlobalLogRow>().Select(row => row.Position).SequenceEqual(either.Items.Select(row => row.Position)));
                    Require(Find<TextBlock>("KindLabel").Text.Contains('2'), "multiple selected reasons were not reflected in the closed filter");
                    var reasonSearch = Find<TextBox>("ReasonSearch");
                    reasonSearch.Text = "USN_REASON_FILE_DELETE";
                    Require(remove.Visibility == Visibility.Visible && create.Visibility == Visibility.Collapsed && create.IsChecked == true, "search removed a hidden selected reason");
                    reasonSearch.Text = "not-a-usn-reason";
                    Require(Find<TextBlock>("NoReasons").Visibility == Visibility.Visible, "missing empty reason search state");
                    Find<Button>("ClearReasons").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Require(reasons.All(box => box.IsChecked != true), "a reason hidden by search survived clear");
                    await Until(() => grid.Items.Count == 200 && ((GlobalLogRow)grid.Items[0]).Position == first);
                    Find<ToggleButton>("More").IsChecked = true;
                    var exact = Find<TextBox>("ExactUsn");
                    exact.Text = $"0x{target.Usn:X}";
                    await Until(() => view.SelectedPosition == target.Position);
                    var detail = Find<RowDefinition>("DetailHeight");
                    var evidenceTabs = Find<TabControl>("Evidence");
                    host.UpdateLayout();
                    double expectedHeight = detail.ActualHeight;
                    int[] inspectorTabs = [1, 2, 0];
                    foreach (int index in inspectorTabs)
                    {
                        evidenceTabs.SelectedIndex = index;
                        host.UpdateLayout();
                        Require(Math.Abs(detail.ActualHeight - expectedHeight) < 0.1, "inspector boundary moved on tab change");
                    }
                    exact.Text = target.Usn.ToString(CultureInfo.InvariantCulture);
                    Require(Find<Button>("ClearFilters").Visibility == Visibility.Visible, "active USN filter lacks reset");
                    await Until(() => view.SelectedPosition == target.Position);
                    Find<ToggleButton>("More").IsChecked = false;
                    Require(Find<TextBlock>("PeriodLabel").Text.Contains(target.Usn.ToString(CultureInfo.InvariantCulture)), "hidden USN filter has no visible value");
                    Find<ToggleButton>("More").IsChecked = true;
                    var search = Find<TextBox>("QueryText");
                    var clearQuery = Find<Button>("ClearQuery");
                    Require(clearQuery.Visibility == Visibility.Collapsed, "clear command exposed for empty search");
                    search.Text = "journaldesk-test-no-match";
                    Require(clearQuery.IsVisible && new ButtonAutomationPeer(clearQuery).GetName() == "파일 이름 검색 지우기",
                        "search clear command missing or unlabeled");
                    ((IInvokeProvider)new ButtonAutomationPeer(clearQuery).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Until(() => view.SelectedPosition == target.Position);
                    Require(search.Text.Length == 0 && exact.Text == target.Usn.ToString(CultureInfo.InvariantCulture) && clearQuery.Visibility == Visibility.Collapsed,
                        "clearing search also changed an independent filter");
                    Require(new MenuItemAutomationPeer(Find<MenuItem>("SaveCsv")).GetName() == "전체 검색 결과 내보내기" &&
                        Find<MenuItem>("ExportResultsCsv").Header.ToString() == "레코드 CSV…" &&
                        Find<MenuItem>("ExportPathsCsv").Header.ToString() == "경로와 근거를 포함한 CSV…",
                        "export menu lacks a scope or a choice between record and path-evidence CSV");
                    var start = Find<TextBox>("StartTime");
                    var end = Find<TextBox>("EndTime");
                    var startDate = Find<DatePicker>("StartDate");
                    var endDate = Find<DatePicker>("EndDate");
                    Require(view.FindName("Zone") == null &&
                        ((HintTextBox)start).Hint == "00:00" && ((HintTextBox)end).Hint == "23:59",
                        "the filter should show numeric time defaults and use Settings for the zone");
                    var localTarget = DateTime.FromFileTimeUtc(target.Timestamp!.Value).AddHours(9);
                    startDate.SelectedDate = localTarget.Date;
                    endDate.IsDropDownOpen = true;
                    await Until(() => endDate.IsDropDownOpen && endDate.DisplayDate.Date == localTarget.Date);
                    endDate.IsDropDownOpen = false;
                    endDate.SelectedDate = localTarget.Date;
                    await Until(() => view.SelectedPosition == target.Position);
                    var periodLabel = Find<TextBlock>("PeriodLabel");
                    var periodButton = Find<ToggleButton>("More");
                    periodButton.IsChecked = false;
                    Require(periodLabel.Text.Contains(startDate.Text) && !periodLabel.Text.Contains('~') &&
                        periodButton.ToolTip.ToString()!.Contains(target.Usn.ToString(CultureInfo.InvariantCulture)), "collapsed single-day and USN filters do not identify their values");
                    periodButton.IsChecked = true;
                    var clearUsn = Find<Button>("ClearUsn");
                    Require(clearUsn.IsVisible && new ButtonAutomationPeer(clearUsn).GetName() == "USN 필터 지우기",
                        "USN clear command missing or unlabeled");
                    ((IInvokeProvider)new ButtonAutomationPeer(clearUsn).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Until(() => exact.Text.Length == 0 && clearUsn.Visibility == Visibility.Collapsed);
                    Require(startDate.SelectedDate == localTarget.Date && endDate.SelectedDate == localTarget.Date,
                        "clearing USN changed the date range");
                    exact.Text = target.Usn.ToString(CultureInfo.InvariantCulture);
                    await Until(() => view.SelectedPosition == target.Position);
                    DisplayTimeZone.Current.Code = "UTC";
                    Require(DisplayTimeZone.Current.Utc && DeskSettings.Read("time-zone.txt") == "UTC",
                        "the date filter and saved display time zone diverged");
                    Require(startDate.SelectedDate == localTarget.Date.AddDays(-1) && start.Text == "15:00:00" &&
                        endDate.SelectedDate == localTarget.Date && end.Text == "14:59:59.9999999", "calendar day range changed on UTC conversion");
                    DisplayTimeZone.Current.Code = "KST";
                    Require(startDate.SelectedDate == localTarget.Date && endDate.SelectedDate == localTarget.Date &&
                        start.Text == "" && end.Text == "", "calendar display changed after timezone round-trip");
                    end.Text = localTarget.AddTicks(-1).ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                    await Until(() => Find<TextBlock>("Empty").Text == "검색 조건에 맞는 기록이 없습니다.");
                    end.Text = localTarget.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                    await Until(() => view.SelectedPosition == target.Position);
                    Find<Button>("ClearEnd").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Require(endDate.SelectedDate == null && end.Text == "" && startDate.SelectedDate == localTarget.Date, "individual date clear");
                    start.Text = GlobalLogFilter.TimeText(target.Timestamp.Value, false, "yyyy-MM-dd HH:mm:ss.fffffff");
                    await Until(() => view.SelectedPosition == target.Position);
                    DisplayTimeZone.Current.Code = "UTC";
                    string utcBoundary = startDate.SelectedDate!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " " + start.Text;
                    Require(view.SelectedPosition == target.Position && GlobalLogFilter.TryTime(utcBoundary, true, out long t) && t == target.Timestamp &&
                        grid.SelectedItem is GlobalLogRow { TimeZone.OffsetMinutes: 0 }, "timezone changes selected record or selected time interval");
                    Find<TabItem>("OriginalBytesTab").IsSelected = true;
                    var raw = Find<HexEvidenceView>("Raw");
                    await Until(() => raw.ByteCount == target.Record.Size && raw.BaseOffset == target.Position);
                    using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        file.Position = target.Position;
                        var bytes = new byte[16];
                        file.ReadExactly(bytes);
                        Require(raw.Text.Split('\n')[0].Contains(BitConverter.ToString(bytes).Replace('-', ' ')), "raw inspector disagrees with original bytes");
                    }
                    raw.SelectField(UsnHexFields.ReasonLabel);
                    Require(raw.SelectedField?.Label == UsnHexFields.ReasonLabel &&
                        raw.SelectedField.Value.Contains($"0x{target.Reason:X8}"), "reason field does not match raw view selection");
                    Find<Button>("CloseInspector").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Require(view.SelectedPosition == target.Position && !Find<Border>("Inspector").IsVisible,
                        "closing the inspector lost the selected record");
                    exact.Text = "0xBADINPUT";
                    await Until(() => Find<TextBlock>("Empty").Text.StartsWith("USN은", StringComparison.Ordinal));
                    Require(grid.Items.Count == 0 && !Find<MenuItem>("SaveCsv").IsEnabled, "invalid filter left stale results exportable");
                    var recover = Find<Button>("RecoverFilter");
                    var more = Find<ToggleButton>("More");
                    more.IsChecked = false;
                    Require(recover.IsVisible && Equals(recover.Content, "USN 수정") &&
                        System.Windows.Automation.AutomationProperties.GetHelpText(exact).Contains("16진수"),
                        "invalid USN was hidden without an accessible correction");
                    recover.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Require(more.IsChecked == true && exact.SelectionLength == exact.Text.Length, "repair did not reveal and select the invalid USN");
                    exact.Text = target.Usn.ToString(CultureInfo.InvariantCulture);
                    await Until(() => view.SelectedPosition == target.Position);
                    Require(!recover.IsVisible && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(exact)),
                        "validation state survived a corrected value");
                    start.Text = "25:99";
                    await Until(() => Equals(recover.Content, "시작 시각 수정") && recover.IsVisible);
                    Require(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(startDate)) &&
                        System.Windows.Automation.AutomationProperties.GetHelpText(start).Contains("시각"), "invalid clock marked the valid date");
                    Find<Button>("ClearStart").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => view.SelectedPosition == target.Position);
                    endDate.Text = "not-a-date";
                    Find<Button>("ClearEnd").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var endEditor = (TextBox)endDate.Template.FindName("PART_TextBox", endDate);
                    Require(endEditor.Text.Length == 0, "queued validation restored a date after the investigator cleared it");
                    search.Text = "journaldesk-test-no-match";
                    await Until(() => recover.IsVisible && Equals(recover.Content, "파일 이름 검색 지우기"));
                    recover.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => view.SelectedPosition == target.Position);
                    Require(search.Text.Length == 0 && exact.Text == target.Usn.ToString(CultureInfo.InvariantCulture), "empty result recovery discarded an independent USN filter");

                    var fields = Find<RecordSearchFields>("PathFilters");
                    ((Expander)fields.FindName("Section")).IsExpanded = true;
                    var pathInput = (TextBox)fields.FindName("PathInput");
                    var fileInput = (TextBox)fields.FindName("FileInput");
                    var parentInput = (TextBox)fields.FindName("ParentInput");
                    var expectedPath = new Journal(database).ResolveRecordPath(target.Record, target.FileId, null, null);
                    Require(expectedPath.HasKnownPath, "real fixture has a searchable path");
                    JournalPathPreference.Current.Enabled = false;
                    pathInput.Text = expectedPath.Display;
                    fileInput.Text = $"0x{BitConverter.ToUInt64(target.FileId):X16}";
                    parentInput.Text = $"0x{BitConverter.ToUInt64(target.Record.Parent):X16}";
                    await Until(() => view.SelectedPosition == target.Position && Find<MenuItem>("SaveCsv").IsEnabled);
                    parentInput.Text = "invalid reference";
                    await Until(() => grid.Items.Count == 0 && !Find<MenuItem>("SaveCsv").IsEnabled && ((TextBlock)fields.FindName("Error")).IsVisible);
                    fields.Reset();
                    await Until(() => view.SelectedPosition == target.Position && Find<MenuItem>("SaveCsv").IsEnabled);
                    Require(exact.Text == target.Usn.ToString(CultureInfo.InvariantCulture), "record filter reset cleared an independent USN filter");
                    JournalPathPreference.Current.Enabled = true;
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { host.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            };
            host.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
