using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace JournalDesk;

internal enum TransactionSort
{
    Lsn, Operation, Name, Path, PathSource, TransactionId
}
internal sealed record TxRow(long Id, string Lsn, string Label, string Name, long Tx,
    string PathText, string PathSource, string PathEvidence, string? MftSource = null, string? MftHash = null)
{
    public IReadOnlyList<string> FileIds { get; init; } = [];
    public IReadOnlyList<string> ParentIds { get; init; } = [];
    public override string ToString() => $"{Label} · {Name} · LSN {Lsn}";
}
internal sealed record TxSummary(string Hash, long Bytes, long Records, long Warnings);
internal sealed record TxPayload(string Operation, long? DeclaredLength, byte[] Bytes);
internal sealed record TxFact(string Label, string Text, Func<JournalTimeZone, string>? Format = null)
{
    public string Value => Format?.Invoke(DisplayTimeZone.Current.Zone) ?? Text;
}
internal sealed record TxDetail(TxRow Record, IReadOnlyList<TxFact> Facts, long Position, byte[] Header, TxPayload Redo, TxPayload Undo,
    string PreviousLsn, string NextUndoLsn, string? NonUpdateType)
{
    public string Info => string.Join(Environment.NewLine, Facts.Select(f => f.Label.Length == 0 ? f.Value : f.Label + "   " + f.Value));
    public string OriginalPosition => L.T("0x{0:X} ({1:N0}바이트)", "0x{0:X} ({1:N0} bytes)", Position, Position);
    public string TransactionIdNote { get; } = L.T("트랜잭션 ID는 재사용될 수 있습니다.", "Transaction IDs can be reused.");
    public string CopyText() => string.Join(Environment.NewLine, new[]{
        $"{Record.Label} · LSN 0x{Record.Lsn}",$"Redo   {Redo.Operation}",$"Undo   {Undo.Operation}",
        L.T("트랜잭션 ID   {0} ({1})", "Transaction ID   {0} ({1})", Record.Tx, TransactionIdNote),L.T("원본 위치   {0}", "Source offset   {0}", OriginalPosition),
        L.T("이전 LSN   {0}", "Previous LSN   {0}", PreviousLsn),L.T("다음 Undo LSN   {0}", "Next Undo LSN   {0}", NextUndoLsn),
        $"{L.Caption("Path")}   {Record.PathText}",
        $"{L.Caption("PathSourceColumn")}   {Record.PathSource}",
        Record.FileIds.Count == 0 ? "" : $"{L.Caption("FilterFileId")}   {string.Join(" · ", Record.FileIds)}",
        Record.ParentIds.Count == 0 ? "" : $"{L.Caption("FilterParentId")}   {string.Join(" · ", Record.ParentIds)}",
        Record.PathEvidence,
        Record.MftSource == null ? "" : $"MFT   {Record.MftSource}",
        Record.MftHash == null ? "" : $"SHA-256   {Record.MftHash}",
        Info
    }.Where(value => value.Length > 0));
}

internal sealed record TransactionMft(PathResolver Resolver, string SourcePath, string Hash);

