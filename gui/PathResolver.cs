using System.Globalization;
using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace JournalDesk;

public sealed record PathResult(string Display, string Basis, string Explanation)
{
    internal (string Path, string Source) ListValues(string recordedName) =>
        (HasKnownPath ? Display : recordedName, SourceLabel);
    internal static (string Path, string Source) UnreadableListValues(string recordedName) =>
        (@"…\" + recordedName, "—");
    public bool HasKnownPath => Display.StartsWith('\\') || Display.StartsWith("…\\", StringComparison.Ordinal);
    public bool IsMftSnapshot => Basis == "MFT 수집 시점";
    public bool IsPartial => Basis == "일부 경로";
    public bool HasMftSegments
    {
        get; init;
    }
    public bool IsEstimated => HasMftSegments || Basis == "수집 시점 자료로 추정";
    public bool PairingUnverified
    {
        get; init;
    }
    public bool HasPairWarning => PairingUnverified || Basis == "자료 연결 주의";
    public string SourceLabel => !HasKnownPath ? "—" : Basis == "덤프 카빙" ? L.T("카빙 후보", "Carved candidate") :
        IsMftSnapshot ? "MFT" : IsEstimated ? "USN + MFT" : "USN";
    public string DisplayBasis => PairingUnverified ? L.T("자료 연결 주의", "Check source pairing") : Basis switch
    {
        "수집 시점 자료로 추정" => L.T("MFT를 참고한 경로", "Path inferred from MFT"),
        "저널로 재구성" => L.T("USN Journal로 복원", "Reconstructed from USN Journal"),
        "일부 경로" => L.T("확인된 구간만 표시", "Known segments only"),
        "MFT 수집 시점" => "MFT",
        "자료 연결 주의" => L.T(Basis, "Check source pairing"),
        "상위 경로 미확인" => L.T(Basis, "Unknown parent path"),
        "덤프 카빙" => L.T("상위 경로 미확인", "Unknown parent path"),
        "직속 폴더만 확인" => L.T(Basis, "Immediate parent only"),
        "이름 기록 없음" => L.T(Basis, "No name recorded"),
        "이름 확인 필요" => L.T(Basis, "Name requires verification"),
        "연결 불가" => L.T(Basis, "Cannot link"),
        "여러 경로 후보" => L.T(Basis, "Multiple candidate paths"),
        "삭제 후 남은 경로 후보" => L.T(Basis, "Residual path after deletion"),
        "수집 정보 불완전" => L.T(Basis, "Incomplete acquisition data"),
        "확인 불가" => L.T(Basis, "Unavailable"),
        _ => Basis
    };
}

/// <summary>Resolves paths relative to the root of one analyst-paired NTFS volume.</summary>
public sealed class PathResolver
{
    private const ulong SlotMask = 0x0000_FFFF_FFFF_FFFF;
    private const int MaxDepth = 128;
    private const int MaxAlternatives = 8;
    private const int CacheLimit = 8192;
    private readonly string? journalConnection;
    private readonly string? mftConnection;
    internal string? MftIndexPath
    {
        get;
    }
    private readonly DirectoryCache<Node> nodes = new();
    private readonly DirectoryCache<FolderHistory> histories = new();
    private readonly DirectoryCache<FolderPath> transactionFolders = new();

    private sealed class DirectoryCache<T> where T : class
    {
        private readonly Dictionary<ulong, T> entries = [];
        private readonly Queue<ulong> inserted = [];

        internal T? Get(ulong id)
        {
            lock (entries)
                return entries.GetValueOrDefault(id);
        }

        internal void Add(ulong id, T value)
        {
            lock (entries)
            {
                if (!entries.TryAdd(id, value))
                {
                    entries[id] = value;
                    return;
                }
                if (inserted.Count == CacheLimit)
                    entries.Remove(inserted.Dequeue());
                inserted.Enqueue(id);
            }
        }
    }

    public PathResolver(string? journalPath, string? mftPath)
    {
        MftIndexPath = mftPath;
        journalConnection = journalPath == null ? null : ConnectionString(journalPath);
        mftConnection = mftPath == null ? null : ConnectionString(mftPath);
    }

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = true
    }.ToString();

