using System.Globalization;
using System.Diagnostics;
using Activity = JournalDesk.Activity;
using System.IO;
using JournalDesk;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using Microsoft.Data.Sqlite;

static void Require(bool value, string message)
{
    if (!value)
        throw new InvalidOperationException(message);
}

static async Task<List<long>> Traverse(GlobalLogQuery query, string search, bool newest, int pageSize)
{
    var result = new List<long>();
    GlobalLogCursor? after = null;
    do
    {
        var page = await query.ReadAsync(search, newest, after, pageSize, CancellationToken.None);
        result.AddRange(page.Items.Select(item => item.Position));
        after = page.Next;
    } while (after is not null);
    return result;
}

static async Task Synthetic()
{
    string path = Path.Combine(Path.GetTempPath(), "journaldesk-log-" + Guid.NewGuid().ToString("N") + ".sqlite");
    try
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var c = db.CreateCommand();
            c.CommandText = """
                CREATE TABLE summary(hash); INSERT INTO summary VALUES('test-hash');
                CREATE TABLE records(pos INTEGER PRIMARY KEY, size,version,file,parent,usn,stamp,reason,source,security,attr,name,minor,resynced,escaped,segment);
                CREATE TABLE aliases(file,name);
                CREATE TABLE range_records(pos INTEGER PRIMARY KEY,remaining);
                CREATE TABLE extents(pos,ordinal,start,length);
                INSERT INTO records VALUES
                  (1,80,2,x'01',x'05',20,200,2147483904,0,0,32,'Älter%_''',0,0,0,0),
                  (2,80,2,x'01',x'05',20,200,8192,0,0,32,'새이름',0,0,0,0),
                  (3,80,2,x'02',x'05',30,100,256,0,0,32,'파일',0,0,0,0),
                  (4,80,4,x'01',x'05',40,NULL,1,0,NULL,NULL,NULL,0,0,0,0),
                  (5,80,4,x'03',x'05',50,NULL,1,0,NULL,NULL,NULL,0,0,0,0);
                INSERT INTO aliases SELECT DISTINCT file,name FROM records WHERE name IS NOT NULL;
                INSERT INTO range_records VALUES(4,4294967295),(5,0);
                INSERT INTO extents VALUES(4,1,300,10),(4,0,40,8);
                """;
            c.ExecuteNonQuery();
        }
        var q = new GlobalLogQuery(path);
        // Ties straddle page boundaries. Derived sorts use source offsets, USN uses its native index.
        foreach (bool reverse in new[] { false, true })
        {
            foreach (var order in new IReadOnlyList<long>?[] { null, new long[] { 3, 2, 5, 4, 1 } })
            {
                var expected = order ?? new long[] { 1, 2, 3, 4, 5 };
                var actual = new List<long>();
                GlobalLogCursor? cursor = null;
                do
                {
                    var slice = await q.ReadAsync(new(), reverse, false, cursor, 2, CancellationToken.None, order == null ? JournalSort.Usn : JournalSort.Path, order);
                    actual.AddRange(slice.Items.Select(r => r.Position));
                    cursor = slice.Next;
                } while (cursor != null);
                Require(actual.SequenceEqual(reverse ? expected.Reverse() : expected), "full rank traversal and duplicate USN tie order");
            }
        }
        Require((await Traverse(q, "", false, 1)).SequenceEqual(new long[] { 3, 1, 2, 4, 5 }), "ascending order / null tail");
        Require((await Traverse(q, "", true, 2)).SequenceEqual(new long[] { 2, 1, 3, 5, 4 }), "descending order / tie / null tail");
        Require((await Traverse(q, "älter%_'", false, 2)).SequenceEqual(new long[] { 1, 2, 4 }), "Unicode old-name substring and literal wildcards");
        Require((await Traverse(q, "irrelevant", false, 2)).Count == 0, "empty result");
        var page = await q.ReadAsync("새이름", null, 3, CancellationToken.None);
        Require(page.Next is null && page.Items[^1].Record.Remaining == uint.MaxValue && page.Items[^1].Record.Extents == "40 + 8; 300 + 10" &&
            page.Items[^1].Timestamp is null && page.Items[^1].Record.Name == "" && page.Items[^1].Record.ExtentStride == null, "complete legacy V4 evidence");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var c = db.CreateCommand();
            c.CommandText = "ALTER TABLE range_records ADD COLUMN stride INTEGER; UPDATE range_records SET stride=24;";
            c.ExecuteNonQuery();
        }
        var withStride = await q.ReadAsync("새이름", null, 3, CancellationToken.None);
        Require(withStride.Items[^1].Record.ExtentStride == 24 && new Journal(path).History([1])[^1].ExtentStride == 24,
            "stored stride preserved in global records and file history");
        var direct = await q.ReadAsync(new GlobalLogFilter("ÄLTER", AllNames: false), false, false, null, 10, CancellationToken.None);
        Require(direct.Items.Select(x => x.Position).SequenceEqual([1L]), "record-name search excludes aliases and unnamed V4 rows");
        var compound = await q.ReadAsync(new GlobalLogFilter(Reasons: 0x100, From: 200, Until: 201), false, true, null, 10, CancellationToken.None);
        Require(compound.Items.Count == 1 && compound.Items[0].Title.Contains("생성 기록") && compound.Items[0].Title.Contains("파일 닫기"), "all composite flags visible, event mask and time bounds");
        var boundary = await q.ReadAsync(new GlobalLogFilter(Usn: 20, Until: 200), false, false, null, 10, CancellationToken.None);
        var close = compound.Items[0].Record with
        {
            Reason = 0x80000108
        };
        Require(close.RecordLabel.Contains("생성 기록") && close.RecordLabel.Contains("파일 닫기") &&
            close.RecordLabel.Contains("0x00000008") && close.ProofReason == close.RecordLabel &&
            new GlobalLogRow([1], close).ReasonsText == close.FlagsText, "composite and unknown bits survive each source label");
        Require((close with
        {
            Reason = 0
        }).FlagsText == "사유 비트 없음", "zero reason is distinct from an unknown bit");
        var earlier = close with
        {
            Reason = 0x108,
            Usn = 19
        };
        Require(Activity.From([earlier, close]).Count == 1 &&
            Activity.From([earlier with { Timestamp = null }, close with { Timestamp = null }]).Count == 2 &&
            Activity.From([earlier with { Usn = close.Usn }, close]).Count == 2, "closing summary needs matching time and a later USN");
        Require(boundary.Items.Count == 0, "exclusive upper bound and exact USN");
        Require(GlobalLogFilter.TryUsn("20", out long dec) && GlobalLogFilter.TryUsn("0x14", out long hex) && dec == 20 && hex == 20 &&
            !GlobalLogFilter.TryUsn("2x14", out _) && !GlobalLogFilter.TryUsn("0xFFFFFFFFFFFFFFFF", out _), "exact decimal and hexadecimal USN parsing");
        Require(GlobalLogFilter.TryTime("2026-11-21 09:00:00.0000001", false, out long korea) &&
            GlobalLogFilter.TryTime("2026-11-21 00:00:00.0000001", true, out long utc) && korea == utc &&
            GlobalLogFilter.TimeText(korea, true, "yyyy-MM-dd HH:mm:ss.fffffff") == "2026-11-21 00:00:00.0000001", "KST / UTC conversion preserves FILETIME precision");
        string csv = path + ".csv";
        try
        {
            long count = await q.ExportAsync(csv, "source,\"quoted\".bin", new GlobalLogFilter("älter"), false, true, CancellationToken.None, carvedSource: true);
            Require(count == 3, "CSV includes alias matches and unnamed V4");
            using (var parser = Csv(csv))
            {
                var header = parser.ReadFields()!;
                var row = parser.ReadFields()!;
                Require(header[1].Contains("UTC") && row.Length == header.Length && row[3] == "0x80000100" && row[5] == "01" && row[6] == "05" && row[7] == "1" &&
                    row[10] == "source,\"quoted\".bin" && row[11] == "test-hash" && row[^1] == "카빙 후보", "CSV quotes, source bytes, source hash and carving provenance");
            }
            try
            {
                await q.ExportAsync(csv, "", new(), false, false, CancellationToken.None);
                throw new InvalidOperationException("existing file overwritten");
            }
            catch (IOException) { }
            using (var existing = Csv(csv))
            {
                existing.ReadFields();
                Require(existing.ReadFields()![10] == "source,\"quoted\".bin", "existing CSV changed");
            }
            string annotated = path + ".paths.csv";
            try
            {
                var allAliases = new GlobalLogFilter("älter");
                await q.ExportAsync(annotated, "source", allAliases, false, true, CancellationToken.None,
                    resolvePath: r => r.Record.Version == 4 ? new("경로 미상", "이름 기록 없음", "파일 이름 없음") :
                        new(@"\자료,1\" + r.Name, "저널로 재구성", "근거 \"A\""), mftHash: "paired-hash", mftSource: "volume.mft");
                using var parser = Csv(annotated);
                var header = parser.ReadFields()!;
                var first = parser.ReadFields()!;
                parser.ReadFields();
                var unnamed = parser.ReadFields()!;
                Require(header.Length == 28 && first.Length == 28 && first[22] == @"\자료,1\" + first[0] && first[23] == "USN" &&
                    first[25] == "근거 \"A\"" && unnamed[22] == "" && unnamed[23] == "" && unnamed[24] == "이름 기록 없음" &&
                    unnamed[26] == "volume.mft" && unnamed[27] == "paired-hash" && parser.EndOfData,
                    "optional CSV paths, source and missing-name evidence");
            }
            finally { File.Delete(annotated); }
        }
        finally { File.Delete(csv); }
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await q.ReadAsync("", null, 3, stop.Token);
            throw new InvalidOperationException("cancellation was ignored");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE records SET file=x'2A00000000000700', parent=x'0500000000000100' WHERE pos IN (1,2);
                UPDATE records SET file=x'2A00000000000800', parent=x'0500000000000200' WHERE pos=3;
                UPDATE records SET file=x'2A00000000000700090A0B0C0D0E0F10', parent=x'0500000000000100' WHERE pos=4;
                """;
            cmd.ExecuteNonQuery();
        }
        Require(NtfsReference.TryParse("42", out var slot), "slot-only NTFS reference");
        Require(NtfsReference.TryParse("5:1", out var parentReference), "NTFS slot and sequence");
        Require(NtfsReference.TryParse("0x0001000000000005", out var hexReference) && hexReference == parentReference,
            "numeric hexadecimal reference matches decimal slot and sequence");
        Require(NtfsReference.TryParse("42:8", out var reusedReference), "reused slot reference");
        Require(NtfsReference.TryParse("2A00000000000700090A0B0C0D0E0F10", out var wideReference), "128-bit source bytes");
        var referenceFilter = new GlobalLogFilter() { RecordFields = new(File: slot, Parent: parentReference) };
        var firstMatch = await q.ReadAsync(referenceFilter, false, false, null, 1, CancellationToken.None);
        var secondMatch = await q.ReadAsync(referenceFilter, false, false, firstMatch.Next, 1, CancellationToken.None);
        Require(firstMatch.Items.Single().Position == 1 && secondMatch.Items.Single().Position == 2 && secondMatch.Next == null,
            "both reference filters remain active across a page boundary");
        var reusedMatch = await q.ReadAsync(referenceFilter with
        {
            RecordFields = new(File: reusedReference)
        },
            false, false, null, 10, CancellationToken.None);
        Require(reusedMatch.Items.Single().Position == 3, "full reference includes the record sequence");
        var wideMatch = await q.ReadAsync(referenceFilter with
        {
            RecordFields = new(File: wideReference)
        },
            false, false, null, 10, CancellationToken.None);
        Require(wideMatch.Items.Single().Position == 4, "wide references match the exact source bytes");
        var pathFilter = referenceFilter with
        {
            RecordFields = new("/älter%_'", slot, parentReference)
        };
        var pathMatch = await q.ReadAsync(pathFilter, false, false, null, 10, CancellationToken.None);
        Require(pathMatch.Items.Single().Position == 1, "path, file and parent match the same record and preserve literal wildcards");
        var filteredRanks = await q.FilterPathOrdersAsync(pathFilter, (new long[] { 4, 1, 3, 2, 5 }, new long[] { 2, 3, 1, 5, 4 }), CancellationToken.None);
        Require(filteredRanks.Paths.SequenceEqual([1L]) && filteredRanks.Sources.SequenceEqual([1L]), "path sort applies the same field filters");
        try
        {
            Require(await q.ExportAsync(csv, "source", pathFilter, false, false, CancellationToken.None) == 1,
                "CSV applies path and reference filters across the full result set");
            using var records = Csv(csv);
            records.ReadFields();
            Require(records.ReadFields()![7] == "1" && records.EndOfData, "CSV retained the exact matching record");
        }
        finally { File.Delete(csv); }
    }
    finally { File.Delete(path); }
}

static TextFieldParser Csv(string path)
{
    var reader = new TextFieldParser(path, System.Text.Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
    reader.SetDelimiters(",");
    return reader;
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

var nepal = new JournalTimeZone(345);
var newfoundland = new JournalTimeZone(-210);
Require(GlobalLogFilter.TryTime("2026-11-22 00:00", true, out var midnight) &&
    GlobalLogFilter.TryTime("2026-11-21 23:59:59.9999999", true, out var lastTick) && lastTick + 1 == midnight &&
    GlobalLogFilter.TryUpperTime("2026-11-21", true, out var endOfDay) && endOfDay == midnight &&
    GlobalLogFilter.TryUpperTime("2026-11-21 23:59", true, out var endOfMinute) && endOfMinute == midnight &&
    GlobalLogFilter.TryUpperTime("2026-11-21 23:59:59", true, out var endOfSecond) && endOfSecond == midnight &&
    GlobalLogFilter.TryUpperTime("2026-11-21 23:59:59.999", true, out var endOfMillisecond) && endOfMillisecond == midnight &&
    GlobalLogFilter.TryUpperTime("2026-11-21 23:59:59.9999999", true, out var endOfTick) && endOfTick == midnight &&
    !GlobalLogFilter.TryUpperTime("2026-11-21 23:59:99", true, out _),
    "inclusive end boundaries honor the selected precision");
Require(GlobalLogFilter.TryTime("2026-11-22 00:15:00", nepal, out long offsetStamp)
    && GlobalLogFilter.TryTime("2026-11-21 18:30:00", true, out long utcStamp)
    && offsetStamp == utcStamp
    && GlobalLogFilter.TimeText(offsetStamp, newfoundland, "yyyy-MM-dd HH:mm:ss") == "2026-11-21 15:00:00"
    && nepal.Code == "UTC+05:45" && newfoundland.Code == "UTC-03:30",
    "fractional or negative offset changed the evidence timestamp");

var pacific = JournalTimeZone.FromWindows(TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
long winterUtc = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
long summerUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
Require(GlobalLogFilter.PreciseTime(winterUtc, pacific) == "2026-01-15 04:00:00.0000000 -08:00" &&
    GlobalLogFilter.PreciseTime(summerUtc, pacific) == "2026-07-15 05:00:00.0000000 -07:00",
    "Windows zone must use the offset valid at each record timestamp");
Require(GlobalLogFilter.DisplayTime(winterUtc, pacific) == "2026-01-15 04:00:00 -08:00" &&
    GlobalLogFilter.DisplayTime(winterUtc + 1_200_000, pacific) == "2026-01-15 04:00:00.12 -08:00" &&
    GlobalLogFilter.DisplayTime(winterUtc + 1, pacific) == "2026-01-15 04:00:00.0000001 -08:00",
    "Displayed timestamps omit trailing zeroes and preserve significant fractional digits");
Require(GlobalLogFilter.TryTime("2026-03-08", pacific, out long spring) &&
    GlobalLogFilter.TryUpperTime("2026-03-08", pacific, out long afterSpring) &&
    afterSpring - spring == TimeSpan.FromHours(23).Ticks &&
    !GlobalLogFilter.TryTime("2026-03-08 02:30", pacific, out _) &&
    GlobalLogFilter.TryTime("2026-11-01", pacific, out long autumn) &&
    GlobalLogFilter.TryUpperTime("2026-11-01", pacific, out long afterAutumn) &&
    afterAutumn - autumn == TimeSpan.FromHours(25).Ticks &&
    GlobalLogFilter.TryTime("2026-11-01 01:30", pacific, out long firstOccurrence) &&
    GlobalLogFilter.TryUpperTime("2026-11-01 01:30", pacific, out long afterSecondOccurrence) &&
    afterSecondOccurrence - firstOccurrence == TimeSpan.FromMinutes(61).Ticks,
    "Windows zone filters must preserve whole civil days and repeated times across daylight-saving transitions");
Console.WriteLine("Windows time zone and daylight-saving boundaries verified.");

await Synthetic();
Console.WriteLine("Synthetic ordering, null timestamps, V4 ranges, old-name search, cursor boundaries and cancellation passed.");

if (args.Length > 0)
{
    string path = args[0];
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

if (args.Length > 1)
{
    string path = args[0], source = args[1];
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

sealed class InlineProgress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}
