using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using System.Windows.Media;

namespace JournalDesk;

public sealed record FileRow(byte[] Id, string Name, long Attributes, int Count, string? FolderHint)
{
    public Entry? LastNamedRecord
    {
        get; init;
    }
    public string? Disambiguator
    {
        get; set;
    }
    public bool Folder => (Attributes & 16) != 0;
    public ImageSource? Icon => LastNamedRecord?.Icon;
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
    public string Subtitle => (FolderHint ?? (Folder ? L.T("폴더", "Folder") : L.T("폴더 이름 미상", "Unknown folder"))) + L.T("   ·   기록 {0:N0}건", "   ·   {0:N0} records", Count);
    public override string ToString() => Name + " · " + Subtitle;
}
public sealed record Entry(long Position, int Size, int Version, byte[] Parent, long Usn,
    long? Timestamp, long Reason, long Source, long? Security, long? Attributes, string Name,
    int Minor, bool Resynced, bool Escaped, long? Remaining, string Extents, long Segment)
{
    public int? ExtentStride
    {
        get; init;
    }
    internal static int? ReadExtentStride(SqliteDataReader reader, int firstExtraColumn)
    {
        // Some older indexes only stored the remaining count in range_records.
        for (int i = firstExtraColumn; i < reader.FieldCount; i++)
            if (reader.GetName(i) == "stride" && !reader.IsDBNull(i))
                return reader.GetInt32(i);
        return null;
    }
    private static readonly (long Mask, Func<string> Label, string Native)[] Flags = [
        (0x2000,static () => L.T("새 이름 기록", "New name record"),"RENAME_NEW_NAME"),(0x1000,static () => L.T("이전 이름 기록", "Old name record"),"RENAME_OLD_NAME"),(0x200,static () => L.T("삭제 기록", "Deletion record"),"FILE_DELETE"),(0x100,static () => L.T("생성 기록", "Creation record"),"FILE_CREATE"),
        (1,static () => L.T("내용 덮어쓰기", "Data overwritten"),"DATA_OVERWRITE"),(2,static () => L.T("데이터 길이 증가", "Data extended"),"DATA_EXTEND"),(4,static () => L.T("데이터 길이 감소", "Data truncated"),"DATA_TRUNCATION"),
        (0x10,static () => L.T("ADS 덮어쓰기", "ADS overwritten"),"NAMED_DATA_OVERWRITE"),(0x20,static () => L.T("ADS 길이 증가", "ADS extended"),"NAMED_DATA_EXTEND"),(0x40,static () => L.T("ADS 길이 감소", "ADS truncated"),"NAMED_DATA_TRUNCATION"),
        (0x8000,static () => L.T("속성 또는 시각 변경", "Attributes or timestamps changed"),"BASIC_INFO_CHANGE"),(0x400,static () => L.T("확장 속성 변경", "Extended attributes changed"),"EA_CHANGE"),(0x800,static () => L.T("접근 권한 변경", "Permissions changed"),"SECURITY_CHANGE"),
        (0x4000,static () => L.T("내용 색인 설정 변경", "Content indexing changed"),"INDEXABLE_CHANGE"),(0x10000,static () => L.T("하드 링크 변경", "Hard link changed"),"HARD_LINK_CHANGE"),(0x20000,static () => L.T("압축 상태 변경", "Compression changed"),"COMPRESSION_CHANGE"),
        (0x40000,static () => L.T("암호화 상태 변경", "Encryption changed"),"ENCRYPTION_CHANGE"),(0x80000,static () => L.T("Object ID 변경", "Object ID changed"),"OBJECT_ID_CHANGE"),(0x100000,static () => L.T("Reparse Point 변경", "Reparse point changed"),"REPARSE_POINT_CHANGE"),
        (0x200000,static () => L.T("이름 있는 스트림 구성 변경", "Named streams changed"),"STREAM_CHANGE"),(0x400000,static () => L.T("TxF를 통한 스트림 변경", "Stream changed by TxF"),"TRANSACTED_CHANGE"),(0x800000,static () => L.T("스트림 무결성 설정 변경", "Stream integrity settings changed"),"INTEGRITY_CHANGE"),
        (0x1000000,static () => L.T("저장소 등급 변경", "Storage class changed"),"DESIRED_STORAGE_CLASS_CHANGE"),(0x80000000,static () => L.T("파일 닫기", "File closed"),"CLOSE")];
    internal static IReadOnlyList<(long Mask, string Label, string Native)> ReasonOptions => Flags.Select(f => (f.Mask, f.Label(), f.Native)).ToArray();
    private static readonly long KnownReasonMask = Flags.Aggregate(0L, (mask, flag) => mask | flag.Mask);
    public string NativeReasons => DescribeReasons(Reason);
    internal static string DescribeReasons(long reason)
    {
        var names = Flags.Where(flag => (reason & flag.Mask) != 0).Select(flag => "USN_REASON_" + flag.Native);
        long unknown = reason & ~KnownReasonMask;
        if (unknown != 0)
            names = names.Append(L.T("미확인 비트 0x{0:X8}", "Unknown bits 0x{0:X8}", unknown));
        return reason == 0 ? L.T("사유 비트 없음", "No reason bits") : string.Join(" | ", names);
    }
    public string RawReason => $"{NativeReasons}  (0x{Reason:X8})";
    public string RecordedName => string.IsNullOrEmpty(Name) ? L.T("이름이 기록되지 않음", "No name recorded") : Name;
    public ImageSource? Icon => Name.Length > 0 && Attributes is long attributes
        ? ShellIcons.For(Name, (attributes & 16) != 0) : null;
    public string ExtentSummary => L.T("변경 범위 (시작 + 길이): {0}\n남은 범위 수: {1}",
        "Changed extents (start + length): {0}\nExtents remaining: {1}",
        string.IsNullOrEmpty(Extents) ? L.T("기록된 범위 없음", "No ranges recorded") : Extents,
        Remaining?.ToString(CultureInfo.InvariantCulture) ?? L.T("확인 불가", "Unavailable"));
    public string RawLocation => L.T("원본 위치 0x{0:X}  ·  {1:N0}바이트", "Source offset 0x{0:X}  ·  {1:N0} bytes", Position, Size);
    public string Title
    {
        get
        {
            var flag = Flags.FirstOrDefault(f => (Reason & f.Mask) != 0);
            return flag.Mask == 0 ? FlagsText : flag.Label();
        }
    }
    internal static string FormatFlags(long reason, bool closeFirst)
    {
        if (reason == 0)
            return L.T("사유 비트 없음", "No reason bits");
        bool promoteClose = closeFirst && (reason & 0x80000000) != 0;
        var labels = Flags.Where(flag => (reason & flag.Mask) != 0 && !(promoteClose && flag.Mask == 0x80000000)).Select(flag => flag.Label());
        if (promoteClose)
            labels = labels.Prepend(L.T("파일 닫기", "File closed"));
        long unknown = reason & ~KnownReasonMask;
        if (unknown != 0)
            labels = labels.Append(L.T("미확인 비트 0x{0:X8}", "Unknown bits 0x{0:X8}", unknown));
        return string.Join(" · ", labels);
    }
    public string FlagsText => FormatFlags(Reason, false);
    public string Time => Timestamp is long value ? FormatTime(value, "HH:mm:ss.FFF") : L.T("시각 없음", "No timestamp");
    public string RecordLabel => Version == 4 ? L.T("변경된 바이트 범위", "Changed byte ranges") + " · " + FlagsText : FormatFlags(Reason, true);
    public string ProofReason => RecordLabel;
    public string DetailedTime(JournalTimeZone utc) => Timestamp is long stamp
        ? GlobalLogFilter.DisplayTime(stamp, utc) + (utc.WindowsZone == null ? " " + utc.Code : "")
        : L.T("시각이 기록되지 않음", "No timestamp recorded");
    public string Day => Timestamp is long value ? FormatTime(value, "yyyy-MM-dd") : "";
    public static string FormatTime(long value, string format) => GlobalLogFilter.TimeText(value, DisplayTimeZone.Current.Zone, format);
}

