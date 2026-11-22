using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace JournalDesk;

public enum JournalSort
{
    Time, Usn, Name, Reason, Path, PathSource
}
public sealed record GlobalLogCursor(long? Timestamp, long Usn, long Position)
{
    internal int Rank
    {
        get; init;
    }
}
public sealed record GlobalLogPage(IReadOnlyList<GlobalLogRow> Items, GlobalLogCursor? Next);
public sealed record GlobalLogFilter(string Name = "", bool AllNames = true, long Reasons = 0,
    long? Usn = null, long? From = null, long? Until = null)
{
    internal RecordSearch RecordFields { get; init; } = new();
    internal bool IsAllRecords => string.IsNullOrWhiteSpace(Name) && Reasons == 0 && Usn == null && From == null && Until == null && RecordFields.IsEmpty;

    public static bool TryUsn(string text, out long value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && value >= 0;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static readonly string[] Formats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"];
    public static bool TryTime(string text, JournalTimeZone utc, out long value)
    {
        text = text.Trim();
        value = 0;
        return DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
            utc.TryFileTime(date, end: false, dayBoundary: text.Length == 10, out value);
    }

    internal static bool TryUpperTime(string text, JournalTimeZone zone, out long exclusive)
    {
        text = text.Trim();
        exclusive = 0;
        if (!DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return false;
        bool dayOnly = text.Length == 10;
        if (dayOnly && zone.WindowsZone != null && date.Date < DateTime.MaxValue.Date)
            return zone.TryFileTime(date.AddDays(1), end: false, dayBoundary: true, out exclusive);
        if (!zone.TryFileTime(date, end: !dayOnly, dayBoundary: dayOnly, out exclusive))
            return false;
        int fractionLength = Math.Max(0, text.Length - 20);
        if (dayOnly)
            exclusive += TimeSpan.TicksPerDay;
        else if (text.Length == 16)
            exclusive += TimeSpan.TicksPerMinute;
        else
        {
            long precision = TimeSpan.TicksPerSecond;
            for (int digit = 0; digit < fractionLength; digit++)
                precision /= 10;
            exclusive += precision;
        }
        return true;
    }
    public static string TimeText(long value, JournalTimeZone utc, string format)
    {
        try
        {
            var time = DateTime.FromFileTimeUtc(value);
            return utc.WindowsZone == null ? time.Add(utc.Offset).ToString(format, CultureInfo.InvariantCulture)
                : utc.At(new DateTimeOffset(time)).ToString(format, CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException) { return L.T("시각 확인 불가", "Invalid timestamp"); }
    }

    internal static string PreciseTime(long value, JournalTimeZone zone) =>
        TimeText(value, zone, zone.WindowsZone == null ? "yyyy-MM-dd HH:mm:ss.fffffff" : "yyyy-MM-dd HH:mm:ss.fffffff zzz");

    internal static string DisplayTime(long value, JournalTimeZone zone) =>
        TimeText(value, zone, zone.WindowsZone == null ? "yyyy-MM-dd HH:mm:ss.FFFFFFF" : "yyyy-MM-dd HH:mm:ss.FFFFFFF zzz");
}

public sealed record GlobalLogRow(byte[] FileId, Entry Record, JournalTimeZone TimeZone)
{
    public GlobalLogRow(byte[] fileId, Entry record) : this(fileId, record, false) { }
    public override string ToString() => $"{TimeText} · {Name} · {ReasonsText} · USN {Usn}";
    public string Name => Record.RecordedName;
    public System.Windows.Media.ImageSource? Icon => Record.Icon;
    public string TimeText => Record.Timestamp is long stamp ? GlobalLogFilter.TimeText(stamp, TimeZone, "yyyy-MM-dd HH:mm:ss.FFF") : L.T("시각이 기록되지 않음", "No timestamp recorded");
    public string TimeDetail => Record.DetailedTime(TimeZone);
    public long? Timestamp => Record.Timestamp;
    public long Usn => Record.Usn;
    public long Position => Record.Position;
    public long Reason => Record.Reason;
    public int Version => Record.Version;
    public string ReasonsText => Record.FlagsText;
    public string Title => ReasonTitle(Version, Reason);
    internal static string ReasonTitle(int version, long reason)
    {
        string flags = Entry.FormatFlags(reason, false);
        return version == 4 ? L.T("변경 범위 · ", "Changed range · ") + flags : flags;
    }
}

public sealed partial class GlobalLogQuery(string dbPath)
{
    private readonly JournalPathSource? pathSource;

    internal GlobalLogQuery(string dbPath, JournalPathSource? pathSource) : this(dbPath)
    {
        this.pathSource = pathSource;
    }

    public Task<GlobalLogPage> ReadAsync(string search, GlobalLogCursor? after, int pageSize,
        CancellationToken token) => ReadAsync(search, false, after, pageSize, token);

    public Task<GlobalLogPage> ReadAsync(string search, bool newest, GlobalLogCursor? after,
        int pageSize, CancellationToken token) => ReadAsync(new GlobalLogFilter(search), newest, false, after, pageSize, token);

    public Task<GlobalLogPage> ReadAsync(GlobalLogFilter filter, bool newest, JournalTimeZone utc, GlobalLogCursor? after,
        int pageSize, CancellationToken token, JournalSort sort = JournalSort.Time, IReadOnlyList<long>? order = null)
    {
        if (pageSize is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        return Task.Run(() =>
        {
            using var db = Open(token);
            int start = after?.Rank ?? 0;
            using var cmd = order == null ? Command(db, filter, newest, after, pageSize + 1, sort, token) :
                RankedCommand(db, order.Skip(newest ? Math.Max(0, order.Count - start - pageSize - 1) : start)
                    .Take(Math.Min(pageSize + 1, order.Count - start)), newest);
            var rows = new List<GlobalLogRow>(pageSize);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (rows.Count == pageSize)
                {
                    var last = rows[^1];
                    return new GlobalLogPage(rows, new(last.Timestamp, last.Usn, last.Position)
                    {
                        Rank = start + pageSize
                    });
                }
                rows.Add(Row(reader, utc));
            }
            token.ThrowIfCancellationRequested();
            return new GlobalLogPage(rows, null);
        }, token).ContinueOnInterrupt(token);
    }

    public Task<long> ExportAsync(string destination, string source, GlobalLogFilter filter, bool newest,
        JournalTimeZone utc, CancellationToken token, IProgress<long>? progress = null, bool carvedSource = false,
        Func<GlobalLogRow, PathResult>? resolvePath = null, string? mftHash = null, string? mftSource = null,
        JournalSort sort = JournalSort.Time, IReadOnlyList<long>? order = null)
    {
        return Task.Run(() =>
        {
            string target = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetExtension(target), ".csv", StringComparison.OrdinalIgnoreCase))
                throw new IOException(L.T("CSV 파일 이름의 확장자는 .csv여야 합니다.", "The CSV file name must end in .csv."));
            if (File.Exists(target))
                throw new IOException(L.T("같은 이름의 파일이 있습니다. 새로운 CSV 파일 이름을 지정해 주세요.", "A file with this name already exists. Choose a new CSV file name."));

            using var db = Open(token);
            using var meta = db.CreateCommand();
            meta.CommandText = "SELECT hash FROM summary";
            string hash = meta.ExecuteScalar() as string ?? throw new InvalidDataException(L.T("원본 SHA-256을 읽을 수 없습니다.", "Could not read the source SHA-256."));
            using var cmd = order == null ? Command(db, filter, newest, null, null, sort, token) : RankedCommand(db, order, newest);
            using var reader = cmd.ExecuteReader();
            string temporary = Path.Combine(Path.GetDirectoryName(target)!, $".ntfstracer-{Guid.NewGuid():N}.partial");
            long count = 0;
            using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true), bufferSize: 64 * 1024))
                {
                    string zone = utc.Code;
                    writer.Write(L.T("기록 당시 파일 이름,기록 시각({0}),변경 사유,사유 비트,USN,파일 식별자(원본 바이트),상위 폴더 식별자(원본 바이트),원본 오프셋,바이트 길이,원본 FILETIME,원본 파일,원본 SHA-256,기록 형식,소스 정보 비트,보안 식별 번호,파일 특성 비트,변경 범위(시작 + 길이),후속 범위 수,손상 직후 재동기화,파일 이름 이스케이프,연속 구간 번호,분석 방식", "Recorded file name,Record time ({0}),Change reason,Reason bits,USN,File ID (raw bytes),Parent ID (raw bytes),Source offset,Byte length,Raw FILETIME,Source file,Source SHA-256,Record format,Source info bits,Security ID,File attribute bits,Changed ranges (start + length),Remaining extents,Resynced after damage,Escaped file name,Continuous segment ID,Analysis method", zone));
                    if (resolvePath != null)
                        writer.Write(L.T(",기록 당시 경로,경로 자료,경로 판정,경로 근거,연결한 MFT 파일,MFT SHA-256",
                            ",Event-time path,Path source,Path assessment,Path evidence,Attached MFT file,MFT SHA-256"));
                    writer.WriteLine();
                    var progressTimer = System.Diagnostics.Stopwatch.StartNew();
                    var line = new StringBuilder(512);
                    var reasonLabels = new Dictionary<(long Reason, string Language), string>();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        var row = Row(reader, utc);
                        var e = row.Record;
                        var reasonKey = (e.Reason, L.Code);
                        if (!reasonLabels.TryGetValue(reasonKey, out var reasonLabel))
                        {
                            reasonLabel = row.ReasonsText;
                            if (reasonLabels.Count < 1024)
                                reasonLabels.Add(reasonKey, reasonLabel);
                        }
                        AppendCsvFields(line, [e.Name, e.Timestamp is long t ? GlobalLogFilter.PreciseTime(t, utc) : "",
                            reasonLabel, $"0x{e.Reason:X8}", Number(e.Usn), Convert.ToHexString(row.FileId), Convert.ToHexString(e.Parent),
                            Number(e.Position), Number(e.Size), Number(e.Timestamp), source, hash, $"{e.Version}.{e.Minor}", $"0x{e.Source:X8}",
                            Number(e.Security), e.Attributes is long a ? $"0x{a:X8}" : "", e.Extents, Number(e.Remaining),
                            e.Resynced ? L.T("예", "Yes") : "", e.Escaped ? L.T("예", "Yes") : "", Number(e.Segment), carvedSource ? L.T("카빙 후보", "Carved candidate") : L.T("저널 기록", "Journal record")]);
                        if (resolvePath != null)
                        {
                            var path = resolvePath(row);
                            AppendCsvFields(line, [path.HasKnownPath ? path.Display : "", path.HasKnownPath ? path.SourceLabel : "",
                                path.DisplayBasis, path.Explanation, mftSource ?? "", mftHash ?? ""]);
                        }
                        writer.WriteLine(line);
                        line.Clear();
                        count++;
                        if (progress != null && (count % 4096 == 0 || progressTimer.ElapsedMilliseconds >= 250))
                        {
                            progress.Report(count);
                            progressTimer.Restart();
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, overwrite: false);
                return count;
            }
            finally
            {
                stream.Dispose();
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }, token).ContinueOnInterrupt(token);
    }

    private static void AppendCsvFields(StringBuilder line, ReadOnlySpan<string> fields)
    {
        foreach (string value in fields)
        {
            if (line.Length > 0)
                line.Append(',');
            line.Append('"');
            var remaining = value.AsSpan();
            int quote;
            while ((quote = remaining.IndexOf('"')) >= 0)
            {
                line.Append(remaining[..quote]).Append("\"\"");
                remaining = remaining[(quote + 1)..];
            }
            line.Append(remaining).Append('"');
        }
    }

    private static string Number(long? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "";
    internal SqliteConnection Open(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        raw.sqlite3_progress_handler(db.Handle, 4096,
            static state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, token);
        return db;
    }

    private string FilterClause(SqliteCommand cmd, GlobalLogFilter filter, CancellationToken token)
    {
        var predicates = new List<string>();
        string search = filter.Name.Trim();
        if (search.Length > 0)
        {
            cmd.Connection!.CreateFunction<string?, bool>("matches_name", n => n?.Contains(search, StringComparison.OrdinalIgnoreCase) == true, isDeterministic: true);
            predicates.Add(filter.AllNames ? "file IN (SELECT file FROM aliases WHERE matches_name(name))" : "matches_name(name)");
        }
        void Add(string expression, string name, long? value)
        {
            if (!value.HasValue)
                return;
            predicates.Add(expression);
            cmd.Parameters.AddWithValue(name, value.Value);
        }
        Add("(reason & $mask) != 0", "$mask", filter.Reasons == 0 ? null : filter.Reasons);
        Add("usn = $exact", "$exact", filter.Usn);
        Add("stamp >= $from", "$from", filter.From);
        Add("stamp < $until", "$until", filter.Until);
        AddReference("file", filter.RecordFields.File);
        AddReference("parent", filter.RecordFields.Parent);
        if (filter.RecordFields.Path.Length != 0)
        {
            var source = pathSource ?? new JournalPathSource(new Journal(dbPath), null, null, false);
            cmd.Connection!.CreateFunction<byte[], byte[], string?, long, long, long, int, bool, bool>(
                "matches_path", (file, parent, name, pos, usn, segment, version, resynced) =>
                {
                    if (token.IsCancellationRequested || version == 4 || string.IsNullOrEmpty(name))
                        return false;
                    var entry = new Entry(pos, 0, version, parent, usn, null, 0, 0, null, null, name, 0,
                        resynced, false, null, "", segment);
                    var result = source.Resolve(entry, file);
                    return result.HasKnownPath && filter.RecordFields.MatchesPath(result.Display);
                }, isDeterministic: true);
            predicates.Add("matches_path(r.file,r.parent,r.name,r.pos,r.usn,r.segment,r.version,r.resynced)");
        }
        return predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);

        void AddReference(string column, NtfsReference? reference)
        {
            if (reference is not { } value)
                return;
            if (value.ExactBytes is { } bytes)
            {
                string parameter = "$" + column;
                cmd.Parameters.AddWithValue(parameter, bytes);
                predicates.Add($"r.{column} = {parameter}");
            }
            else
            {
                var minimum = (value with
                {
                    SlotOnly = false
                }).ExactBytes!;
                var maximum = (byte[])minimum.Clone();
                maximum[^2] = maximum[^1] = byte.MaxValue;
                string low = "$" + column + "_minimum", high = "$" + column + "_maximum";
                cmd.Parameters.AddWithValue(low, minimum);
                cmd.Parameters.AddWithValue(high, maximum);
                predicates.Add($"r.{column} BETWEEN {low} AND {high} AND length(r.{column}) = 8");
            }
        }
    }

    private SqliteCommand Command(SqliteConnection db, GlobalLogFilter filter, bool newest, GlobalLogCursor? after, int? limit, JournalSort sort, CancellationToken token, bool keysOnly = false)
    {
        var cmd = db.CreateCommand();
        string where = FilterClause(cmd, filter, token);
        string direction = newest ? "DESC" : "ASC";
        string comparison = newest ? "<" : ">";
        const string candidates = "SELECT pos,stamp,usn FROM filtered";
        string pageRows = candidates;
        if (after is not null)
        {
            cmd.Parameters.AddWithValue("$usn", after.Usn);
            cmd.Parameters.AddWithValue("$pos", after.Position);
            if (sort == JournalSort.Usn)
                pageRows += $" WHERE (usn,pos) {comparison} ($usn,$pos)";
            else if (after.Timestamp is long stamp)
            {
                cmd.Parameters.AddWithValue("$stamp", stamp);
                pageRows += $" WHERE (stamp,usn,pos) {comparison} ($stamp,$usn,$pos)";
                // NULLs come last in both directions. Two disjoint seeks avoid
                // walking from the beginning of the chronological index on each page.
                if (filter.From == null && filter.Until == null)
                    pageRows += $" UNION ALL {candidates} WHERE stamp IS NULL";
            }
            else
                pageRows += $" WHERE stamp IS NULL AND (usn,pos) {comparison} ($usn,$pos)";
        }
        if (limit.HasValue)
            cmd.Parameters.AddWithValue("$limit", limit.Value);
        // A page bounds the expensive row and extent lookups. A full export only
        // needs one ordered scan, without materializing every row a second time.
        string orderBy(string alias) => (sort == JournalSort.Time ? $"{alias}stamp {direction} NULLS LAST," : "") + $"{alias}usn {direction},{alias}pos {direction}";
        string prefix = limit.HasValue ? $"""
            WITH filtered AS NOT MATERIALIZED (SELECT pos,stamp,usn FROM records r {where}),
            page AS MATERIALIZED (
                {pageRows}
                ORDER BY {orderBy("")} LIMIT $limit
            )
            """ : "";
        string records = limit.HasValue ? "page p JOIN records r ON r.pos=p.pos" : "records r";
        string outerFilter = limit.HasValue ? "" : where;
        if (keysOnly)
        {
            cmd.CommandText = $"SELECT r.pos,r.stamp,r.usn FROM records r {where} ORDER BY {orderBy("r.")}";
            return cmd;
        }
        cmd.CommandText = $"""
            {prefix}
            SELECT {RecordProjection}, v.*
            FROM {records} LEFT JOIN range_records v ON v.pos=r.pos {outerFilter}
            ORDER BY {orderBy("r.")}
            """;
        return cmd;
    }

    internal Task<List<GlobalLogCursor?>> FindPageStarts(GlobalLogFilter filter, JournalSort sort,
        bool descending, IReadOnlyList<long>? order, long position, int pageSize, CancellationToken token) =>
        Task.Run(() =>
        {
            var starts = new List<GlobalLogCursor?> { null };
            if (order != null)
            {
                for (int index = 0; index < order.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    long id = order[descending ? order.Count - 1 - index : index];
                    if (id == position)
                        return starts;
                    if ((index + 1) % pageSize == 0)
                        starts.Add(new(null, 0, id)
                        {
                            Rank = index + 1
                        });
                }
            }
            else
            {
                using var db = Open(token);
                using var cmd = Command(db, filter, descending, null, null, sort, token, keysOnly: true);
                using var reader = cmd.ExecuteReader();
                int count = 0;
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    long id = reader.GetInt64(0);
                    if (id == position)
                        return starts;
                    if (++count % pageSize == 0)
                        starts.Add(new(reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2), id)
                        {
                            Rank = count
                        });
                }
            }
            return new List<GlobalLogCursor?> { null };
        }, token).ContinueOnInterrupt(token);

    // Only identities are retained between pages. The immutable offset order belongs
    // to one filter and evidence pairing; rows and raw extents are read on demand.
    private static SqliteCommand RankedCommand(SqliteConnection db, IEnumerable<long> positions, bool descending)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT {RecordProjection},v.* FROM json_each($positions) k
            CROSS JOIN records r ON r.pos=k.value LEFT JOIN range_records v ON v.pos=r.pos
            ORDER BY k.key {(descending ? "DESC" : "ASC")}
            """;
        cmd.Parameters.AddWithValue("$positions", System.Text.Json.JsonSerializer.Serialize(positions));
        return cmd;
    }

    internal Task<IReadOnlyList<long>> BuildLabelOrderAsync(GlobalLogFilter filter, bool byReason, CancellationToken token) =>
        Task.Run<IReadOnlyList<long>>(() =>
        {
            using var db = Open(token);
            using var cmd = db.CreateCommand();
            string projection = byReason ? "reason,version" : "name";
            cmd.CommandText = $"SELECT pos,{projection} FROM records r {FilterClause(cmd, filter, token)} ORDER BY pos";
            using var reader = cmd.ExecuteReader();
            var groups = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var labels = new Dictionary<(long, int), string>();
            int count = 0;
            string unnamed = L.T("이름이 기록되지 않음", "No name recorded");
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                string label;
                if (byReason)
                {
                    var key = (reader.GetInt64(1), reader.GetInt32(2));
                    if (!labels.TryGetValue(key, out var cached))
                        labels[key] = cached = GlobalLogRow.ReasonTitle(key.Item2, key.Item1);
                    label = cached;
                }
                else
                    label = !reader.IsDBNull(1) && reader.GetString(1) is { Length: > 0 } name ? name : unnamed;
                AddOffset(groups, label, reader.GetInt64(0));
                count++;
            }
            return Order(groups, count, token);
        }, token).ContinueOnInterrupt(token);

    internal const string RecordProjection = """
        r.file,r.pos,r.size,r.version,r.parent,r.usn,r.stamp,r.reason,r.source,
        r.security,r.attr,r.name,r.minor,r.resynced,r.escaped,v.remaining,
        CASE WHEN r.version=4 THEN
          (SELECT group_concat(printf('%lld + %lld',start,length),'; ' ORDER BY ordinal)
           FROM extents WHERE pos=r.pos) END,r.segment
        """;

    internal static GlobalLogRow Row(SqliteDataReader reader, JournalTimeZone utc)
    {
        var record = new Entry(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3),
            (byte[])reader[4], reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetInt64(7), reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.IsDBNull(11) ? "" : reader.GetString(11),
            reader.GetInt32(12), reader.GetBoolean(13), reader.GetBoolean(14),
            reader.IsDBNull(15) ? null : reader.GetInt64(15), reader.IsDBNull(16) ? "" : reader.GetString(16),
            reader.GetInt64(17))
        {
            ExtentStride = reader.GetInt32(3) == 4 ? Entry.ReadExtentStride(reader, 18) : null
        };
        return new((byte[])reader[0], record, utc);
    }
}

internal static class GlobalLogCancellation
{
    internal static async Task<T> ContinueOnInterrupt<T>(this Task<T> task, CancellationToken token)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT && token.IsCancellationRequested)
        {
            throw new OperationCanceledException(L.T("로그 조회를 취소했습니다.", "Log query cancelled."), ex, token);
        }
    }
}
