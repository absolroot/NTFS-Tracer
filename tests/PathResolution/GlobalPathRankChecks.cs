using JournalDesk;
using Microsoft.Data.Sqlite;

internal static class GlobalPathRankChecks
{
    internal static async Task RunAsync(string root)
    {
        string db = Path.Combine(root, "rank-test.sqlite"), mft = Path.Combine(root, "rank-mft");
        string settings = DeskSettings.DirectoryPath;
        DeskSettings.DirectoryPath = Path.Combine(root, "rank-settings");
        try
        {
            void Execute(string file, string sql)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            Execute(db, """
                CREATE TABLE summary(records INTEGER);
                INSERT INTO summary VALUES(3);
                CREATE TABLE records(pos INTEGER PRIMARY KEY, size,version,file,parent,usn,stamp,reason,source,security,attr,name,minor,resynced,escaped,segment);
                CREATE TABLE range_records(pos INTEGER PRIMARY KEY, remaining);
                CREATE TABLE extents(pos,start,length,ordinal);
                INSERT INTO records VALUES
                    (4,60,2,X'01',X'02',2,3,1,0,0,0,'b',0,0,0,0),
                    (2,60,2,X'01',X'02',1,4,2,0,0,0,'a',0,0,0,0),
                    (9,60,2,X'01',X'02',3,2,1,0,0,0,'a',0,0,0,0);
                """);
            File.WriteAllText(mft, "first MFT index");
            var query = new GlobalLogQuery(db);
            var all = new GlobalLogFilter();
            int calls = 0;
            (string, string) Resolve(GlobalLogRow row)
            {
                calls++;
                return (row.Name, row.Reason.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            Task<(IReadOnlyList<long> Paths, IReadOnlyList<long> Sources)> Read(CancellationToken token = default) =>
                query.ReusablePathOrdersAsync(all, Resolve, mft, false, false, token);

            try
            {
                await query.ReusablePathOrdersAsync(all, _ => throw new InvalidDataException("unreadable index"), mft, false, false, default);
                throw new InvalidOperationException("A transient resolver failure must not publish an order.");
            }
            catch (InvalidDataException) { }
            var first = await Read();
            var reopened = await Read();
            if (calls != 3 || !first.Paths.SequenceEqual([2L, 9, 4]) || !first.Sources.SequenceEqual([4L, 9, 2])
                || !first.Paths.SequenceEqual(reopened.Paths) || !first.Sources.SequenceEqual(reopened.Sources))
                throw new InvalidOperationException("The reopened path orders must preserve ties and reuse only the verified artifact pair.");

            var filtered = await query.FilterPathOrdersAsync(new GlobalLogFilter(Reasons: 1), reopened, default);
            if (!filtered.Paths.SequenceEqual([9L, 4]) || !filtered.Sources.SequenceEqual([4L, 9]))
                throw new InvalidOperationException("Both path orders must retain their exact relative order under filters.");

            string cache = Directory.GetFiles(Path.Combine(DeskSettings.DirectoryPath, "sort-cache"), "*.sqlite").Single();
            Execute(cache, "UPDATE ranks SET data=zeroblob(length(data));");
            var repaired = await Read();
            if (calls != 6 || !repaired.Paths.SequenceEqual(first.Paths))
                throw new InvalidOperationException("Corrupt rank blocks must be rebuilt.");

            File.WriteAllText(mft, "other MFT index");
            await Read();
            if (calls != 9)
                throw new InvalidOperationException("A change to the MFT must invalidate the order.");

            Execute(db, "UPDATE records SET name='z' WHERE pos=2");
            var changed = await Read();
            if (calls != 12 || !changed.Paths.SequenceEqual([9L, 4, 2]))
                throw new InvalidOperationException("A change to the journal must invalidate the order.");

            try
            {
                await Read(new CancellationToken(true));
                throw new InvalidOperationException("Cancelled sorting must not publish a result.");
            }
            catch (OperationCanceledException) { }
        }
        finally { DeskSettings.DirectoryPath = settings; }
    }
}