internal sealed class TransactionQuery(string path, TransactionMft? mft = null)
{
    private const int DefaultPageSize = 200;
    private static readonly PathResolver LogPaths = new(null, null);
    private PathResolver Paths => mft?.Resolver ?? LogPaths;
    internal TransactionMft? Mft => mft;
    internal TransactionQuery WithMft(TransactionMft? attachment) => new(path, attachment);

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        try
        {
            db.Open();
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    internal TxSummary Summary()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        EngineIndexFormat.Validate(db, EngineArtifact.TransactionLog);
        cmd.CommandText = "SELECT hash,bytes,records,warnings FROM tx_summary";
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            throw new InvalidDataException(L.T("트랜잭션 색인에서 분석 결과를 읽지 못했습니다.", "Could not read analysis results from the transaction index."));
        return new(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }
    internal string DescribeSource(string original, TxSummary summary, CancellationToken token)
    {
        var text = new StringBuilder();
        text.AppendLine(L.T("원본 파일   {0}", "Source file   {0}", original));
        text.AppendLine(L.T("파일 크기   {0:N0}바이트", "File size   {0:N0} bytes", summary.Bytes));
        text.Append("SHA-256   ").AppendLine(summary.Hash);
        text.AppendLine(L.T("판독한 로그 레코드   {0:N0}건", "Decoded log records   {0:N0}", summary.Records));
        text.AppendLine().AppendLine(L.T("엔진이 보고한 해석 경고   {0:N0}건", "Warnings reported by the engine   {0:N0}", summary.Warnings));
        using var db = Open();
        raw.sqlite3_progress_handler(db.Handle, 4096, static state =>
            ((CancellationToken)state).IsCancellationRequested ? 1 : 0, token);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM tx_provenance WHERE kind='prior_pass_slack'";
        long prior = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (prior != 0)
            text.AppendLine(L.T("이전 순환의 빈 공간에 남은 레코드   {0:N0}건 (이동 후보 산출에서 제외)", "Records from an earlier cycle in unused space   {0:N0} (excluded from move candidates)", prior));
        if (summary.Warnings == 0)
            return text.ToString().TrimEnd();
        cmd.CommandText = "SELECT pos,reason FROM tx_warnings ORDER BY pos LIMIT 100";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            text.AppendLine(L.T("원본 0x{0:X}   ·   {1}", "Source 0x{0:X}   ·   {1}", r.GetInt64(0), r.GetString(1)));
        if (summary.Warnings > 100)
            text.AppendLine(L.T("원본 위치 순으로 처음 100건을 표시합니다.", "The first 100 are listed in source offset order."));
        return text.ToString().TrimEnd();
    }

    internal List<TxRow> Page(string search, int filter, string after, CancellationToken token,
        bool descending = false, IReadOnlyList<long>? order = null, int start = 0, int pageSize = DefaultPageSize,
        RecordSearch? records = null)
    {
        using var db = Open();
        raw.sqlite3_progress_handler(db.Handle, 4096, static state =>
            ((CancellationToken)state).IsCancellationRequested ? 1 : 0, token);
        using var cmd = db.CreateCommand();
        string condition = Condition(db, search, filter, records, token);
        bool candidates = filter == 3 && ExactLsn(search) == null;
        string names = NamesExpression(candidates);
        string direction = descending ? "DESC" : "ASC";
        string rowSource = "tx_records r", where = condition, orderBy = "r.lsn " + direction;
        if (order != null)
        {
            rowSource = "json_each($positions) k CROSS JOIN tx_records r ON r.id=k.value";
            var slice = order.Skip(descending ? Math.Max(0, order.Count - start - pageSize - 1) : start)
                .Take(Math.Min(pageSize + 1, order.Count - start));
            cmd.Parameters.AddWithValue("$positions", JsonSerializer.Serialize(slice));
            where = "1";
            orderBy = "k.key " + direction;
        }
        else if (after.Length > 0)
        {
            cmd.Parameters.AddWithValue("$after", after);
            where = $"r.lsn {(descending ? "<" : ">")} $after AND {condition}";
        }
        cmd.CommandText = $"""
            SELECT r.id,r.lsn,r.redo,r.txid,{names},p.kind FROM {rowSource}
            LEFT JOIN tx_provenance p ON p.id=r.id
            WHERE {where} ORDER BY {orderBy} LIMIT {pageSize + 1}
            """;
        var rows = new List<TxRow>();
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                string title = OperationLabel(r.GetString(2), candidates, !r.IsDBNull(5));
                var links = ReadNames(r.GetString(4));
                var displayed = DescribePaths(links);
                var observed = links.SelectMany(group => group);
                rows.Add(new(r.GetInt64(0), r.GetString(1), title, displayed.Names, r.GetInt64(3),
                    displayed.Paths, displayed.Source, displayed.Evidence,
                    displayed.UsesMft ? mft?.SourcePath : null, displayed.UsesMft ? mft?.Hash : null)
                {
                    FileIds = DisplayIds(observed.Select(link => link.File)),
                    ParentIds = DisplayIds(observed.Select(link => link.Parent))
                });
            }
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        return rows;
    }

    // Keep each leaf and its recorded parent together, including both sides of a candidate.
    private static string NamesExpression(bool candidates) => candidates
            ? "(SELECT json_group_array(json_array(old_name,hex(file),hex(old_parent),new_name,hex(file),hex(new_parent))) FROM tx_moves WHERE new_id=r.id)"
            : "(SELECT json_group_array(json_array(name,hex(file),hex(parent))) FROM (SELECT DISTINCT name,file,parent FROM tx_names WHERE id=r.id ORDER BY name,file,parent))";

