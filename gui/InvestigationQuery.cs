using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace JournalDesk;

public enum LeadKind
{
    DocumentDelete, ClockBack, RenameDelete, ToolName
}

public sealed record LeadRule(LeadKind Kind, string LabelKey, string CriterionKey)
{
    public static readonly LeadRule[] All = [
        new(LeadKind.DocumentDelete, "LeadDocumentDeleted", "LeadDocumentDeletedRule"),
        new(LeadKind.ClockBack, "LeadTimeOutOfOrder", "LeadTimeOutOfOrderRule"),
        new(LeadKind.RenameDelete, "LeadRenamedThenDeleted", "LeadRenamedThenDeletedRule"),
        new(LeadKind.ToolName, "LeadSDeleteRecords", "LeadSDeleteNameRule")
    ];

    public string Label => L.Caption(LabelKey);
    public string Criterion => Kind == LeadKind.DocumentDelete
        ? string.Format(CultureInfo.CurrentCulture, L.Caption(CriterionKey), InvestigationQuery.DocumentExtensions)
        : L.Caption(CriterionKey);
}

public sealed record LeadObservation(long Position, long Usn, long? Stamp, long Reason, byte[] FileId, byte[] ParentId,
    string Name, long Segment)
{
    public string Time => InZone(DisplayTimeZone.Current.Zone);
    internal string InZone(JournalTimeZone utc) => Stamp is long stamp ? GlobalLogFilter.TimeText(stamp, utc, "yyyy-MM-dd HH:mm:ss.fffffff") : L.T("시각 없음", "No timestamp");
    public string Offset => $"0x{Position:X}";
    public string ReasonText => $"0x{Reason:X8}";
    public string DisplayName => Name.Length == 0 ? L.T("이름 기록 없음", "No name recorded") : Name;
    public string ReasonNames => Entry.FormatFlags(Reason, closeFirst: false);
    public string FileText => Journal.Ref(FileId);
}

public sealed record LeadEvidence(string RoleKey, LeadObservation Record)
{
    internal const string BasicInfoRole = "LeadRoleBasicInfoChanged";
    internal const string PrefetchRole = "LeadRolePrefetch";
    public string Role => RoleKey switch
    {
        BasicInfoRole => L.T("기본 정보 변경", "Basic information changed"),
        PrefetchRole => L.T("실행 관련 흔적", "Execution-related trace"),
        _ => L.Caption(RoleKey)
    };
}
public enum LeadSortField
{
    Number, Time, Name, Observation
}
public readonly record struct LeadOrder(LeadSortField Field, bool Descending = false)
{
    internal string Key => Field switch
    {
        LeadSortField.Number => "number",
        LeadSortField.Time => "stamp",
        LeadSortField.Name => "name",
        LeadSortField.Observation => "observation",
        _ => throw new ArgumentOutOfRangeException()
    };
    internal string Sql => Key + (Descending ? " DESC" : " ASC") + (Field == LeadSortField.Number ? "" : ", number ASC");
}

public sealed record InvestigationLead(long Number, LeadKind Kind, IReadOnlyList<LeadEvidence> Evidence)
{
    public LeadObservation Anchor => Evidence[^1].Record;
    public string Detail => Kind switch
    {
        LeadKind.ClockBack => L.T("앞 기록보다 {0:N1}초 이릅니다.", "{0:N1} seconds earlier than the previous record.", TimeSpan.FromTicks(Evidence[0].Record.Stamp!.Value - Anchor.Stamp!.Value).TotalSeconds),
        LeadKind.RenameDelete => RenameDetail(),
        LeadKind.DocumentDelete => Path.GetExtension(Anchor.Name),
        LeadKind.ToolName => ToolDetail(),
        _ => Anchor.Name
    };
    private string ToolDetail()
    {
        if (Evidence[0].RoleKey != LeadEvidence.PrefetchRole)
            return L.T("SDelete 실행 파일 이름과 일치합니다.", "The name matches an SDelete executable.");
        return (Anchor.Reason & InvestigationQuery.FileCreated) != 0
            ? L.T("SDelete 이름이 붙은 .pf 파일의 생성 기록입니다.", "A Prefetch file named for SDelete was created.")
            : L.T("SDelete 이름이 붙은 .pf 파일의 내용이 바뀌었습니다.", "A Prefetch file named for SDelete was updated.");
    }

