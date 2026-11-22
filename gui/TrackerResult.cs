using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace JournalDesk;

internal sealed record TrackerTable(string Name, string[] Columns, string[] Headers, string Order)
{
    internal string[] DisplayHeaders { get; } = MakeDisplayHeaders(Headers);

    private static string[] MakeDisplayHeaders(string[] headers)
    {
        var counts = headers.CountBy(name => name, StringComparer.Ordinal).ToDictionary();
        return headers.Select((name, i) => string.IsNullOrWhiteSpace(name)
            ? L.T("열 {0} (이름 없음)", "Column {0} (unnamed)", i + 1)
            : counts[name] > 1 ? L.T("{0} (열 {1})", "{0} (column {1})", name, i + 1) : name).ToArray();
    }
}
internal sealed record TrackerOrder(int Column, bool Descending);
internal sealed record TrackerPage(List<TrackerCell[]> Rows, bool HasMore);

internal sealed record TrackerCell(string Type, string? Value)
{
    public bool IsPlaceholder => Value is null or "";
    public string Display => (Type, Value) switch
    {
        ("NULL", _) => "NULL",
        ("BLOB", "") => L.T("0바이트", "0 bytes"),
        (_, "") => L.T("빈 문자열", "Empty string"),
        _ => Value!
    };
    public string Preview => Display.ReplaceLineEndings(" ↵ ");
    public string DetailPlaceholder => Type == "NULL" ? L.T("값 없음", "No value") : Display;
    public string Storage => Type == "BLOB" && !string.IsNullOrEmpty(Value)
        ? L.T("BLOB · {0:N0}바이트", "BLOB · {0:N0} bytes", Value!.Length / 2) : Type;

    internal static TrackerCell Read(SqliteDataReader reader, int column) => reader.GetValue(column) switch
    {
        DBNull => new("NULL", null),
        byte[] bytes => new("BLOB", Convert.ToHexString(bytes)),
        long number => new("INTEGER", number.ToString(CultureInfo.InvariantCulture)),
        double number => new("REAL", number.ToString("R", CultureInfo.InvariantCulture)),
        string text => new("TEXT", text),
        var value => throw new InvalidDataException(value.GetType().Name)
    };
}

internal sealed class TrackerResult : IDisposable
{
    private static readonly string[] RowIdAliases = ["rowid", "_rowid_", "oid"];
    private readonly string database;
    private readonly bool temporary;
    private readonly FileStream original;
    public IReadOnlyList<TrackerTable> Tables { get; private set; } = [];
    public string SourceName => original.Name;
    public bool IsTextFile => temporary;
    public string Format { get; private set; } = L.T("Tracker 결과 DB", "Tracker results database");