    // Connections are used only for cache misses and never shared between workers.
    private sealed class Reader(PathResolver owner) : IDisposable
    {
        private SqliteConnection? journal, mft;
        public SqliteConnection Journal => journal ??= Open(owner.journalConnection ?? throw new InvalidOperationException());
        public SqliteConnection Mft => mft ??= Open(owner.mftConnection ?? throw new InvalidOperationException());
        private static SqliteConnection Open(string cs)
        {
            var c = new SqliteConnection(cs);
            try
            {
                c.Open();
                return c;
            }
            catch { c.Dispose(); throw; }
        }
        public void Dispose()
        {
            journal?.Dispose();
            mft?.Dispose();
        }
    }

    private sealed record Link(ulong Parent, string Name);
    private sealed record Node(bool Found, bool Reused, bool Directory, bool InUse, bool Complete, Link[] Links);
    private sealed record Observation(long Usn, long Pos, long Reason, Link Link);
    private sealed record FolderHistory(Dictionary<long, List<Observation>> Segments);
    private sealed record FolderPath(string Language, PathResult? Result);

    private static byte[] Key(ulong id)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(key, id);
        return key;
    }
    private static ulong Id(byte[] key) => BinaryPrimitives.ReadUInt64LittleEndian(key);
    private static bool IsRoot(ulong id) => (id & SlotMask) == 5;
    private static bool ValidComponent(string name) => name.Length != 0 && name is not ("." or "..")
        && name.IndexOfAny(['\\', '/', '\0']) < 0;
    private static string PathOf(List<string> bottomUp, bool complete)
    {
        string tail = string.Join("\\", bottomUp.AsEnumerable().Reverse());
        return complete ? "\\" + tail : tail.Length == 0 ? L.T("경로 미상", "Unknown path") : "…\\" + tail;
    }

    private Node GetNode(Reader reader, ulong id)
    {
        if (nodes.Get(id) is { } cached)
            return cached;
        using var cmd = reader.Mft.CreateCommand();
        cmd.CommandText = """
            SELECT e.directory,e.in_use,e.names_complete,n.parent,n.name,n.namespace
            FROM mft_entries e LEFT JOIN mft_names n ON n.file=e.id WHERE e.id=$id
            """;
        cmd.Parameters.AddWithValue("$id", Key(id));
        bool found = false, directory = false, inUse = false, complete = false;
        var names = new List<(Link Link, int Namespace)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                found = true;
                directory = r.GetBoolean(0);
                inUse = r.GetBoolean(1);
                complete = r.GetBoolean(2);
                if (!r.IsDBNull(3) && r[3] is byte[] { Length: 8 } parent)
                    names.Add((new(Id(parent), r.GetString(4)), r.GetInt32(5)));
            }
        }
        bool reused = false;
        if (!found)
        {
            // LE references with the same slot share their first six BLOB bytes.
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM mft_entries WHERE id BETWEEN $lo AND $hi)";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$lo", Key(id & SlotMask));
            cmd.Parameters.AddWithValue("$hi", Key((id & SlotMask) | ~SlotMask));
            reused = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
        var links = names.GroupBy(x => x.Link.Parent)
            .SelectMany(group => group.Any(x => x.Namespace != 2) ? group.Where(x => x.Namespace != 2) : group)
            .Select(x => x.Link).Distinct().OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Parent).ToArray();
        var node = new Node(found, reused, directory, inUse, complete, links);
        nodes.Add(id, node);
        return node;
    }

    private FolderHistory GetHistory(Reader reader, ulong id)
    {
        if (histories.Get(id) is { } cached)
            return cached;
        using var cmd = reader.Journal.CreateCommand();
        cmd.CommandText = """
            SELECT usn,pos,reason,parent,name,segment FROM records
            WHERE file=$id AND (attr & 16)!=0 ORDER BY usn,pos
            """;
        cmd.Parameters.AddWithValue("$id", Key(id));
        var segments = new Dictionary<long, List<Observation>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r[3] is not byte[] { Length: 8 } parent || r.IsDBNull(4))
                continue;
            var segment = r.GetInt64(5);
            if (!segments.TryGetValue(segment, out var rows))
                segments[segment] = rows = [];
            rows.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), new(Id(parent), r.GetString(4))));
        }
        var history = new FolderHistory(segments);
        histories.Add(id, history);
        return history;
    }

    private static (Observation? Before, Observation? After) Neighbors(List<Observation> rows, Entry entry)
    {
        int lo = 0, hi = rows.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            var row = rows[mid];
            if (row.Usn < entry.Usn || row.Usn == entry.Usn && row.Pos <= entry.Position)
                lo = mid + 1;
            else
                hi = mid;
        }
        return (lo == 0 ? null : rows[lo - 1], lo == rows.Count ? null : rows[lo]);
    }

    private static string Missing(Node node) => node.Reused
        ? L.T("같은 MFT 항목이 다른 파일에 재사용되어 당시 경로를 확인할 수 없습니다.", "The MFT entry was reused for another file. The historical path cannot be determined.")
        : L.T("필요한 항목이 수집한 MFT에 없습니다. 삭제되었거나 수집 범위에서 빠졌을 수 있습니다.", "The required entry is absent from the acquired MFT. It may have been deleted or omitted during acquisition.");

    private string? HistoricalLink(Reader reader, ulong id, Entry entry,
        HashSet<string> notes, HashSet<string> snapshotNames, out Link? result)
    {
        result = null;
        var history = GetHistory(reader, id);
        Observation? later = null;
        if (history.Segments.TryGetValue(entry.Segment, out var rows))
        {
            var (before, after) = Neighbors(rows, entry);
            later = after;
            // OLD_NAME and DELETE are endpoints, and even CLOSE records can repeat these flags.
            const long oldOrDeleted = 0x1000 | 0x200;
            const long newOrCreated = 0x2000 | 0x100;
            if (before != null && (before.Reason & oldOrDeleted) != 0)
                return L.T("상위 폴더의 이전 이름 또는 삭제 기록만 남아 그 다음 위치를 확인할 수 없습니다.", "Only the old name or deletion of the parent folder remains. Its subsequent location is unknown.");
            if (before != null && after != null && before.Link != after.Link)
                return L.T("이 사건 전후로 상위 폴더의 이름이나 위치가 다릅니다. 어느 시점에 바뀌었는지 좁힐 수 없습니다.", "The parent folder's name or location differs before and after this event. The time of the change cannot be narrowed down.");
            if (before != null)
            {
                if ((before.Reason & 0x10000) != 0)
                    return L.T("상위 폴더에 여러 이름이 연결된 흔적이 있어 하나의 경로를 선택할 수 없습니다.", "The parent folder has evidence of multiple linked names. No single path can be determined.");
                result = before.Link;
                if (after == null)
                    notes.Add(L.T("일부 상위 폴더 이름은 사건 전에 남은 마지막 저널 기록에서 확인했습니다. 그 뒤에 이름이나 위치가 바뀌었을 수 있습니다.", "Some folder names come from the last journal record before this event. Those names or locations may have changed afterward."));
                return null;
            }
            if (after != null && (after.Reason & newOrCreated) != 0)
                return L.T("상위 폴더가 만들어지거나 새 이름을 받은 기록이 모두 사건 이후에 남아 있습니다. 사건 당시 폴더 이름은 확인할 수 없습니다.", "The parent folder’s creation or new-name records all come after this event. Its name at the time of the event is unknown.");
            if (after != null && (after.Reason & 0x1000) != 0 && (after.Reason & 0x10000) == 0)
            {
                result = after.Link;
                notes.Add(L.T("상위 폴더의 이후 이름 변경 기록에 남은 이전 이름을 사용했습니다.", "The old name comes from a later rename of the parent folder."));
                return null;
            }
        }
        else if (history.Segments.Count != 0)
            notes.Add(L.T("상위 폴더가 기록된 시점과 사건 시점 사이에 읽지 못한 저널 구간이 있습니다.", "Part of the journal between the parent folder record and this event could not be read."));

        if (mftConnection == null)
            return L.T("같은 저널 구간에서 상위 폴더를 확인할 수 없습니다. MFT가 연결되지 않았습니다.", "The parent folder cannot be identified in this journal segment. No MFT is attached.");
        var node = GetNode(reader, id);
        if (!node.Found)
            return Missing(node);
        if (!node.Directory)
            return L.T("상위 폴더로 연결된 MFT 항목이 디렉터리가 아닙니다.", "The MFT entry referenced as the parent is not a directory.");
        if (!node.InUse)
            return L.T("수집 당시 MFT에서 상위 폴더 항목의 할당이 이미 해제된 상태였습니다. 이 항목으로는 이전 위치를 확인할 수 없습니다.", "The parent folder’s MFT entry was already unallocated at acquisition. It does not establish the folder’s former location.");
        if (!node.Complete)
            return L.T("상위 폴더의 이름 정보를 모두 읽지 못해 유일한 경로인지 확인할 수 없습니다.", "Some parent names could not be read. The path may not be unique.");
        if (node.Links.Length != 1)
            return node.Links.Length == 0 ? L.T("상위 폴더의 이름 정보가 MFT에 남아 있지 않습니다.", "The MFT contains no surviving name for the parent folder.")
            : L.T("상위 폴더에 여러 이름이나 위치가 남아 있습니다. 사건 당시 경로를 하나로 특정할 수 없습니다.", "The parent folder has several recorded names or locations. The path used at the time of the event is unknown.");
        if (later != null && later.Link != node.Links[0])
            return L.T("이 사건 이후에 관측된 상위 폴더의 위치와 수집 시점의 위치가 다릅니다. 당시 위치를 확인할 수 없습니다.", "The parent location observed after this event differs from its location at acquisition. Its event-time location is unknown.");
        result = node.Links[0];
        snapshotNames.Add(result.Name);
        return null;
    }

    public PathResult Resolve(Entry entry, byte[] fileId)
    {
        if (entry.Version == 4 || entry.Name.Length == 0)
            return new(L.T("경로 미상", "Unknown path"), "이름 기록 없음", L.T("이 저널 기록에는 당시 파일명이 없어 해당 시점의 경로를 만들 수 없습니다.", "This journal record lacks the event-time file name required to construct a path."));
        if (entry.Parent.Length != 8 || fileId.Length != 8)
            return new("…\\" + entry.Name, "일부 경로", L.T("이 기록의 128비트 식별자는 MFT의 64비트 식별자와 직접 연결할 수 없습니다.", "This 128-bit journal identifier cannot be directly linked to a 64-bit MFT reference."));
        using var reader = new Reader(this);
        ulong file = Id(fileId);
        if (IsRoot(file) && (mftConnection != null || (file >> 48) != 0) && entry.Name == "." && Id(entry.Parent) == file)
            return mftConnection != null ? Snapshot(fileId) : new(@"\", "저널로 재구성",
                L.T("저널에서 볼륨 루트의 자기 참조를 확인했습니다.", "The journal records the volume root referencing itself."));
        var segments = new List<string>();
        if (!ValidComponent(entry.Name))
            return new(L.T("경로 미상", "Unknown path"), "이름 확인 필요", L.T("저널에 남은 파일명을 하나의 경로 구성 요소로 읽을 수 없습니다.", "The journal file name cannot be read as a single path component."));
        segments.Add(entry.Name);
        var notes = new HashSet<string>();
        if (entry.Resynced)
            notes.Add(L.T("손상 구간 직후에 다시 읽은 기록입니다.", "This record was read immediately after a damaged segment."));
        var seen = new HashSet<ulong> { file };
        var snapshotNames = new HashSet<string>();
        ulong parent = Id(entry.Parent);
        string? issue = null;
        while (true)
        {
            if (!seen.Add(parent))
            {
                issue = L.T("상위 폴더 참조가 순환하여 경로를 끝까지 확인할 수 없습니다.", "A cycle in the parent references prevents reconstruction of the complete path.");
                break;
            }
            if (IsRoot(parent))
            {
                if (mftConnection == null)
                {
                    if ((parent >> 48) == 0)
                    {
                        issue = L.T("루트 참조에 사용 회차가 없어 볼륨 루트를 확인할 수 없습니다.", "The root reference has no sequence number, so the volume root cannot be confirmed.");
                        break;
                    }
                    notes.Add(L.T("루트 위치는 저널의 NTFS 루트 참조(항목 5)를 기준으로 합니다.", "The journal references NTFS root record 5."));
                    break;
                }
                var node = GetNode(reader, parent);
                if (!node.Found || !node.Directory || !node.InUse)
                    issue = L.T("저널의 최상위 폴더 식별자가 수집한 MFT의 최상위 폴더와 일치하지 않습니다.", "The journal root identifier does not match the acquired MFT root.");
                break;
            }
            if (segments.Count >= MaxDepth)
            {
                issue = L.T("상위 폴더 연결이 안전하게 표시할 수 있는 깊이를 넘었습니다.", "The parent chain exceeds the supported display depth.");
                break;
            }
            issue = HistoricalLink(reader, parent, entry, notes, snapshotNames, out var link);
            if (issue != null || link == null)
                break;
            if (!ValidComponent(link.Name))
            {
                issue = L.T("상위 폴더 이름을 하나의 경로 구성 요소로 읽을 수 없습니다.", "The parent name cannot be read as one path component.");
                break;
            }
            segments.Add(link.Name);
            parent = link.Parent;
        }
        if (issue != null)
            notes.Add(issue);
        if (snapshotNames.Count != 0)
        {
            var labels = string.Join(", ", snapshotNames.Take(3).Select(n => $"‘{n}’"));
            if (snapshotNames.Count > 3)
                labels += L.T(" 외 {0}개", " and {0} more", snapshotNames.Count - 3);
            notes.Add(L.T("저널에 이름이 남지 않은 상위 폴더({0})는 수집 당시 MFT를 참고했습니다. 사건 당시의 이름과 위치는 확인할 수 없습니다.", "Names missing from the journal ({0}) come from the MFT at acquisition. Their names and locations at the time of the event are unknown.", labels));
        }
        else if (issue == null)
            notes.Add(L.T("저널에 기록된 파일 이름과 상위 폴더를 연결해 볼륨의 루트까지 경로를 확인했습니다.", "The journal links this file name to its parent folders up to the volume root."));
        return new(PathOf(segments, issue == null), issue != null ? "일부 경로" : snapshotNames.Count != 0 ? "수집 시점 자료로 추정" : "저널로 재구성", string.Join(Environment.NewLine, notes))
        {
            HasMftSegments = snapshotNames.Count != 0
        };
    }

    internal PathResult ResolveTransactionName(string leaf, byte[]? parentRef)
    {
        if (!ValidComponent(leaf))
            return new("—", "이름 확인 필요", L.Caption("TransactionPathInvalidName"));
        if (parentRef is not { Length: 8 } || BinaryPrimitives.ReadUInt16LittleEndian(parentRef.AsSpan(6)) == 0)
            return new(@"…\" + leaf, "일부 경로", L.Caption("TransactionPathMissingParent"));
        ulong parent = Id(parentRef);
        if (IsRoot(parent))
            return new(@"\" + leaf, "기록된 경로", L.Caption("TransactionPathRoot"));
        if (mftConnection == null)
            return new(@"…\" + leaf, "일부 경로", L.Caption("TransactionPathNeedsMft"));
        var folder = TransactionFolder(parent, parentRef);
        if (folder == null)
            return new(@"…\" + leaf, "일부 경로", L.Caption("TransactionPathParentNotDirectory"));
        string[] candidates = folder.Display.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var paths = candidates.Where(path => path.StartsWith('\\') || path.StartsWith("…\\", StringComparison.Ordinal))
            .Select(path => path.TrimEnd('\\') + "\\" + leaf).ToArray();
        if (paths.Length == 0)
            return new(@"…\" + leaf, "일부 경로", folder.Explanation);
        string evidence = L.Caption("MftPathNotEventTime");
        if (!folder.IsMftSnapshot)
            evidence += Environment.NewLine + folder.Explanation;
        return new(string.Join(Environment.NewLine, paths),
            paths.All(path => path.StartsWith('\\')) ? "수집 시점 자료로 추정" : "일부 경로", evidence)
        {
            HasMftSegments = true
        };
    }

    private PathResult? TransactionFolder(ulong parent, byte[] parentRef)
    {
        string language = L.Code;
        if (transactionFolders.Get(parent) is { } cached && cached.Language == language)
            return cached.Result;
        using var reader = new Reader(this);
        var node = GetNode(reader, parent);
        var result = node.Found && !node.Directory ? null : Snapshot(parentRef);
        transactionFolders.Add(parent, new(language, result));
        return result;
    }

    public PathResult Snapshot(byte[] fileId)
    {
        if (mftConnection == null)
            return new(L.T("경로 미상", "Unknown path"), "연결 불가", L.T("MFT가 연결되지 않았습니다.", "No MFT is attached."));
        if (fileId.Length != 8)
            return new(L.T("경로 미상", "Unknown path"), "연결 불가", L.T("이 식별자는 MFT 파일 참조와 형식이 달라 수집 시점 경로를 확인할 수 없습니다.", "This identifier is incompatible with MFT file references. The acquisition path is unavailable."));
        using var reader = new Reader(this);
        var paths = new List<string>();
        var notes = new HashSet<string>();
        var stack = new List<string>();
        var seen = new HashSet<ulong>();
        bool partial = false, residual = false, incomplete = false, limited = false;
        void Stop(string why)
        {
            partial = true;
            notes.Add(why);
            paths.Add(PathOf(stack, false));
        }
        void Walk(ulong id)
        {
            if (paths.Count >= MaxAlternatives)
            {
                limited = true;
                return;
            }
            if (!seen.Add(id))
            {
                Stop(L.T("상위 폴더 참조가 순환합니다.", "The parent references contain a cycle."));
                return;
            }
            try
            {
                var node = GetNode(reader, id);
                if (!node.Found)
                {
                    Stop(Missing(node));
                    return;
                }
                if (stack.Count > 0 && !node.Directory)
                {
                    Stop(L.T("상위 폴더 참조가 디렉터리를 가리키지 않습니다.", "The parent reference does not refer to a directory."));
                    return;
                }
                if (!node.InUse)
                {
                    residual = true;
                    notes.Add(L.T("할당이 해제된 항목의 잔존 이름입니다. 수집 당시에 그 경로가 존재했는지 확인할 수 없습니다.", "This name survives in an unallocated entry. The path may not have existed at acquisition."));
                }
                if (!node.Complete)
                {
                    incomplete = true;
                    notes.Add(L.T("MFT 이름 정보를 모두 읽지 못했습니다. 다른 이름이나 위치가 더 있을 수 있습니다.", "Some MFT names could not be read. Other names or locations may exist."));
                }
                if (IsRoot(id) && node.Directory && node.InUse)
                {
                    paths.Add(PathOf(stack, true));
                    return;
                }
                if (stack.Count >= MaxDepth)
                {
                    Stop(L.T("폴더 연결이 표시할 수 있는 깊이를 넘었습니다.", "The folder chain exceeds the supported display depth."));
                    return;
                }
                if (node.Links.Length == 0)
                {
                    Stop(L.T("MFT에 필요한 파일명 또는 상위 폴더가 남아 있지 않습니다.", "The MFT lacks a required file name or parent folder."));
                    return;
                }
                foreach (var link in node.Links)
                {
                    if (paths.Count >= MaxAlternatives)
                    {
                        limited = true;
                        break;
                    }
                    if (!ValidComponent(link.Name))
                    {
                        Stop(L.T("경로에 사용할 수 없는 이름이 기록되어 있습니다.", "A recorded name cannot be used in a path."));
                        continue;
                    }
                    stack.Add(link.Name);
                    Walk(link.Parent);
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            finally { seen.Remove(id); }
        }
        Walk(Id(fileId));
        var distinct = paths.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > 1 || limited)
        {
            notes.Add(L.T("같은 항목에 여러 이름이나 폴더가 연결되어 있습니다. 파일을 어느 경로로 열었는지는 확인할 수 없습니다.", "Multiple names or folders refer to the same entry. The path actually used to open the file is unknown."));
            if (limited)
                notes.Add(L.T("경로 후보는 최대 {0}개까지 표시합니다.", "Up to {0} candidate paths are shown.", MaxAlternatives));
            return new(string.Join(Environment.NewLine, distinct), "여러 경로 후보", string.Join(Environment.NewLine, notes));
        }
        if (!partial && !residual && !incomplete)
            notes.Add(L.T("수집한 MFT를 바탕으로 파일 이름과 상위 폴더를 연결했습니다.", "The acquired MFT links the file name to its parent folders."));
        return new(distinct.FirstOrDefault() ?? L.T("경로 미상", "Unknown path"), partial ? "일부 경로" : residual ? "삭제 후 남은 경로 후보" : incomplete ? "수집 정보 불완전" : "MFT 수집 시점", string.Join(Environment.NewLine, notes));
    }
}