    private string RenameDetail()
    {
        string sequence = RenameSequence(Evidence[0].Record, Evidence[1].Record, Anchor);
        int changes = Evidence.Count(item => item.RoleKey == LeadEvidence.BasicInfoRole);
        return changes == 0
            ? L.T("{0} → 삭제", "{0} → deleted", sequence)
            : L.T("{0} → 삭제 · 기본 정보 변경 {1:N0}건", "{0} → deleted · {1:N0} basic information changes", sequence, changes);
    }

    internal static string RenameSequence(LeadObservation first, LeadObservation next, LeadObservation last) =>
        string.Join(" → ", first.Name, next.Name, last.Name);
}

// Keep sortable keys and original offsets in a temporary database. Evidence is read from the journal on demand.
public sealed class InvestigationQuery : IDisposable
{
    public const string DocumentExtensions = ".pdf, .hwp, .hwpx, .doc, .docx, .xls, .xlsx, .ppt, .pptx, .odt, .ods, .odp, .rtf, .txt, .csv";
    private static readonly HashSet<string> Documents = new(DocumentExtensions.Split(", "), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Tools = new(["sdelete.exe", "sdelete64.exe", "sdelete64a.exe"], StringComparer.OrdinalIgnoreCase);
    private static readonly string[][] Roles = [
        ["LeadRoleDeleted"], ["LeadRolePrevious", "LeadRoleEarlierTime"],
        ["LeadRoleFirstBefore", "LeadRoleFirstAfter", "LeadRoleSecondBefore", "LeadRoleSecondAfter", "LeadRoleDeleted"], ["LeadRoleNameMatch"]
    ];
    private const int RenameWindow = 512;
    private const long BasicInfoChange = 0x00008000;
    internal const long FileCreated = 0x00000100;
    private const long DataChanged = 0x00000001 | 0x00000002 | 0x00000004;
    private const string Columns = "pos,usn,stamp,reason,file,parent,name,segment,attr";
    private readonly string dbPath;
    private readonly SqliteConnection candidates = new("Data Source=;Pooling=False");
    private readonly long[] counts = new long[Roles.Length];
    public string Hash { get; private set; } = "";
    public long Total
    {
        get; private set;
    }

    private InvestigationQuery(string dbPath)
    {
        this.dbPath = dbPath;
        try
        {
            candidates.Open();
            candidates.CreateCollation("lead_text", StringComparer.OrdinalIgnoreCase.Compare);
            using var cmd = candidates.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE leads (
                    kind INTEGER NOT NULL, number INTEGER NOT NULL, stamp INTEGER,
                    name TEXT COLLATE lead_text NOT NULL, observation COLLATE lead_text NOT NULL,
                    positions BLOB NOT NULL, PRIMARY KEY(kind,number)
                ) WITHOUT ROWID
                """;
            cmd.ExecuteNonQuery();
        }
        catch { Dispose(); throw; }
    }

    public void Dispose() => candidates.Dispose();
    public long Count(LeadKind kind) => counts[(int)kind];

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    public static InvestigationQuery Scan(Journal journal, CancellationToken token, IProgress<long>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var result = new InvestigationQuery(journal.DbPath);
        try
        {
            result.Analyze(token, progress);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static LeadObservation Row(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2),
        r.GetInt64(3), (byte[])r[4], (byte[])r[5], r.IsDBNull(6) ? "" : r.GetString(6), r.GetInt64(7));

    private void Save(SqliteCommand insert, LeadKind kind, params ReadOnlySpan<LeadObservation> records)
    {
        var anchor = records[^1];
        var bytes = new byte[records.Length * sizeof(long)];
        for (int i = 0; i < records.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * sizeof(long)), records[i].Position);
        insert.Parameters["$kind"].Value = (int)kind;
        insert.Parameters["$number"].Value = ++counts[(int)kind];
        insert.Parameters["$stamp"].Value = (object?)anchor.Stamp ?? DBNull.Value;
        insert.Parameters["$name"].Value = anchor.DisplayName;
        // Store time differences as ticks, including precision beyond the display's rounded seconds.
        insert.Parameters["$observation"].Value = kind switch
        {
            LeadKind.ClockBack => records[0].Stamp!.Value - anchor.Stamp!.Value,
            LeadKind.RenameDelete => InvestigationLead.RenameSequence(records[0], records[1], records[3]),
            LeadKind.DocumentDelete => Path.GetExtension(anchor.Name),
            LeadKind.ToolName => IsPrefetchTrace(anchor) ? (anchor.Reason & FileCreated) != 0 ? 1 : 2 : 0,
            _ => anchor.Name
        };
        insert.Parameters["$positions"].Value = bytes;
        insert.ExecuteNonQuery();
    }

    private void Analyze(CancellationToken token, IProgress<long>? progress)
    {
        using var db = Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT hash,records FROM summary";
        using (var meta = query.ExecuteReader())
        {
            if (!meta.Read())
                throw new InvalidDataException(L.T("저널 색인의 원본 정보를 읽을 수 없습니다.", "Could not read source details from the journal index."));
            Hash = meta.GetString(0);
            Total = meta.GetInt64(1);
        }
        query.CommandText = $"SELECT {Columns} FROM records ORDER BY pos";
        using var rows = query.ExecuteReader();
        using var transaction = candidates.BeginTransaction();
        using var insert = candidates.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO leads VALUES ($kind,$number,$stamp,$name,$observation,$positions)";
        foreach (var key in new[] { "$kind", "$number", "$stamp", "$name", "$observation", "$positions" })
            insert.Parameters.Add(new SqliteParameter { ParameterName = key });
        long ordinal = 0;
        LeadObservation? previous = null;
        var tracks = new Dictionary<string, RenameTrack>(StringComparer.Ordinal);
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            var entry = Row(rows);
            if (previous != null)
            {
                if (previous.Segment != entry.Segment || previous.Usn >= entry.Usn)
                    tracks.Clear();
                else if (ValidStamp(previous.Stamp) && ValidStamp(entry.Stamp) &&
                    previous.Stamp!.Value - entry.Stamp!.Value >= TimeSpan.TicksPerMinute)
                    Save(insert, LeadKind.ClockBack, previous, entry);
            }
            previous = entry;
            if (++ordinal % RenameWindow == 0)
            {
                foreach (var key in tracks.Where(p => ordinal - p.Value.Last >= RenameWindow).Select(p => p.Key).ToArray())
                    tracks.Remove(key);
                if (ordinal % 16384 == 0)
                    progress?.Report(ordinal);
            }
            if (rows.IsDBNull(8) || (rows.GetInt64(8) & 16) != 0)
                continue;
            bool deleted = (entry.Reason & 0x200) != 0;
            if (deleted && Documents.Contains(Path.GetExtension(entry.Name)))
                Save(insert, LeadKind.DocumentDelete, entry);
            if (Tools.Contains(entry.Name) || IsPrefetchTrace(entry))
                Save(insert, LeadKind.ToolName, entry);
            TrackRename(insert, entry, ordinal, deleted, tracks);
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static bool IsPrefetchTrace(LeadObservation entry)
    {
        string name = entry.Name;
        const int hashLength = 8;
        const string extension = ".pf";
        int separator = name.Length - extension.Length - hashLength - 1;
        return (entry.Reason & (FileCreated | DataChanged)) != 0
            && separator > 0 && name[separator] == '-'
            && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            && Tools.Contains(name[..separator])
            && uint.TryParse(name.AsSpan(separator + 1, hashLength), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
    }

    private static bool ValidStamp(long? value) => value is > 0 and <= 2650467743999999999L;
    private sealed record Mark(LeadObservation Entry, long Ordinal);
    private sealed record RenamePair(Mark Before, LeadObservation After);
    private sealed class RenameTrack
    {
        public long Last;
        public Mark? Pending;
        public RenamePair? First, Second;
    }

    private static bool SameLink(LeadObservation a, LeadObservation b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal) && a.ParentId.AsSpan().SequenceEqual(b.ParentId);

    private void TrackRename(SqliteCommand insert, LeadObservation entry, long ordinal, bool deleted, Dictionary<string, RenameTrack> tracks)
    {
        if (entry.Reason != 0x1000 && tracks.Count == 0)
            return;
        var key = Convert.ToHexString(entry.FileId);
        tracks.TryGetValue(key, out var track);
        if (track != null && (ordinal - track.Last >= RenameWindow || (entry.Reason & 0x100) != 0))
        {
            tracks.Remove(key);
            track = null;
        }
        if (entry.Reason == 0x1000)
        {
            if (track == null)
                tracks[key] = track = new();
            track.Pending = new(entry, ordinal);
            track.Last = ordinal;
            return;
        }
        if (track == null)
            return;
        var pending = track.Pending;
        track.Pending = null;
        if (entry.Reason == 0x2000 && pending != null && pending.Entry.Name != entry.Name &&
            ValidStamp(entry.Stamp) && pending.Entry.Stamp == entry.Stamp)
        {
            track.First = track.Second != null && SameLink(track.Second.After, pending.Entry) ? track.Second : null;
            track.Second = new(pending, entry);
            track.Last = ordinal;
        }
        else if (deleted)
        {
            if (track.First != null && track.Second != null && SameLink(track.Second.After, entry) &&
                ordinal - track.First.Before.Ordinal < RenameWindow)
                Save(insert, LeadKind.RenameDelete, track.First.Before.Entry, track.First.After,
                    track.Second.Before.Entry, track.Second.After, entry);
            tracks.Remove(key);
        }
        else if ((entry.Reason & (0x1000 | 0x2000)) != 0 && (entry.Reason & 0x80000000) == 0)
        {
            tracks.Remove(key);
        }
    }

    private void PrepareOrder(LeadOrder order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SQLitePCL.raw.sqlite3_progress_handler(candidates.Handle, 4096, static t => ((CancellationToken)t).IsCancellationRequested ? 1 : 0, token);
        if (order.Field == LeadSortField.Number)
            return;
        using var cmd = candidates.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS leads_{order.Key}_{(order.Descending ? "desc" : "asc")} ON leads(kind,{order.Sql})";
        cmd.ExecuteNonQuery();
    }

    public long Locate(LeadKind kind, long number, LeadOrder order, CancellationToken token)
    {
        if (order.Field == LeadSortField.Number)
            return order.Descending ? Count(kind) - number : number - 1;
        PrepareOrder(order, token);
        using var cmd = candidates.CreateCommand();
        cmd.CommandText = $"SELECT ordinal FROM (SELECT number,row_number() OVER (ORDER BY {order.Sql})-1 AS ordinal FROM leads WHERE kind=$kind) WHERE number=$number";
        cmd.Parameters.AddWithValue("$kind", (int)kind);
        cmd.Parameters.AddWithValue("$number", number);
        return (long)cmd.ExecuteScalar()!;
    }

    private IEnumerable<InvestigationLead> Read(LeadKind kind, long start, long length, LeadOrder order, CancellationToken token)
    {
        PrepareOrder(order, token);
        using var page = candidates.CreateCommand();
        page.CommandText = $"SELECT number,positions FROM leads WHERE kind=$kind ORDER BY {order.Sql} LIMIT $length OFFSET $start";
        page.Parameters.AddWithValue("$kind", (int)kind);
        page.Parameters.AddWithValue("$start", start);
        page.Parameters.AddWithValue("$length", length);
        using var found = page.ExecuteReader();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM records WHERE pos=$pos";
        var param = cmd.Parameters.Add("$pos", SqliteType.Integer);
        var roles = Roles[(int)kind];
        while (found.Read())
        {
            token.ThrowIfCancellationRequested();
            var bytes = (byte[])found[1];
            var evidence = new LeadEvidence[roles.Length];
            for (int j = 0; j < roles.Length; j++)
            {
                param.Value = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(j * sizeof(long)));
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                    throw new InvalidDataException(L.T("색인에서 근거 레코드를 찾을 수 없습니다.", "The evidence record was not found in the index."));
                var entry = Row(r);
                string role = kind == LeadKind.ToolName && IsPrefetchTrace(entry) ? LeadEvidence.PrefetchRole : roles[j];
                evidence[j] = new(role, entry);
            }
            yield return new(found.GetInt64(0), kind, kind == LeadKind.RenameDelete
                ? WithBasicInfoChanges(db, evidence, token)
                : evidence);
        }
    }

    private static IReadOnlyList<LeadEvidence> WithBasicInfoChanges(SqliteConnection db, LeadEvidence[] evidence, CancellationToken token)
    {
        var firstNewName = evidence[1].Record;
        var deletion = evidence[^1].Record;
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM records
            WHERE file=$file AND usn>$firstUsn AND usn<$lastUsn AND pos>$firstPos AND pos<$lastPos
              AND segment=$segment AND (reason & $basicInfo) != 0
            ORDER BY pos
            """;
        cmd.Parameters.AddWithValue("$file", deletion.FileId);
        cmd.Parameters.AddWithValue("$firstUsn", firstNewName.Usn);
        cmd.Parameters.AddWithValue("$lastUsn", deletion.Usn);
        cmd.Parameters.AddWithValue("$firstPos", firstNewName.Position);
        cmd.Parameters.AddWithValue("$lastPos", deletion.Position);
        cmd.Parameters.AddWithValue("$segment", deletion.Segment);
        cmd.Parameters.AddWithValue("$basicInfo", BasicInfoChange);
        using var reader = cmd.ExecuteReader();
        List<LeadEvidence>? combined = null;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            combined ??= [.. evidence];
            combined.Add(new(LeadEvidence.BasicInfoRole, Row(reader)));
        }
        if (combined == null)
            return evidence;
        combined.Sort(static (a, b) => a.Record.Position.CompareTo(b.Record.Position));
        return combined;
    }

    public List<InvestigationLead> Page(LeadKind kind, long start, int length, CancellationToken token, LeadOrder order = default) =>
        Read(kind, start, length, order, token).ToList();

    public void Export(LeadRule rule, string source, string destination, CancellationToken token, LeadOrder order = default)
    {
        var dest = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(dest), ".csv", StringComparison.OrdinalIgnoreCase) || File.Exists(dest))
            throw new IOException(L.T("저장하려면 새로운 CSV 파일 이름을 지정해 주세요.", "Choose a new CSV file name."));
        string temp = Path.Combine(Path.GetDirectoryName(dest)!, $".ntfstracer-{Guid.NewGuid():N}.partial");
        try
        {
            using (var writer = new StreamWriter(new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(true)))
            {
                JournalTimeZone utc = DisplayTimeZone.Current.Zone;
                writer.WriteLine(L.T("후보 번호,유형,판정 기준,관찰 내용,근거 역할,기록 당시 이름,시각(KST),원본 FILETIME,USN,파일 참조(원본 바이트),상위 폴더 참조(원본 바이트),사유 비트,추출 파일 오프셋,연속 구간,원본 파일,원본 SHA-256", "Candidate number,Type,Rule,Observation,Evidence role,Recorded name,Time (KST),Raw FILETIME,USN,File reference (raw bytes),Parent reference (raw bytes),Reason bits,Extraction offset,Continuous segment,Source file,Source SHA-256").Replace("KST", utc.Code, StringComparison.Ordinal));
                foreach (var lead in Read(rule.Kind, 0, Count(rule.Kind), order, token))
                {
                    foreach (var evidence in lead.Evidence)
                    {
                        var r = evidence.Record;
                        string[] values = [Num(lead.Number), rule.Label, rule.Criterion, lead.Detail, evidence.Role, r.Name,
                            r.Stamp is long stamp ? GlobalLogFilter.PreciseTime(stamp, utc) : r.InZone(utc), Num(r.Stamp), Num(r.Usn),
                            Convert.ToHexString(r.FileId), Convert.ToHexString(r.ParentId), r.ReasonText, Num(r.Position), Num(r.Segment), source, Hash];
                        writer.WriteLine(string.Join(",", values.Select(s => "\"" + s.Replace("\"", "\"\"") + "\"")));
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, dest);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Num(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}

