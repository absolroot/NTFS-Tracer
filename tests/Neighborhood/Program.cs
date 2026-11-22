using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JournalDesk;
using Microsoft.Data.Sqlite;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string temp = Path.Combine(Path.GetTempPath(), "JournalDesk-neighborhood-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/NtfsTracer;component/DeskResources.xaml", UriKind.Relative) });
        int exitCode = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                string database = Path.Combine(temp, "index.sqlite"), source = Path.Combine(temp, "source.bin");
                CreateFixture(database);
                File.WriteAllBytes(source, new byte[1024]);
                await Check(new Journal(database), source, TimedAnchor, false, async window =>
                {
                    Require(Row(window).Position == TimedAnchor.Position, "initial anchor with duplicate USN");
                    Require(Count(window).StartsWith("기록 212건", StringComparison.Ordinal), "included lower/excluded upper timestamp bound");
                    Click(window, "Prev");
                    await Until(() => !Button(window, "Prev").IsEnabled && Button(window, "Next").IsEnabled);
                    Require(Row(window).Position == 4, "lower bound missing");
                    Click(window, "ToAnchor");
                    await Until(() => RowOrNull(window)?.Position == TimedAnchor.Position);
                    Radio(window, "Range1").IsChecked = true;
                    await Until(() => Count(window).StartsWith("기록 211건", StringComparison.Ordinal));
                    Require(Row(window).Position == TimedAnchor.Position, "anchor lost on resize");
                });
                await Check(new Journal(database), source, UntimedAnchor, true, async window =>
                {
                    Require(Radio(window, "Range5").Content?.ToString() == "각 25건", "invented time range");
                    Require(Rows(window).Items.Count == 51, "V4 initial record radius");
                    Radio(window, "Range1").IsChecked = true;
                    await Until(() => Rows(window).Items.Count == 11);
                    var items = Rows(window).Items.Cast<object>().Select(UnderlyingRow).ToList();
                    Require(items.All(r => r.Record.Segment == 2) && items[5].Position == UntimedAnchor.Position,
                        "timestamp-less browsing crossed a gap or misplaced an equal USN");
                });
                if (args.Length == 2)
                {
                    var query = new GlobalLogQuery(args[0]);
                    var result = await query.ReadAsync(new GlobalLogFilter(Reasons: 0x2000), false, false, null, 100, default);
                    var anchor = result.Items.Select(row => row.Record).First(record =>
                        record.Timestamp.HasValue && record.Version is 2 or 3 && record.Name.Length != 0);
                    using var reference = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = args[0],
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false
                    }.ToString());
                    reference.Open();
                    (long Records, long Files) CountAt(int seconds)
                    {
                        using var sql = reference.CreateCommand();
                        sql.CommandText = "SELECT count(*), count(DISTINCT file) FROM records WHERE stamp >= $from AND stamp < $until";
                        long stamp = anchor.Timestamp!.Value;
                        long span = seconds * TimeSpan.TicksPerSecond;
                        sql.Parameters.AddWithValue("$from", stamp - span);
                        sql.Parameters.AddWithValue("$until", stamp + span);
                        using var counts = sql.ExecuteReader();
                        Require(counts.Read(), "reference counts unavailable");
                        return (counts.GetInt64(0), counts.GetInt64(1));
                    }
                    var five = CountAt(5);
                    var one = CountAt(1);
                    var thirty = CountAt(30);
                    bool MatchesCounts(NeighborhoodWindow window, (long Records, long Files) counts) =>
                        Count(window).Contains($"기록 {counts.Records:N0}건", StringComparison.Ordinal) &&
                        Count(window).Contains($"파일·폴더 {counts.Files:N0}개", StringComparison.Ordinal);
                    await Check(new Journal(args[0]), args[1], anchor, false, async window =>
                    {
                        Require(MatchesCounts(window, five), "real 5 seconds");
                        Radio(window, "Range1").IsChecked = true;
                        await Until(() => MatchesCounts(window, one));
                        Radio(window, "Range30").IsChecked = true;
                        await Until(() => MatchesCounts(window, thirty));
                        Require(Row(window).Position == anchor.Position, "real anchor not visible on its page");
                        Click(window, "OpenOriginal");
                        var child = window.OwnedWindows.OfType<EvidenceWindow>().Single();
                        var hex = (HexEvidenceView)child.FindName("Viewer");
                        Require(hex.BaseOffset == anchor.Position && hex.ByteCount == anchor.Size &&
                                hex.SelectedField?.Value == anchor.RecordedName, "incorrect child evidence");
                        child.Close();
                    });
                    Console.WriteLine("Real rename counts, anchor page and original decoded filename verified.");
                }
                Console.WriteLine("Half-open time range, exact anchor, paging and timestamp-less same-segment ordering passed.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { app.Shutdown(); }
        };
        try
        {
            app.Run();
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(temp, true); }
        return exitCode;
    }

    const long BaseTime = 135000000000000000;
    static readonly Entry TimedAnchor = new(310, 8, 2, new byte[8], 17, BaseTime, 2, 0, 0, 0, "target", 0, false, false, null, "", 1);
    static readonly Entry UntimedAnchor = new(740, 8, 4, new byte[16], 19, null, 2, 0, null, null, "", 0, false, false, 0, "", 2);
    static void CreateFixture(string path)
    {
        using var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE records(pos INTEGER PRIMARY KEY,size,version,file,parent,usn,stamp,reason,source,security,attr,name,minor,resynced,escaped,segment);
            CREATE TABLE range_records(pos INTEGER PRIMARY KEY,remaining,stride);
            CREATE TABLE extents(pos,ordinal,start,length);
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<210)
            INSERT INTO records SELECT 100+i,8,2,x'01',x'02',17,$stamp,2,0,0,0,'test',0,0,0,1 FROM n;
            INSERT INTO records VALUES (4,8,2,x'03',x'02',7,$stamp-50000000,2,0,0,0,'lower',0,0,0,1),
                 (5,8,2,x'04',x'02',8,$stamp+50000000,2,0,0,0,'upper',0,0,0,1);
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<80)
            INSERT INTO records SELECT 700+i,8,4,x'01',x'02',19,NULL,2,0,NULL,NULL,NULL,0,0,0,2 FROM n;
            INSERT INTO records VALUES (699,8,4,x'01',x'02',19,NULL,2,0,NULL,NULL,NULL,0,0,0,8);
            """;
        cmd.Parameters.AddWithValue("$stamp", BaseTime);
        cmd.ExecuteNonQuery();
    }

    static async Task Check(Journal j, string source, Entry anchor, bool utc, Func<NeighborhoodWindow, Task> work)
    {
        var window = new NeighborhoodWindow(j, source, anchor, utc) { Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            await Until(() => Rows(window).SelectedItem != null);
            await work(window);
        }
        finally { window.Close(); }
    }
    static DataGrid Rows(Window w) => (DataGrid)w.FindName("Rows");
    static Button Button(Window w, string name) => (Button)w.FindName(name);
    static RadioButton Radio(Window w, string name) => (RadioButton)w.FindName(name);
    static string Count(Window w) => ((TextBlock)w.FindName("Counts")).Text;
    static GlobalLogRow UnderlyingRow(object view) => (GlobalLogRow)view.GetType().GetProperty("Item")!.GetValue(view)!;
    static GlobalLogRow Row(Window w) => UnderlyingRow(Rows(w).SelectedItem);
    static GlobalLogRow? RowOrNull(Window w) => Rows(w).SelectedItem is { } value ? UnderlyingRow(value) : null;
    static void Click(Window w, string name) => Button(w, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    static async Task Until(Func<bool> check)
    {
        var clock = Stopwatch.StartNew();
        while (!check())
        {
            if (clock.ElapsedMilliseconds > 10000)
                throw new TimeoutException();
            await Task.Delay(15);
        }
    }
    static void Require(bool yes, string message)
    {
        if (!yes)
            throw new InvalidOperationException(message);
    }
}
