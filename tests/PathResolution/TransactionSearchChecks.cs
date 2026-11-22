using JournalDesk;
using Microsoft.Data.Sqlite;

internal static class TransactionSearchChecks
{
    internal static void Run(string directory)
    {
        string log = Path.Combine(directory, "transaction-search.sqlite");
        string mft = Path.Combine(directory, "transaction-search-mft.sqlite");
        MakeDatabase(log, """
            CREATE TABLE tx_records(id INTEGER PRIMARY KEY,lsn TEXT,prev TEXT,undo_next TEXT,
                pos INTEGER,txid INTEGER,redo TEXT,undo TEXT,redo_len INTEGER,undo_len INTEGER,
                redodata BLOB,undodata BLOB,lcns TEXT,mft_slot INTEGER);
            CREATE TABLE tx_names(id INTEGER,side TEXT,name TEXT,file BLOB,parent BLOB);
            CREATE TABLE tx_moves(old_id INTEGER,new_id INTEGER,file BLOB,old_name TEXT,new_name TEXT,old_parent BLOB,new_parent BLOB);
            CREATE TABLE tx_provenance(id INTEGER,kind TEXT);
            CREATE TABLE tx_times(id INTEGER,side TEXT,created INTEGER,modified INTEGER,changed INTEGER,accessed INTEGER);
            INSERT INTO tx_records(id,lsn,txid,redo,mft_slot) VALUES
                (1,'0000000000000010',8,'UpdateResidentValue',80),
                (2,'0000000000000020',8,'UpdateResidentValue',80),
                (3,'0000000000000030',9,'DeleteIndexEntryRoot',82),
                (4,'0000000000000040',9,'AddIndexEntryRoot',82),
                (5,'0000000000000050',8,'UpdateResidentValue',80);
            INSERT INTO tx_names VALUES
                (1,'redo','shared.txt',x'5000000000000100',x'4E01000000000200'),
                (1,'undo','shared.txt',x'5100000000000400',x'5001000000000200'),
                (2,'redo','unidentified.txt',NULL,x'4E01000000000200'),
                (3,'undo','old.txt',x'5200000000000100',x'4E01000000000200'),
                (4,'redo','new.txt',x'5200000000000100',x'5001000000000200'),
                (5,'redo','root.txt',x'5300000000000100',x'0500000000000100');
            INSERT INTO tx_moves VALUES
                (3,4,x'5200000000000100','old.txt','new.txt',x'4E01000000000200',x'5001000000000200');
            """);
        MakeDatabase(mft, """
            CREATE TABLE mft_entries(id BLOB PRIMARY KEY,directory INTEGER,in_use INTEGER,names_complete INTEGER);
            CREATE TABLE mft_names(file BLOB,parent BLOB,name TEXT,namespace INTEGER);
            INSERT INTO mft_entries VALUES
                (x'0500000000000100',1,1,1),(x'4E01000000000200',1,1,1),(x'5001000000000200',1,1,1);
            INSERT INTO mft_names VALUES
                (x'4E01000000000200',x'0500000000000100','Records',1),
                (x'5001000000000200',x'0500000000000100','Other',1);
            """);

        var bare = new TransactionQuery(log);
        var attached = bare.WithMft(new(new PathResolver(null, mft), "test-$MFT", "test-hash"));
        const string folder = @"\Records\";
        var known = new RecordSearch(folder, Parse("80:1"), Parse("334:2"));
        var results = attached.Page("", 0, "", default, records: known);
        Assert(results is [{ Id: 1, MftSource: "test-$MFT", MftHash: "test-hash" }], "exact file and parent with path");
        Assert(results[0].PathText.Contains(@"\Records\shared.txt", StringComparison.Ordinal) &&
            results[0].PathEvidence.Contains("562949953421646", StringComparison.Ordinal), "same observed link in path evidence");
        Assert(results[0].FileIds.Contains("281474976710736") && results[0].ParentIds.Contains("562949953421646"),
            "overview exposes the exact decimal IDs");
        Assert(attached.Page("", 0, "", default, records: known with
        {
            File = Parse("81:4")
        }).Count == 0, "reject cross-link conjunction");
        Assert(attached.Page("", 0, "", default, records: known with
        {
            Parent = Parse("334:3")
        }).Count == 0, "require full parent sequence");
        Assert(attached.Page("unidentified.txt", 0, "", default, records: new(File: Parse("80"))).Count == 0, "do not use unsequenced MFT slot as file identity");
        Assert(bare.Page("", 0, "", default, records: known).Count == 0, "no inferred path when MFT is detached");
        Assert(bare.Page("root.txt", 0, "", default) is [{ PathText: @"\root.txt", MftSource: null }], "recorded root path needs no MFT");
        Assert(attached.Page("0x10", 0, "", default, records: known) is [{ Id: 1 }] &&
            attached.Page("0x20", 0, "", default, records: known).Count == 0, "combine exact LSN with the additional filters");

        var oldLink = new RecordSearch(@"\Records\old.txt", Parse("82"), Parse("334"));
        Assert(attached.Page("", 3, "", default, records: oldLink) is [{ Id: 4 }], "find move by removed link");
        Assert(attached.Page("", 3, "", default, records: oldLink with
        {
            Parent = Parse("336")
        }).Count == 0,
            "do not combine old path with new parent");
        Assert(attached.BuildOrder("", 3, TransactionSort.Path, default, oldLink).SequenceEqual([4L]), "sort uses the same condition");

        string csv = Path.Combine(directory, "transaction-search.csv");
        long count = attached.Export(csv, "", 3, "$LogFile", "test-log-hash", default, records: oldLink);
        string exported = File.ReadAllText(csv);
        Assert(count == 1 && exported.Contains("mft_SHA256", StringComparison.Ordinal) &&
            exported.Contains("test-hash", StringComparison.Ordinal) && exported.Contains(@"\Records\old.txt", StringComparison.Ordinal),
            "export keeps the same match and MFT provenance");
    }

    private static NtfsReference Parse(string text) => NtfsReference.TryParse(text, out var result) ? result :
        throw new InvalidOperationException($"Invalid fixture reference {text}");

    private static void Assert(bool result, string scenario)
    {
        if (!result)
            throw new InvalidOperationException("Transaction lookup failed: " + scenario);
    }

    private static void MakeDatabase(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
