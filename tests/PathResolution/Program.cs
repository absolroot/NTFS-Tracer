using JournalDesk;
using Microsoft.Data.Sqlite;

WindowLayoutChecks.Run();
LocaleChecks.Run();
var root = Directory.CreateTempSubdirectory("JournalDesk-path-test-");
try
{
    Test(root.FullName);
    TransactionSearchChecks.Run(root.FullName);
    await IndexCacheChecks.RunAsync(root.FullName);
    await GlobalPathRankChecks.RunAsync(root.FullName);
}
finally { SqliteConnection.ClearAllPools(); root.Delete(true); }

static void Test(string dir)
{
    static byte[] Key(int slot, int sequence = 1) => BitConverter.GetBytes((uint)slot | ((ulong)(ushort)sequence << 48));
    static void Check(bool ok, string message)
    {
        if (!ok)
            throw new InvalidOperationException(message);
    }
    var mft = Path.Combine(dir, "mft");
    var journal = Path.Combine(dir, "journal");
    using var m = new SqliteConnection("Data Source=" + mft);
    using var j = new SqliteConnection("Data Source=" + journal);
    m.Open();
    j.Open();
    static void Run(SqliteConnection db, string sql, params (string, object)[] values)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in values)
            cmd.Parameters.AddWithValue(key, value);
        cmd.ExecuteNonQuery();
    }
    Run(m, "CREATE TABLE mft_entries(id BLOB PRIMARY KEY,directory INTEGER,in_use INTEGER,names_complete INTEGER); CREATE TABLE mft_names(file BLOB,parent BLOB,name TEXT,namespace INTEGER);");
    Run(j, "CREATE TABLE records(file BLOB,usn INTEGER,pos INTEGER,segment INTEGER,attr INTEGER,reason INTEGER,name TEXT,parent BLOB);");
    void Add(int slot, bool isDir, bool allocated = true, bool complete = true, int seq = 1) =>
        Run(m, "INSERT INTO mft_entries VALUES($f,$d,$a,$c)", ("$f", Key(slot, seq)), ("$d", isDir), ("$a", allocated), ("$c", complete));
    void Name(int slot, int parent, string name, int ns = 1) =>
        Run(m, "INSERT INTO mft_names VALUES($f,$p,$n,$ns)", ("$f", Key(slot)), ("$p", Key(parent)), ("$n", name), ("$ns", ns));
    void Observe(int slot, int parent, string name, long usn, long reason, int segment = 0) =>
        Run(j, "INSERT INTO records VALUES($f,$u,$u,$s,16,$r,$n,$p)", ("$f", Key(slot)), ("$p", Key(parent)), ("$u", usn), ("$r", reason), ("$s", segment), ("$n", name));
    Entry Change(int parent, long usn, int segment = 0, string name = "문서.txt") =>
        new(usn, 80, 2, Key(parent), usn, 1, 256, 0, 1, 32, name, 0, false, false, null, "", segment);

    Add(5, true);
    Name(5, 5, ".");
    Add(6, true);
    Name(6, 5, "보관");
    Name(6, 5, "ARCHIV~1", 2);
    Add(7, true);
    Name(7, 6, "새폴더");
    Add(8, false);
    Name(8, 7, "완료.txt");
    Add(9, false);
    Name(9, 6, "A");
    Name(9, 7, "B");
    Add(10, true, seq: 2);
    Add(11, true, complete: false);
    Name(11, 5, "불완전");
    Add(12, false, allocated: false);
    Name(12, 6, "삭제.txt");
    Add(13, true);
    Name(13, 14, "A");
    Add(14, true);
    Name(14, 13, "B");
    Add(15, true);
    Name(15, 5, "현재");
    Add(16, true);
    Name(16, 5, "이후");
    Observe(16, 5, "이후", 100, 0x2000);
    Add(17, true);
    Name(17, 5, "새이름");
    Observe(17, 5, "변경전", 100, 0x1000);
    Observe(17, 5, "새이름", 101, 0x2000);
    Observe(7, 5, "옛폴더", 10, 0x100);
    Observe(7, 5, "옛폴더", 30, 0x1000);
    Observe(7, 6, "새폴더", 32, 0x2000);
    Observe(15, 5, "과거", 5, 0x100, 0);
    var paths = new PathResolver(journal, mft);
    var transactionPaths = new PathResolver(null, mft);
    var transaction = transactionPaths.ResolveTransactionName("기록된이름.txt", Key(7));
    Check(transaction.Display == @"\보관\새폴더\기록된이름.txt" && transaction.IsEstimated &&
        transaction.Explanation.Contains("작업 당시"), "transaction paths keep the logged leaf and label acquisition-time folders");
    Check(transactionPaths.ResolveTransactionName("기록된이름.txt", Key(7, 2)).Display == @"…\기록된이름.txt",
        "changed MFT sequence cannot provide a transaction folder");
    Check(transactionPaths.ResolveTransactionName("기록된이름.txt", Key(8)).Display == @"…\기록된이름.txt",
        "a matching file reference cannot stand in for a parent folder");
    var transactionRoot = new PathResolver(null, null).ResolveTransactionName("root.txt", Key(5));
    Check(transactionRoot.Display == @"\root.txt" && !transactionRoot.IsEstimated, "root path recorded directly in the transaction");
    Check(transactionPaths.ResolveTransactionName("기록된이름.txt", Key(5, 0)).IsPartial, "root reference requires a sequence");
    Check(NtfsReference.TryParse("300:2", out var exact) && exact.Matches(Key(300, 2)) && !exact.Matches(Key(300, 3)),
        "exact NTFS reference distinguishes MFT sequences");
    Check(NtfsReference.TryParse("300", out var slot) && slot.Matches(Key(300, 3))
        && !slot.Matches([.. Key(300, 3), .. new byte[8]]), "slot search cannot truncate a 128-bit reference");
    Check(NtfsReference.TryParse("0x000200000000012C  (MFT record 300, sequence 2)", out var copied) && copied == exact,
        "accept the reference copied from Record details");
    Check(NtfsReference.DisplayId(Key(300, 2)) == "562949953421612"
        && NtfsReference.TryParse("562949953421612", out var decimalId) && decimalId == exact,
        "the displayed decimal ID round-trips to the exact file reference");
    Check(NtfsReference.DisplayId(Key(300, 0)) == "0x000000000000012C"
        && NtfsReference.TryParse("0x000000000000012C", out var zeroSequence)
        && zeroSequence.Matches(Key(300, 0)) && !zeroSequence.Matches(Key(300, 2)),
        "zero-sequence IDs display a value that cannot be mistaken for entry-only search");
    Check(!NtfsReference.TryParse("300:65536", out _) && !NtfsReference.TryParse("0x!", out _), "invalid reference values are rejected");
    Check(paths.Snapshot(Key(8)).Display == @"\보관\새폴더\완료.txt", "snapshot chain");
    Check(paths.Snapshot(Key(9)).Display.Split(Environment.NewLine).Length == 2 && paths.Snapshot(Key(9)).Basis.Contains("여러"), "multiple real links");
    Check(paths.Snapshot(Key(6)).Display == @"\보관", "DOS alias suppression");
    Check(paths.Snapshot(Key(12)).Basis.Contains("삭제"), "inactive item");
    var early = paths.Resolve(Change(7, 20), Key(8));
    Check(early.Display == @"\옛폴더\문서.txt" && early.Basis.Contains("저널"), "historical ancestor");
    var late = paths.Resolve(Change(7, 40), Key(8));
    Check(late.Display == @"\보관\새폴더\문서.txt" && late.Basis.Contains("추정"), "mixed snapshot fallback");
    Check(paths.Resolve(Change(7, 31), Key(8)).Display == @"…\문서.txt", "middle of incomplete rename");
    Check(paths.Resolve(Change(10, 20), Key(8)).Explanation.Contains("재사용"), "sequence reuse");
    Check(paths.Resolve(Change(11, 20), Key(8)).Explanation.Contains("모두 읽지"), "unknown external name");
    Check(paths.Resolve(Change(13, 20), Key(8)).Explanation.Contains("순환"), "cyclic folder links");
    var separated = paths.Resolve(Change(15, 20, 1), Key(8));
    Check(separated.Basis.Contains("추정") && separated.Explanation.Contains("읽지 못한"), "no crossing gap");
    Check(paths.Resolve(Change(16, 25), Key(8)).Display == @"…\문서.txt", "never project a later new name back in time");
    Check(paths.Resolve(Change(17, 25), Key(8)).Display == @"\변경전\문서.txt", "use an explicit later old name to reconstruct an earlier parent");
    Check(paths.Resolve(Change(19, 20), Key(8)).Display == @"…\문서.txt", "missing parent");
    var usnOnly = new PathResolver(journal, null);
    Observe(40, 5, "상위", 1, 0x100);
    Observe(41, 40, "하위", 2, 0x100);
    Check(usnOnly.Resolve(Change(41, 15), Key(50)).Display == @"\상위\하위\문서.txt", "journal-only path through two named ancestors");
    Check(usnOnly.Resolve(Change(5, 15) with
    {
        Parent = Key(5, 0)
    }, Key(50)).Display == @"…\문서.txt", "a zero-sequence reference cannot establish the journal-only root");
    Add(42, true);
    Name(42, 43, "남은폴더");
    var mixedPartial = paths.Resolve(Change(42, 15), Key(50));
    Check(mixedPartial.Display == @"…\남은폴더\문서.txt" && mixedPartial.IsPartial && mixedPartial.HasMftSegments, "partial path retains independent snapshot provenance");
    Check(usnOnly.Resolve(Change(41, 15, 1), Key(50)).Display == @"…\문서.txt", "journal-only path cannot cross a damaged segment");
    Check(usnOnly.Resolve(Change(7, 31), Key(8)).Display == @"…\문서.txt", "do not infer a folder during its rename gap without MFT");
    Check(usnOnly.Resolve(Change(6, 40), Key(8)).Display == @"…\문서.txt", "no acquisition snapshot when MFT is absent");
    var v4 = Change(7, 40) with
    {
        Version = 4,
        Parent = new byte[16],
        Name = ""
    };
    Check(paths.Resolve(v4, new byte[16]).Basis.Contains("이름"), "V4 does not acquire arbitrary name");
    var unpaired = new Journal(journal);
    Observe(28, 5, "옛 위치", 10, 0);
    Observe(28, 6, "변경된 위치", 30, 0);
    Check(unpaired.FolderName(Change(28, 20)).Contains("확인할 수 없음"), "unpaired parent changed across event");
    Check(unpaired.FolderName(Change(28, 35)).Contains("변경된 위치"), "unpaired observed name");
    Check(unpaired.FolderName(Change(28, 35, 1)).Contains("확인할 수 없음"), "unpaired segment boundary");
    Observe(28, 6, "변경된 위치", 36, 0x200);
    Check(unpaired.FolderName(Change(28, 37)).Contains("확인할 수 없음"), "unpaired deleted parent");
    Console.WriteLine("Path resolution fixtures passed.");
}