    private TrackerResult(string source, bool csv)
    {
        original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        temporary = csv;
        database = csv ? Path.Combine(Path.GetTempPath(), "journaldesk-tracker-" + Guid.NewGuid().ToString("N") + ".sqlite") : original.Name;
    }
    public static TrackerResult Load(string path, CancellationToken token)
    {
        bool csv = Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".tsv";
        var result = new TrackerResult(path, csv);
        try
        {
            if (csv)
                result.ImportText(token);
            else
                result.ReadSchema(token);
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public void Dispose()
    {
        original.Dispose();
        if (temporary)
        {
            try
            {
                File.Delete(database);
            }
            catch (IOException) { }
        }
    }
    private SqliteConnection Open(CancellationToken token, bool create = false)
    {
        token.ThrowIfCancellationRequested();
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        try
        {
            db.Open();
            raw.sqlite3_progress_handler(db.Handle, 4096, static t => ((CancellationToken)t).IsCancellationRequested ? 1 : 0, token);
            using var cmd = db.CreateCommand();
            cmd.CommandText = create ? "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF" : "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON";
            cmd.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static string Id(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private void ReadSchema(CancellationToken token)
    {
        using var db = Open(token);
        var names = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_list WHERE schema='main' AND type='table' AND name NOT GLOB 'sqlite_*' ORDER BY CASE lower(name) WHEN 'usnjrnl' THEN 0 WHEN 'logfile' THEN 1 ELSE 2 END,name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                names.Add(r.GetString(0));
        }
        if (names.Count == 0 || names.Count == 1 && names[0].Equals("SignatureTable", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("분석 결과 테이블을 찾지 못했습니다. Tracker에서 저장한 결과 DB 또는 CSV를 선택해 주세요.", "No analysis tables found. Select a results database or CSV saved by Tracker."));
        var tables = new List<TrackerTable>();
        foreach (string name in names)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT name,pk FROM pragma_table_info($name) ORDER BY cid";
            cmd.Parameters.AddWithValue("$name", name);
            using var r = cmd.ExecuteReader();
            var cols = new List<string>();
            var keys = new SortedDictionary<long, string>();
            while (r.Read())
            {
                cols.Add(r.GetString(0));
                if (r.GetInt64(1) > 0)
                    keys[r.GetInt64(1)] = r.GetString(0);
            }
            if (cols.Count == 0)
                continue;
            var orderKeys = keys.Values.Select(Id).ToList();
            string? rowid = RowIdAliases.FirstOrDefault(alias => !cols.Contains(alias, StringComparer.OrdinalIgnoreCase));
            using var storage = db.CreateCommand();
            storage.CommandText = "SELECT wr FROM pragma_table_list WHERE schema='main' AND name=$name";
            storage.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt64(storage.ExecuteScalar(), CultureInfo.InvariantCulture) == 0 && rowid != null)
                orderKeys.Add(rowid);
            if (orderKeys.Count == 0)
                orderKeys.AddRange(cols.Select(Id));
            string order = string.Join(",", orderKeys);
            var headers = cols.ToArray();
            tables.Add(new(name, headers, headers, order));
        }
        if (tables.Count == 0)
            throw new InvalidDataException(L.T("표시할 열이 있는 테이블을 찾지 못했습니다.", "No table with displayable columns was found."));
        Tables = tables;
    }
    private void ImportText(CancellationToken token)
    {
        byte[] probe = new byte[4096];
        int n = original.Read(probe);
        original.Position = 0;
        Encoding encoding;
        if (n >= 2 && probe[0] == 0xFF && probe[1] == 0xFE)
            encoding = Encoding.Unicode;
        else if (n >= 2 && probe[0] == 0xFE && probe[1] == 0xFF)
            encoding = Encoding.BigEndianUnicode;
        else if (HasUtf16Separator(probe.AsSpan(0, n), false))
            encoding = Encoding.Unicode;
        else if (HasUtf16Separator(probe.AsSpan(0, n), true))
            encoding = Encoding.BigEndianUnicode;
        else
            encoding = new UTF8Encoding(false, true);
        try
        {
            ImportText(encoding, token);
        }
        catch (DecoderFallbackException) when (encoding.CodePage == 65001)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            original.Position = 0;
            File.Delete(database);
            ImportText(Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), token);
        }
    }
    private static bool HasUtf16Separator(ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        int punctuation = bigEndian ? 1 : 0, zero = 1 - punctuation;
        for (int i = 0; i + 1 < bytes.Length; i += 2)
            if (bytes[i + zero] == 0 && bytes[i + punctuation] is 9 or 10 or 13 or 44)
                return true;
        return false;
    }
    private void ImportText(Encoding encoding, CancellationToken token)
    {
        char delimiter;
        using (var sample = new StreamReader(original, encoding, true, 8192, true))
        {
            string line = sample.ReadLine() ?? throw new InvalidDataException(L.T("결과 파일에 열 이름이 없습니다.", "The results file has no column names."));
            delimiter = DetectDelimiter(line);
        }
        original.Position = 0;
        using var input = new StreamReader(original, encoding, true, 65536, true);
        using var rows = CsvRows(input, delimiter, token).GetEnumerator();
        if (!rows.MoveNext())
            throw new InvalidDataException(L.T("결과 파일이 비었습니다.", "The results file is empty."));
        string[] headers = rows.Current;
        var cols = Enumerable.Range(0, headers.Length).Select(i => "c" + i).ToArray();
        var table = new TrackerTable(L.T("결과", "Results"), cols, headers, "rowid");
        using var db = Open(token, true);
        using var tx = db.BeginTransaction();
        using (var create = db.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = $"CREATE TABLE {Id(table.Name)} ({string.Join(",", cols.Select(c => Id(c) + " TEXT"))})";
            create.ExecuteNonQuery();
        }
        using var add = db.CreateCommand();
        add.Transaction = tx;
        add.CommandText = $"INSERT INTO {Id(table.Name)} VALUES ({string.Join(",", cols.Select(c => "$" + c))})";
        foreach (var col in cols)
            add.Parameters.Add("$" + col, SqliteType.Text);
        add.Prepare();
        long row = 1;
        while (rows.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            row++;
            var fields = rows.Current;
            if (cols.Length > 1 && fields.Length == 1 && fields[0].Length == 0)
                continue;
            if (fields.Length != cols.Length)
                throw new InvalidDataException(L.T("결과 파일의 {0:N0}번째 행에서 열 {1}개를 읽었습니다. 머리글의 열은 {2}개입니다.", "Row {0:N0} has {1} columns. The header has {2}.", row, fields.Length, cols.Length));
            for (int i = 0; i < cols.Length; i++)
                add.Parameters[i].Value = fields[i];
            add.ExecuteNonQuery();
        }
        tx.Commit();
        Format = L.T("{0} 파일 · {1}", "{0} file · {1}", (delimiter == '\t' ? L.T("탭 구분", "Tab-separated") : L.T("쉼표 구분", "Comma-separated")), input.CurrentEncoding.WebName);
        Tables = [table];
    }
    private static char DetectDelimiter(string line)
    {
        int commas = 0, tabs = 0;
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"')
                quoted = !quoted;
            else if (!quoted)
            {
                if (c == ',')
                    commas++;
                if (c == '\t')
                    tabs++;
            }
        }
        return tabs > commas ? '\t' : ',';
    }
    private static IEnumerable<string[]> CsvRows(TextReader input, char separator, CancellationToken token)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        bool quoted = false, started = false;
        int ch, ticks = 0;
        while ((ch = input.Read()) != -1)
        {
            if (++ticks == 8192)
            {
                token.ThrowIfCancellationRequested();
                ticks = 0;
            }
            char c = (char)ch;
            if (quoted)
            {
                if (c != '"')
                {
                    value.Append(c);
                    continue;
                }
                if (input.Peek() == '"')
                {
                    input.Read();
                    value.Append('"');
                }
                else
                    quoted = false;
                continue;
            }
            if (c == '"' && !started)
            {
                quoted = true;
                started = true;
                continue;
            }
            if (c == separator)
            {
                fields.Add(value.ToString());
                value.Clear();
                started = false;
                continue;
            }
            if (c == '\n' || c == '\r')
            {
                if (c == '\r' && input.Peek() == '\n')
                    input.Read();
                fields.Add(value.ToString());
                yield return fields.ToArray();
                fields.Clear();
                value.Clear();
                started = false;
                continue;
            }
            value.Append(c);
            started = true;
        }
        if (quoted)
            throw new InvalidDataException(L.T("결과 파일의 마지막 따옴표를 찾을 수 없습니다.", "The results file has an unclosed quoted field."));
        if (started || fields.Count > 0)
        {
            fields.Add(value.ToString());
            yield return fields.ToArray();
        }
    }
    private static string Order(TrackerTable table, TrackerOrder? sort) => sort == null ? table.Order :
        Id(table.Columns[sort.Column]) + (sort.Descending ? " DESC," : " ASC,") + table.Order;

    private static SqliteCommand Query(SqliteConnection db, TrackerTable table, string term, TrackerOrder? sort,
        string? projection = null)
    {
        var cmd = db.CreateCommand();
        string where = "";
        if (term.Length > 0)
        {
            where = " WHERE " + string.Join(" OR ", table.Columns.Select(col => $"instr(lower(CASE typeof({Id(col)}) WHEN 'blob' THEN hex({Id(col)}) ELSE CAST({Id(col)} AS TEXT) END),lower($term))>0"));
            cmd.Parameters.AddWithValue("$term", term);
        }
        cmd.CommandText = $"SELECT {projection ?? string.Join(",", table.Columns.Select(Id))} FROM {Id(table.Name)}{where}";
        if (projection == null)
            cmd.CommandText += " ORDER BY " + Order(table, sort);
        return cmd;
    }
    public long LocateAfterSort(TrackerTable table, string term, long position, TrackerOrder? previous, TrackerOrder next, CancellationToken token)
    {
        using var db = Open(token);
        string ranks = $"row_number() OVER(ORDER BY {Order(table, previous)}) AS prior,row_number() OVER(ORDER BY {Order(table, next)}) AS current";
        using var cmd = Query(db, table, term, previous, ranks);
        cmd.CommandText = $"SELECT current-1 FROM ({cmd.CommandText}) WHERE prior=$prior";
        cmd.Parameters.AddWithValue("$prior", position + 1);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    public TrackerPage Read(TrackerTable table, string term, int page, int size, CancellationToken token, TrackerOrder? sort = null)
    {
        using var db = Open(token);
        using var cmd = Query(db, table, term, sort);
        cmd.CommandText += " LIMIT $size OFFSET $start";
        cmd.Parameters.AddWithValue("$size", size + 1);
        cmd.Parameters.AddWithValue("$start", (long)page * size);
        using var r = cmd.ExecuteReader();
        var rows = new List<TrackerCell[]>(size);
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            if (rows.Count == size)
                return new(rows, true);
            var values = new TrackerCell[r.FieldCount];
            for (int i = 0; i < values.Length; i++)
                values[i] = TrackerCell.Read(r, i);
            rows.Add(values);
        }
        return new(rows, false);
    }
    public long Export(TrackerTable table, string term, string filename, CancellationToken token, TrackerOrder? sort = null)
    {
        if (Path.GetFullPath(filename).Equals(original.Name, StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("분석 결과 원본과 다른 저장 위치를 선택해 주세요.", "Choose a destination other than the source results file."));
        string temp = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var db = Open(token);
            using var cmd = Query(db, table, term, sort);
            using var r = cmd.ExecuteReader();
            long written = 0;
            using (var output = new StreamWriter(temp, false, new UTF8Encoding(true)))
            {
                static string Escape(string? text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
                output.WriteLine(string.Join(",", table.Headers.Select(Escape)));
                while (r.Read())
                {
                    token.ThrowIfCancellationRequested();
                    written++;
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        if (i > 0)
                            output.Write(',');
                        output.Write(Escape(TrackerCell.Read(r, i).Value));
                    }
                    output.WriteLine();
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, filename, true);
            return written;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
