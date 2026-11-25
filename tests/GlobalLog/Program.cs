using System.Globalization;
using System.IO;
using JournalDesk;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;
using Activity = JournalDesk.Activity;

static partial class Program
{
    static async Task Main(string[] args)
    {
        CheckTimeBoundaries();
        await Synthetic();
        Console.WriteLine("Synthetic ordering, null timestamps, V4 ranges, old-name search, cursor boundaries and cancellation passed.");

        if (args.Length > 0)
            await CheckEvidenceOrderAsync(args[0]);
        if (args.Length > 1)
            await CheckEvidenceAndViewAsync(args[0], args[1]);
    }

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

    static void CheckTimeBoundaries()
    {
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
    }
}