public sealed class Journal(string path)
{
    public string DbPath => path;
    internal PathResolver HistoricalPaths { get; } = new(path, null);

    public string? CheckPair(string mftDbPath)
    {
        using var j = Open();
        using var mft = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = mftDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        mft.Open();
        using var recent = j.CreateCommand();
        recent.CommandText = """
            SELECT r.file,r.parent,r.name FROM files f JOIN records r ON r.pos=f.pos
            WHERE length(r.file)=8 AND r.name IS NOT NULL ORDER BY f.pos DESC LIMIT 192
            """;
        using var match = mft.CreateCommand();
        match.CommandText = """
            SELECT EXISTS(SELECT 1 FROM mft_names n JOIN mft_entries e ON e.id=n.file
              WHERE n.file=$file AND n.parent=$parent AND n.name=$name AND e.in_use=1)
            """;
        var file = match.Parameters.Add("$file", SqliteType.Blob);
        var parent = match.Parameters.Add("$parent", SqliteType.Blob);
        var name = match.Parameters.Add("$name", SqliteType.Text);
        using var rows = recent.ExecuteReader();
        int candidates = 0;
        while (rows.Read())
        {
            var id = (byte[])rows[0];
            if ((BitConverter.ToUInt64(id) & 0x0000ffffffffffffUL) < 24)
                continue;
            candidates++;
            file.Value = id;
            parent.Value = rows[1];
            name.Value = rows.GetString(2);
            if (Convert.ToInt32(match.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                return null;
        }
        return candidates == 0
            ? L.T("두 자료의 출처를 비교할 파일 기록이 부족합니다. 같은 볼륨에서 추출했는지 확인하세요.", "Insufficient file records to compare sources. Verify that both files came from the same volume.")
            : L.T("최근 파일 {0}개의 표본에서 식별자·이름·상위 폴더가 모두 일치하는 항목을 찾지 못했습니다. 두 자료의 볼륨과 수집 시점을 확인하세요.", "None of the {0} sampled recent files matched on identifier, name and parent. Check the volume and acquisition time of both sources.", candidates);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }
    public (long Files, long Records, long Warnings, string Hash) Summary() => Summary(carved: false);
    internal (long Files, long Records, long Warnings, string Hash) Summary(bool carved)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        EngineIndexFormat.Validate(c, carved ? EngineArtifact.CarvedJournal : EngineArtifact.Journal);
        cmd.CommandText = "SELECT (SELECT count(*) FROM files),records,skipped,hash FROM summary";
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            throw new InvalidDataException(L.T("분석 결과가 완성되지 않았습니다.", "Analysis results are incomplete."));
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3));
    }
    public int? FilePage(byte[] id, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT count(*) FROM files f WHERE f.pos>target.pos)/$size
            FROM files target WHERE target.file=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$size", pageSize);
        return cmd.ExecuteScalar() is long page ? checked((int)page) : null;
    }

    // The outer row r locates the event. Only adjacent directory observations
    // in the same uninterrupted segment can support a parent name.
    private const string FolderAtRecord = """
        SELECT CASE WHEN (before.reason & 0x11200)=0 AND NOT EXISTS (
          SELECT 1 FROM (SELECT d.name,d.parent FROM records d
            WHERE d.file=r.parent AND (d.attr & 16)!=0 AND d.segment=r.segment
              AND (d.usn,d.pos)>(r.usn,r.pos) ORDER BY d.usn,d.pos LIMIT 1) after
          WHERE after.name IS NOT before.name OR after.parent IS NOT before.parent
        ) THEN before.name END
        FROM records before WHERE before.file=r.parent AND (before.attr & 16)!=0
          AND before.segment=r.segment AND (before.usn,before.pos)<=(r.usn,r.pos)
        ORDER BY before.usn DESC,before.pos DESC LIMIT 1
        """;

    public List<FileRow> Files(string query, int page, int size)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = query.Length == 0 ? "" : "WHERE f.file IN (SELECT file FROM aliases WHERE instr(lower(name),lower($q))>0)";
        cmd.CommandText = $"""
            WITH selected AS (SELECT f.* FROM files f {where} ORDER BY pos DESC LIMIT $n OFFSET $offset)
            SELECT {GlobalLogQuery.RecordProjection},s.name,s.attr,s.hits,({FolderAtRecord})
            FROM selected s JOIN records last ON last.pos=s.pos
            JOIN records r ON r.pos=CASE WHEN last.name IS NULL AND s.name IS NOT NULL THEN
              (SELECT pos FROM records WHERE file=s.file AND name IS NOT NULL ORDER BY usn DESC,pos DESC LIMIT 1)
              ELSE s.pos END
            LEFT JOIN range_records v ON v.pos=r.pos ORDER BY s.pos DESC
            """;
        if (query.Length != 0)
            cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$n", size + 1);
        cmd.Parameters.AddWithValue("$offset", (long)page * size);
        using var r = cmd.ExecuteReader();
        var rows = new List<FileRow>();
        while (r.Read())
        {
            var item = GlobalLogQuery.Row(r, false);
            rows.Add(new(item.FileId, r.IsDBNull(18) ? L.T("이름 없는 범위 기록", "Unnamed range record") : r.GetString(18), r.IsDBNull(19) ? 0 : r.GetInt64(19), r.GetInt32(20), r.IsDBNull(21) ? null : r.GetString(21))
            {
                LastNamedRecord = item.Record
            });
        }
        return rows;
    }
    public List<Entry> History(byte[] id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT r.pos,r.size,r.version,r.parent,r.usn,r.stamp,r.reason,r.source,r.security,r.attr,r.name,
                   r.minor,r.resynced,r.escaped,v.remaining,
                   CASE WHEN r.version=4 THEN (SELECT group_concat(printf('%lld + %lld',start,length),'; ' ORDER BY ordinal)
                     FROM extents WHERE pos=r.pos) END,r.segment,v.*
            FROM records r LEFT JOIN range_records v ON v.pos=r.pos
            WHERE r.file=$id ORDER BY r.usn,r.pos
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        var rows = new List<Entry>();
        while (r.Read())
            rows.Add(new(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), (byte[])r[3], r.GetInt64(4),
            r.IsDBNull(5) ? null : r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt64(8),
            r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? "" : r.GetString(10), r.GetInt32(11), r.GetBoolean(12),
            r.GetBoolean(13), r.IsDBNull(14) ? null : r.GetInt64(14), r.IsDBNull(15) ? "" : r.GetString(15), r.GetInt64(16))
            {
                ExtentStride = r.GetInt32(2) == 4 ? Entry.ReadExtentStride(r, 17) : null
            });
        return rows;
    }
    private static string? LookupFolder(SqliteConnection c, Entry entry)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            WITH r AS (SELECT $parent AS parent,$segment AS segment,$u AS usn,$pos AS pos)
            SELECT ({FolderAtRecord}) FROM r
            """;
        cmd.Parameters.AddWithValue("$parent", entry.Parent);
        cmd.Parameters.AddWithValue("$u", entry.Usn);
        cmd.Parameters.AddWithValue("$pos", entry.Position);
        cmd.Parameters.AddWithValue("$segment", entry.Segment);
        return cmd.ExecuteScalar() as string;
    }
    public string FolderName(Entry entry)
    {
        using var c = Open();
        return LookupFolder(c, entry) is string name ? L.T("{0}  (이전 저널 기록 기준)", "{0}  (earlier journal record)", name) : L.T("폴더 이름을 확인할 수 없음", "Folder name unavailable");
    }
    internal (string Path, string Source) ResolveListValues(Entry record, byte[] file, string fallback,
        PathResolver? resolver, string? warning, bool carved = false) =>
        record.Version == 4 || record.Name.Length == 0 ? (fallback, "") :
        ResolveRecordPath(record, file, resolver, warning, carved).ListValues(record.Name);

    internal PathResult ResolveRecordPath(Entry record, byte[] file, PathResolver? resolver, string? warning, bool carved = false)
    {
        if (record.Version == 4 || record.Name.Length == 0)
            return new PathResult(L.T("경로 미상", "Unknown path"), "이름 기록 없음",
            L.T("이 기록에는 파일 이름이 없습니다.", "This record contains no file name."));
        if (carved)
        {
            if (record.Name is "." or ".." || record.Name.IndexOfAny(['\\', '/', '\0']) >= 0)
                return new PathResult(L.T("경로 미상", "Unknown path"), "이름 확인 필요",
                    L.T("카빙한 이름을 하나의 경로 구성 요소로 확인할 수 없습니다.", "The carved name cannot be verified as a single path component."));
            return new PathResult(@"…\" + record.Name, "덤프 카빙",
                L.T("이 레코드 후보만으로는 상위 경로를 확인할 수 없습니다.", "The carved record does not establish its parent path."));
        }
        if (file.Length == 16 && record.Parent.Length == 16 && record.Name is not "." and not ".." && record.Name.IndexOfAny(['\\', '/', '\0']) < 0)
        {
            var observed = ObservedPath(record);
            return observed with
            {
                Explanation = observed.Explanation + " " + L.T("128비트 식별자는 MFT의 64비트 참조와 직접 대조할 수 없습니다.", "A 128-bit identifier cannot be matched directly to a 64-bit MFT reference.")
            };
        }
        var result = (resolver ?? HistoricalPaths).Resolve(record, file);
        return warning == null ? result : result with
        {
            PairingUnverified = true,
            Explanation = warning + "\n" + result.Explanation
        };
    }

    internal PathResult ObservedPath(Entry entry)
    {
        using var c = Open();
        string? parent = LookupFolder(c, entry);
        if (string.IsNullOrEmpty(parent) || parent == ".")
            return new($@"…\{entry.Name}", "상위 경로 미확인", L.T("이 시점의 상위 폴더 이름을 저널에서 확인할 수 없습니다. 같은 볼륨의 MFT를 연결해 경로를 조사할 수 있습니다.", "The journal does not identify the parent folder at this time. Attach the MFT from the same volume to investigate the path."));
        return new($@"…\{parent}\{entry.Name}", "직속 폴더만 확인", L.T("같은 저널 구간에서 관측한 직속 폴더 이름입니다. 그 위의 경로는 확인되지 않았습니다.", "The immediate parent name was observed in the same journal segment. Higher folders are unknown."));
    }
    public List<Activity> Activities(List<Entry> entries)
    {
        var list = Activity.From(entries);
        using var c = Open();
        foreach (var a in list.Where(x => x.Moved))
        {
            var before = LookupFolder(c, a.Records[0]) ?? L.T("이전 폴더 이름 미상", "Unknown previous folder");
            var after = LookupFolder(c, a.Records[1]) ?? L.T("새 폴더 이름 미상", "Unknown new folder");
            var original = a.Description;
            a.Description = before + "   →   " + after;
            if (a.Records[0].Name != a.Records[1].Name)
                a.Description += "   ·   " + original;
        }
        return list;
    }
    public static string Ref(byte[] bytes) => bytes.Length == 8 ?
        L.T("항목 {0}, 사용 회차 {1}", "Entry {0}, sequence {1}", BitConverter.ToUInt64(bytes) & 0x0000ffffffffffff, BitConverter.ToUInt16(bytes, 6)) : Convert.ToHexString(bytes);

    public static void Export(string dest, FileRow file, List<Entry> entries, string source, string hash,
        PathResolver? resolver = null, string? mftHash = null, string? pairWarning = null, string? mftSource = null, Journal? journal = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(dest);
        if (new[] { source, mftSource }.Any(p => !string.IsNullOrWhiteSpace(p) &&
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(p), destination)))
            throw new IOException(L.T("원본 증거 파일과 다른 CSV 저장 경로를 선택해 주세요.", "Choose a CSV destination other than the source evidence file."));
        string temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new StreamWriter(temp, false, new UTF8Encoding(true)))
            {
                JournalTimeZone utc = DisplayTimeZone.Current.Zone;
                var header = L.T("파일 이름,기록 시각(KST),변경 사유,사유 비트,USN,파일 참조,상위 폴더 참조,추출 파일 오프셋,바이트 길이,원본 FILETIME,원본 파일,SHA-256,USN 형식,변경 범위(시작 + 길이),후속 범위 수,손상 직후 재동기화,파일명 코드 단위 이스케이프", "File name,Record time (KST),Change reason,Reason bits,USN,File reference,Parent reference,Extraction offset,Byte length,Raw FILETIME,Source file,SHA-256,USN format,Changed ranges (start + length),Remaining extents,Resynced after damage,Escaped file name code units");
                var snapshot = resolver?.Snapshot(file.Id);
                if (journal != null || resolver != null)
                    header += L.T(",기록 당시 경로,당시 경로의 근거,당시 경로 설명", ",Event-time path,Event path basis,Event path explanation");
                if (snapshot != null)
                    header += L.T(",MFT 수집 시점 경로,수집 경로의 근거,수집 경로 설명,MFT SHA-256", ",MFT acquisition path,Acquisition path basis,Acquisition path explanation,MFT SHA-256");
                output.WriteLine(header.Replace("KST", utc.Code, StringComparison.Ordinal));
                foreach (var e in entries)
                {
                    token.ThrowIfCancellationRequested();
                    string[] values = [e.Name,e.Timestamp is long stamp?GlobalLogFilter.PreciseTime(stamp,utc):"",e.FlagsText,$"0x{e.Reason:X8}",e.Usn.ToString(CultureInfo.InvariantCulture),Ref(file.Id),
                        Ref(e.Parent),e.Position.ToString(CultureInfo.InvariantCulture),e.Size.ToString(CultureInfo.InvariantCulture),e.Timestamp?.ToString(CultureInfo.InvariantCulture)??"",source,hash,
                        $"{e.Version}.{e.Minor}",e.Extents,e.Remaining?.ToString(CultureInfo.InvariantCulture)??"",e.Resynced?L.T("예", "Yes"):"",e.Escaped?L.T("예", "Yes"):""];
                    var pairing = pairWarning == null ? "" : L.T(" 자료 연결 주의. ", " Source pairing warning. ") + pairWarning;
                    var historical = journal?.ResolveRecordPath(e, file.Id, resolver, pairWarning) ?? resolver?.Resolve(e, file.Id);
                    if (historical != null)
                        values = [.. values, historical.Display, historical.DisplayBasis, historical.Explanation + (journal == null ? pairing : "")];
                    if (snapshot != null)
                        values = [.. values, snapshot.Display, snapshot.DisplayBasis, snapshot.Explanation + pairing, mftHash ?? ""];
                    output.WriteLine(string.Join(",", values.Select(v => "\"" + v.Replace("\"", "\"\"") + "\"")));
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, dest, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