    private static string OperationLabel(string redo, bool candidate, bool earlier) =>
        (candidate ? L.T("이동·이름 변경 후보", "Move or rename candidate") : Label(redo)) +
        (earlier ? L.T(" · 이전 순환 흔적", " · Earlier cycle") : "");

    internal IReadOnlyList<long> BuildOrder(string search, int filter, TransactionSort sort, CancellationToken token, RecordSearch? records = null)
    {
        using var db = Open();
        raw.sqlite3_progress_handler(db.Handle, 4096, static state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, token);
        using var cmd = db.CreateCommand();
        bool candidates = filter == 3 && ExactLsn(search) == null;
        string names = sort is TransactionSort.Path or TransactionSort.Name or TransactionSort.PathSource ? NamesExpression(candidates) : "NULL";
        cmd.CommandText = $"""
            SELECT r.id,r.redo,p.kind,{names} FROM tx_records r LEFT JOIN tx_provenance p ON p.id=r.id
            WHERE {Condition(db, search, filter, records, token)} ORDER BY {(sort == TransactionSort.TransactionId ? "r.txid,r.lsn" : "r.id")}
            """;
        var ids = new List<long>();
        var keyed = new List<(string Value, long Id)>();
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                long id = reader.GetInt64(0);
                if (sort == TransactionSort.TransactionId)
                {
                    ids.Add(id);
                    continue;
                }
                string value;
                if (sort == TransactionSort.Operation)
                    value = OperationLabel(reader.GetString(1), candidates, !reader.IsDBNull(2));
                else
                {
                    var observed = ReadNames(reader.GetString(3));
                    if (sort == TransactionSort.Name)
                        value = DisplayNames(observed);
                    else
                    {
                        var displayed = DescribePaths(observed);
                        value = sort == TransactionSort.Path ? displayed.Paths : displayed.Source;
                    }
                }
                keyed.Add((value, id));
            }
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        if (sort == TransactionSort.TransactionId)
            return ids;
        keyed.Sort((a, b) =>
        {
            int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Value, b.Value);
            return cmp == 0 ? a.Id.CompareTo(b.Id) : cmp;
        });
        token.ThrowIfCancellationRequested();
        return keyed.Select(item => item.Id).ToArray();
    }

    internal List<string> FindPageStarts(string search, int filter, bool descending, IReadOnlyList<long>? order,
        TxRow anchor, CancellationToken token, int pageSize = DefaultPageSize, RecordSearch? records = null)
    {
        var starts = new List<string> { "" };
        if (order != null)
        {
            for (int i = 0; i < order.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (order[descending ? order.Count - 1 - i : i] == anchor.Id)
                    return starts;
                if ((i + 1) % pageSize == 0)
                    starts.Add(""); // Ranked pages use the absolute page number.
            }
        }
        else
        {
            using var db = Open();
            raw.sqlite3_progress_handler(db.Handle, 4096, static state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, token);
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT r.lsn FROM tx_records r WHERE {Condition(db, search, filter, records, token)} ORDER BY r.lsn {(descending ? "DESC" : "ASC")}";
            try
            {
                using var reader = cmd.ExecuteReader();
                int count = 0;
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    string lsn = reader.GetString(0);
                    if (lsn == anchor.Lsn)
                        return starts;
                    if (++count % pageSize == 0)
                        starts.Add(lsn);
                }
            }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
        return [""];
    }

    private sealed record ObservedName(string Name, byte[]? File, byte[]? Parent);
    private sealed record DisplayedPaths(string Names, string Paths, string Source, string Evidence, bool UsesMft);

    private static List<ObservedName[]> ReadNames(string json)
    {
        var links = new List<ObservedName[]>();
        using var parsed = JsonDocument.Parse(json);
        foreach (var pair in parsed.RootElement.EnumerateArray())
        {
            var group = new ObservedName[pair.GetArrayLength() / 3];
            for (int i = 0; i < group.Length; i++)
            {
                int index = i * 3;
                group[i] = new(pair[index].GetString() ?? "", DecodeReference(pair[index + 1]),
                    DecodeReference(pair[index + 2]));
            }
            links.Add(group);
        }
        return links;
    }

    private static byte[]? DecodeReference(JsonElement field) => field.GetString() is { Length: 16 } hex
        ? Convert.FromHexString(hex) : null;

    private static string DisplayNames(List<ObservedName[]> links) => links.Count == 0 ? "—" :
        string.Join(" · ", links.Select(group => string.Join(" → ", group.Select(link => link.Name))).Distinct(StringComparer.Ordinal));

    private static string[] DisplayIds(IEnumerable<byte[]?> values) =>
        values.Select(NtfsReference.DisplayId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

    private DisplayedPaths DescribePaths(List<ObservedName[]> links)
    {
        if (links.Count == 0)
            return new("—", "—", "—", L.T("이 작업에서 판독한 파일 이름이 없습니다.", "No file name was decoded for this operation."), false);
        var paths = new List<string>();
        var evidence = new List<string>();
        bool multipleLinks = links.Count != 1 || links[0].Length != 1;
        bool usesMft = false, hasPartial = false, hasKnownPath = false;
        foreach (var group in links)
        {
            var locations = new List<string>(group.Length);
            foreach (var link in group)
            {
                var resolved = Paths.ResolveTransactionName(link.Name, link.Parent);
                usesMft |= resolved.IsEstimated;
                hasPartial |= resolved.IsPartial;
                hasKnownPath |= resolved.HasKnownPath;
                locations.Add(resolved.Display.ReplaceLineEndings(" | "));
                var lines = new List<string>();
                if (multipleLinks)
                    lines.Add(resolved.Display);
                if (resolved.Explanation.Length > 0)
                    lines.Add(resolved.Explanation);
                if (multipleLinks)
                {
                    if (NtfsReference.DisplayId(link.File) is string file)
                        lines.Add($"{L.Caption("FilterFileId")}  {file}");
                    if (NtfsReference.DisplayId(link.Parent) is string parent)
                        lines.Add($"{L.Caption("FilterParentId")}  {parent}");
                }
                evidence.Add(string.Join(Environment.NewLine, lines));
            }
            paths.Add(string.Join(" → ", locations));
        }
        string source = usesMft ? L.Caption("TransactionPathFromMft") :
            hasPartial ? L.Caption("PartialPath") : hasKnownPath ? "$LogFile" : "—";
        return new(DisplayNames(links), string.Join(" · ", paths.Distinct(StringComparer.Ordinal)),
            source, string.Join(Environment.NewLine + Environment.NewLine, evidence.Distinct(StringComparer.Ordinal)), usesMft);
    }

    internal static string? ExactLsn(string search)
    {
        string value = search.Trim();
        bool prefixed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (prefixed)
            value = value[2..];
        if (value.Length is < 1 or > 16 || (!prefixed && value.Length != 16) ||
           !ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong lsn))
            return null;
        return lsn.ToString("X16", CultureInfo.InvariantCulture);
    }
    private string Condition(SqliteConnection db, string search, int filter, RecordSearch? records, CancellationToken token)
    {
        string? lsn = ExactLsn(search);
        var predicates = new List<string>();
        if (lsn != null)
        {
            // The parser returns exactly sixteen uppercase hexadecimal digits.
            predicates.Add($"r.lsn='{lsn}'");
            if (records is not { IsEmpty: false })
                return predicates[0];
        }
        bool searchName = lsn == null && search.Length != 0;
        if (records is { IsEmpty: false })
        {
            db.CreateFunction<string, byte[]?, byte[]?, bool>("matches_name_link", (name, file, parent) =>
            {
                token.ThrowIfCancellationRequested();
                if ((searchName && !name.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                    (records.File is { } fileRef && !fileRef.Matches(file)) ||
                    (records.Parent is { } parentRef && !parentRef.Matches(parent)))
                    return false;
                if (records.Path.Length == 0)
                    return true;
                var path = Paths.ResolveTransactionName(name, parent);
                return path.HasKnownPath && records.MatchesPath(path.Display);
            }, isDeterministic: true);
            predicates.Add(filter == 3 && lsn == null
                ? "r.id IN (SELECT new_id FROM tx_moves WHERE matches_name_link(old_name,file,old_parent) OR matches_name_link(new_name,file,new_parent))"
                : "r.id IN (SELECT id FROM tx_names WHERE matches_name_link(name,file,parent))");
        }
        else
        {
            if (searchName)
                db.CreateFunction<string, bool>("matches_name", text => text.Contains(search, StringComparison.OrdinalIgnoreCase), true);
            if (filter == 3)
            {
                string matching = searchName ? "WHERE matches_name(old_name) OR matches_name(new_name)" : "";
                predicates.Add($"r.id IN (SELECT new_id FROM tx_moves {matching})");
            }
            else if (searchName)
                predicates.Add("r.id IN (SELECT id FROM tx_names WHERE matches_name(name))");
            else if (filter == 1)
                predicates.Add("EXISTS (SELECT 1 FROM tx_names WHERE id=r.id)");
        }
        if (filter == 2 && lsn == null)
            predicates.Add("""
                r.id IN (SELECT a.id FROM tx_times a JOIN tx_times b ON b.id=a.id
                    WHERE a.side='redo' AND b.side='undo' AND
                    (a.created<b.created OR a.modified<b.modified OR a.changed<b.changed OR a.accessed<b.accessed))
                """);
        return predicates.Count == 0 ? "1" : string.Join(" AND ", predicates);
    }

    internal long Export(string destination, string search, int filter, string source, string sha, CancellationToken token,
        bool descending = false, IReadOnlyList<long>? order = null, RecordSearch? records = null)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        string temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".partial");
        using var db = Open();
        raw.sqlite3_progress_handler(db.Handle, 4096, static state =>
            ((CancellationToken)state).IsCancellationRequested ? 1 : 0, token);
        using var cmd = db.CreateCommand();
        string rowSource = order == null ? "tx_records r" : "json_each($positions) k CROSS JOIN tx_records r ON r.id=k.value";
        string direction = descending ? "DESC" : "ASC";
        if (order != null)
            cmd.Parameters.AddWithValue("$positions", JsonSerializer.Serialize(order));
        cmd.CommandText = $"""
            SELECT r.lsn,r.prev,r.undo_next,r.pos,r.txid,
              CASE WHEN r.redo<>'Checkpoint' AND r.redo NOT LIKE 'RecordType %' THEN r.redo END,
              r.undo,r.redo_len,r.undo_len,
              (SELECT group_concat(name,' | ') FROM tx_names WHERE id=r.id AND side='undo'),
              (SELECT group_concat(name,' | ') FROM tx_names WHERE id=r.id AND side='redo'),
              u.created,v.created,u.modified,v.modified,u.changed,v.changed,u.accessed,v.accessed,
              (SELECT group_concat(old_name || ' → ' || new_name,' | ') FROM tx_moves WHERE old_id=r.id OR new_id=r.id),
              (SELECT group_concat(o.lsn || '@' || o.pos || ' → ' || n.lsn || '@' || n.pos,' | ')
               FROM tx_moves m JOIN tx_records o ON o.id=m.old_id JOIN tx_records n ON n.id=m.new_id
               WHERE m.old_id=r.id OR m.new_id=r.id),p.kind,$source,$sha,length(r.redodata),length(r.undodata),
              CASE WHEN u.id IS NOT NULL THEN CASE r.undo WHEN 'UpdateResidentValue' THEN 'resident_offset_inference' ELSE 'attribute_structure' END END,
              CASE WHEN v.id IS NOT NULL THEN CASE r.redo WHEN 'UpdateResidentValue' THEN 'resident_offset_inference' ELSE 'attribute_structure' END END,
              CASE WHEN r.redo='Checkpoint' OR r.redo LIKE 'RecordType %' THEN r.redo ELSE 'Update' END,
              {NamesExpression(filter == 3 && ExactLsn(search) == null)}
            FROM {rowSource} LEFT JOIN tx_times u ON u.id=r.id AND u.side='undo'
             LEFT JOIN tx_times v ON v.id=r.id AND v.side='redo'
             LEFT JOIN tx_provenance p ON p.id=r.id
            WHERE {(order == null ? Condition(db, search, filter, records, token) : "1")} ORDER BY {(order == null ? "r.lsn" : "k.key")} {direction}
            """;
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$sha", sha);
        long count = 0;
        try
        {
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var csv = new StreamWriter(f, new UTF8Encoding(true)))
            using (var r = cmd.ExecuteReader())
            {
                csv.WriteLine("LSN_hex,previous_LSN_hex,undo_next_LSN_hex,source_offset_bytes,transaction_id,redo_operation,undo_operation,redo_declared_bytes,undo_declared_bytes,undo_names,redo_names,created_undo_unix_100ns,created_redo_unix_100ns,modified_undo_unix_100ns,modified_redo_unix_100ns,changed_undo_unix_100ns,changed_redo_unix_100ns,accessed_undo_unix_100ns,accessed_redo_unix_100ns,move_candidate_names,move_candidate_LSN_at_source_offset,page_provenance,source_path,source_SHA256,redo_recovered_bytes,undo_recovered_bytes,undo_timestamp_basis,redo_timestamp_basis,decoded_record_kind,resolved_paths,path_source,path_evidence,mft_source,mft_SHA256,name_reference_links_json");
                while (r.Read())
                {
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < r.FieldCount - 1; i++)
                    {
                        if (i > 0)
                            csv.Write(',');
                        csv.Write(Csv(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? ""));
                    }
                    var links = ReadNames(r.GetString(r.FieldCount - 1));
                    if (links.Count == 0)
                        csv.Write(",,,,,,");
                    else
                    {
                        var displayed = DescribePaths(links);
                        string rawLinks = JsonSerializer.Serialize(links.Select(group => group.Select(link => new
                        {
                            name = link.Name,
                            file_le = link.File == null ? null : Convert.ToHexString(link.File),
                            parent_le = link.Parent == null ? null : Convert.ToHexString(link.Parent)
                        })));
                        string[] evidence = [displayed.Source == "—" ? "" : displayed.Paths, displayed.Source == "—" ? "" : displayed.Source,
                            displayed.Evidence, displayed.UsesMft ? mft?.SourcePath ?? "" : "", displayed.UsesMft ? mft?.Hash ?? "" : "", rawLinks];
                        foreach (string value in evidence)
                        {
                            csv.Write(',');
                            csv.Write(Csv(value));
                        }
                    }
                    csv.WriteLine();
                    count++;
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, destination);
            return count;
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

    internal TxDetail Inspect(TxRow row, string original)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.CommandText = """
            SELECT pos,prev,undo_next,redo,undo,redo_len,undo_len,redodata,undodata,lcns,mft_slot,p.kind
            FROM tx_records r LEFT JOIN tx_provenance p ON p.id=r.id WHERE r.id=$id
            """;
        var facts = new List<TxFact>();
        void Note(string text) => facts.Add(new("", text));
        void Fact(string label, string value) => facts.Add(new(label, value));
        TxPayload redo, undo;
        long position;
        string previousLsn, nextUndoLsn;
        string? nonUpdate;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read())
                throw new InvalidDataException(L.T("선택한 로그 기록을 찾지 못했습니다.", "The selected log record was not found."));
            position = r.GetInt64(0);
            if (!r.IsDBNull(11))
                Note(L.T("이전 순환에서 기록한 데이터가 현재 페이지의 빈 공간에 남아 있습니다. 이 레코드는 이동이나 이름 변경을 연결하는 근거에서 제외했습니다.", "Data from an earlier cycle remains in unused space on the current page. This record was excluded from move and rename correlation."));
            previousLsn = Lsn(r.GetString(1));
            nextUndoLsn = Lsn(r.GetString(2));
            var decodedType = r.GetString(3);
            nonUpdate = decodedType == "Checkpoint" || decodedType.StartsWith("RecordType ", StringComparison.Ordinal) ? decodedType : null;
            if (!r.IsDBNull(10))
                Fact(L.T("대상 MFT 레코드 번호", "Target MFT record number"), L.T("{0}  (재사용 순번 미포함)", "{0}  (sequence number unavailable)", r.GetInt64(10)));
            if (!r.IsDBNull(9) && r.GetString(9).Length != 0)
                Fact(L.T("대상 디스크 클러스터 (LCN)", "Target disk clusters (LCN)"), r.GetString(9));
            TxPayload ReadPayload(int op, int len, int data) => new(
                nonUpdate is "Checkpoint" or "RecordType 2" ? L.T("해당 없음", "Not applicable") :
                    nonUpdate != null || r.IsDBNull(op) ? L.T("작업 미판독", "Not decoded") : r.GetString(op),
                r.IsDBNull(len) ? null : r.GetInt64(len),
                r.IsDBNull(data) ? [] : (byte[])r[data]);
            redo = ReadPayload(3, 5, 7);
            undo = ReadPayload(4, 6, 8);
        }
        cmd.CommandText = "SELECT side,name FROM tx_names WHERE id=$id ORDER BY side,name";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                Fact(L.T("{0}에 기록된 파일 이름", "File name in {0}", r.GetString(0) == "redo" ? "Redo" : "Undo"), r.GetString(1));
        cmd.CommandText = """
            SELECT m.old_name,m.new_name,m.old_parent=m.new_parent,o.lsn,o.pos,n.lsn,n.pos
            FROM tx_moves m JOIN tx_records o ON o.id=m.old_id JOIN tx_records n ON n.id=m.new_id
            WHERE m.old_id=$id OR m.new_id=$id
            """;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                bool same = r.GetBoolean(2);
                Note(same ? L.T("같은 폴더에서 이름 변경 후보", "Possible rename within the same folder") : L.T("다른 폴더로 이동한 후보", "Possible move to another folder"));
                Fact(L.T("삭제된 폴더 항목", "Removed directory entry"), r.GetString(0));
                Fact(L.T("LSN · 원본 위치", "LSN · Source offset"), $"0x{r.GetString(3)}   ·   0x{r.GetInt64(4):X}");
                Fact(L.T("추가된 폴더 항목", "Added directory entry"), r.GetString(1));
                Fact(L.T("LSN · 원본 위치", "LSN · Source offset"), $"0x{r.GetString(5)}   ·   0x{r.GetInt64(6):X}");
                Fact(L.T("상위 폴더", "Parent folders"), L.T("{0} (폴더의 레코드 번호와 재사용 순번을 함께 비교)", "{0} (compared using record and sequence numbers)", (same ? L.T("동일", "Same") : L.T("서로 다름", "Different"))));
                Note(L.T("이전 LSN으로 연결된 작업에서 같은 파일을 가리키는 삭제·추가 항목이 한 쌍씩 남아 있습니다. 같은 파일을 가리키는 다른 이름(하드 링크)을 삭제·추가한 경우도 이처럼 보일 수 있습니다.", "Operations linked by the previous LSN contain one removed entry and one added entry for the same file. Removing and adding different hard links to one file can produce the same pattern."));
            }
        }
        cmd.CommandText = "SELECT side,created,modified,changed,accessed FROM tx_times WHERE id=$id ORDER BY side DESC";
        List<(string Side, long?[] Times)> stamps = [];
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                stamps.Add((r.GetString(0), Enumerable.Range(1, 4).Select(i => r.IsDBNull(i) ? (long?)null : r.GetInt64(i)).ToArray()));
        if (stamps.Count != 0)
        {
            string timesLabel = L.T("파일 속성 시각 ($STANDARD_INFORMATION)  ·  KST (UTC+09:00)", "File attribute timestamps ($STANDARD_INFORMATION)  ·  KST (UTC+09:00)");
            facts.Add(new("", timesLabel, zone => timesLabel.Replace("KST (UTC+09:00)", zone.Caption, StringComparison.Ordinal)));
            var inferred = stamps.Where(s => (s.Side == "redo" ? redo : undo).Operation == "UpdateResidentValue")
                .Select(s => s.Side == "redo" ? "Redo" : "Undo").Distinct().ToArray();
            if (inferred.Length > 0)
                Note(L.T("{0}의 속성 종류는 레코드 안의 위치와 시각 값으로 추정했습니다.", "The attribute type in {0} is inferred from its position and timestamp values.", string.Join(" / ", inferred)));
            Note(L.T("이 로그 작업의 발생 시각은 기록되지 않았습니다.", "The time of the log operation is not recorded."));
            var before = stamps.FirstOrDefault(x => x.Side == "undo").Times;
            var after = stamps.FirstOrDefault(x => x.Side == "redo").Times;
            string[] labels = [L.T("만든 시각", "Created"), L.T("내용 수정 시각", "Content modified"), L.T("메타데이터 변경 시각", "Metadata changed"), L.T("마지막 접근 시각", "Last accessed")];
            for (int i = 0; i < labels.Length; i++)
            {
                long? old = before?[i], newer = after?[i];
                if (old == null && newer == null)
                    continue;
                Note($"{labels[i]}{(newer < old ? L.T("   ·   Redo 값이 더 이릅니다.", "   ·   Redo is earlier than Undo.") : "")}");
                facts.Add(new(L.T("Undo에 기록된 값", "Value in Undo"), "", zone => Stamp(old, zone)));
                facts.Add(new(L.T("Redo에 기록된 값", "Value in Redo"), "", zone => Stamp(newer, zone)));
            }
        }
        using var file = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
        file.Position = position;
        byte[] header = new byte[48];
        file.ReadExactly(header);
        return new(row, facts, position, header, redo, undo, previousLsn, nextUndoLsn, nonUpdate);
    }

    private static string Stamp(long? value, JournalTimeZone utc)
    {
        if (!value.HasValue)
            return L.T("데이터에서 확인되지 않음", "Unavailable in this data");
        try
        {
            var time = utc.At(DateTimeOffset.UnixEpoch.AddTicks(value.Value));
            return time.ToString(utc.WindowsZone == null ? "yyyy-MM-dd HH:mm:ss.fffffff" : "yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture)
                + L.T("  (1970년 기준 100ns 단위 값 {0})", "  (100 ns units since 1970: {0})", value);
        }
        catch (ArgumentOutOfRangeException) { return L.T("표시 범위 밖 (1970년 기준 100ns 단위 값 {0})", "Out of display range (100 ns units since 1970: {0})", value); }
    }

    private static string Lsn(string value) => value == "0000000000000000" ? L.T("연결 없음", "No link") : "0x" + value;
    private static string Label(string op) => op switch
    {
        "SetNewAttributeSizes" => L.T("속성 크기 변경", "Change attribute sizes"),
        "UpdateResidentValue" => L.T("MFT 내부 속성값 갱신", "Update resident attribute value"),
        "UpdateNonresidentValue" => L.T("MFT 외부 속성값 갱신", "Update nonresident attribute value"),
        "ForgetTransaction" => L.T("트랜잭션 추적 종료", "Forget transaction"),
        "InitializeFileRecordSegment" => L.T("파일 레코드 초기화", "Initialize file record"),
        "DeallocateFileRecordSegment" => L.T("파일 레코드 할당 해제", "Deallocate file record"),
        "CreateAttribute" => L.T("속성 만들기", "Create attribute"),
        "DeleteAttribute" => L.T("속성 삭제", "Delete attribute"),
        "Checkpoint" => L.T("검사점 기록", "Checkpoint"),
        "AddIndexEntryAllocation" or "AddIndexEntryRoot" => L.T("인덱스 항목 추가", "Add index entry"),
        "DeleteIndexEntryAllocation" or "DeleteIndexEntryRoot" => L.T("인덱스 항목 삭제", "Delete index entry"),
        "UpdateFileNameAllocation" or "UpdateFileNameRoot" => L.T("인덱스 파일명 정보 갱신", "Update indexed file name"),
        "ClearBitsInNonresidentBitMap" => L.T("비트맵 할당 해제", "Clear bitmap bits"),
        "SetBitsInNonresidentBitMap" => L.T("비트맵 할당 표시", "Set bitmap bits"),
        "Noop" => L.T("수행할 작업 없음", "No operation"),
        "UpdateMappingPairs" => L.T("디스크 클러스터 매핑 변경", "Update disk cluster mapping"),
        "ZeroEndOfFileRecord" => L.T("파일 레코드 끝 지우기", "Clear end of file record"),
        "OpenNonresidentAttribute" => L.T("MFT 외부 속성 열기", "Open nonresident attribute"),
        "WriteEndOfIndexBuffer" => L.T("인덱스 버퍼 끝 쓰기", "Write end of index buffer"),
        "CompensationLogRecord" => L.T("보상 로그 기록", "Compensation log record"),
        "SetIndexEntryVcnRoot" or "SetIndexEntryVcnAllocation" => L.T("인덱스 클러스터 번호 설정", "Set index cluster number"),
        "DirtyPageTableDump" => L.T("변경된 페이지 표 기록", "Write dirty page table"),
        "AttributeNamesDump" => L.T("속성 이름 목록 기록", "Write attribute names"),
        "OpenAttributeTableDump" => L.T("열린 속성 표 기록", "Write open attribute table"),
        "DeleteDirtyClusters" => L.T("변경된 클러스터 목록 정리", "Delete dirty clusters"),
        _ => op
    };
}
