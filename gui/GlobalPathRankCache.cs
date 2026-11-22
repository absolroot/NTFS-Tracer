using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace JournalDesk;

public sealed partial class GlobalLogQuery
{
    internal async Task<(IReadOnlyList<long> Paths, IReadOnlyList<long> Sources)> ReusablePathOrdersAsync(
        GlobalLogFilter filter, Func<GlobalLogRow, (string Path, string Source)> resolve, string? mftIndexPath,
        bool carved, bool pairingWarning, CancellationToken token, IProgress<int>? progress = null)
    {
        if (!filter.IsAllRecords)
            return await BuildPathOrdersAsync(filter, resolve, token, progress).ConfigureAwait(false);

        return await Task.Run(async () =>
        {
            async Task<(IReadOnlyList<long> Paths, IReadOnlyList<long> Sources)> Build() =>
                await BuildPathOrdersAsync(filter, resolve, token, progress).ConfigureAwait(false);
            using var source = OpenImmutableIndex(dbPath);
            using var mft = mftIndexPath == null ? null : OpenImmutableIndex(mftIndexPath);
            if (source == null || mftIndexPath != null && mft == null)
                return await Build().ConfigureAwait(false);

            string? sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            string root = Path.GetFileName(sourceDirectory) == "v1" ? Path.GetDirectoryName(sourceDirectory)! : DeskSettings.IndexDirectory;
            string folder = Path.Combine(root, "sort-cache");
            string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dbPath).ToUpperInvariant())));
            string cache = Path.Combine(folder, name + ".sqlite");
            string? identity = null;
            int records = 0;
            try
            {
                using var db = Open(token);
                using var count = db.CreateCommand();
                count.CommandText = "SELECT records FROM summary";
                if (count.ExecuteScalar() is long value and >= 0 and <= MaxCachedRecords)
                {
                    records = (int)value;
                    string journalHash = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
                    string mftHash = mft == null ? "-" : Convert.ToHexString(await SHA256.HashDataAsync(mft, token).ConfigureAwait(false));
                    identity = $"1/{L.Code}/{carved}/{pairingWarning}/{journalHash}/{mftHash}";
                }
            }
            catch (Exception ex) when (CacheUnavailable(ex)) { }
            if (identity == null)
                return await Build().ConfigureAwait(false);

            try
            {
                if (File.Exists(cache) && ReadOrders(cache, identity, records, token) is { } saved)
                    return saved;
            }
            catch (Exception ex) when (CacheUnavailable(ex)) { }

            var orders = await Build().ConfigureAwait(false);
            if (orders.Paths.Count == records && orders.Sources.Count == records)
            {
                string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".partial");
                try
                {
                    Directory.CreateDirectory(folder);
                    WriteOrders(temporary, identity, orders, token);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, cache, true);
                }
                catch (Exception ex) when (CacheUnavailable(ex)) { }
                finally
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (Exception ex) when (CacheUnavailable(ex)) { }
                }
            }
            return orders;
        }, token).ConfigureAwait(false);
    }

    private static bool CacheUnavailable(Exception ex) => ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException;

    private static FileStream? OpenImmutableIndex(string path)
    {
        try
        {
            if (File.Exists(path + "-wal") || File.Exists(path + "-journal"))
                return null;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (CacheUnavailable(ex)) { return null; }
    }

    private static SqliteConnection OpenRankCache(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try
        {
            db.Open();
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private const int RanksPerBlock = 4096;
    private const int MaxCachedRecords = 4 * 1024 * 1024;

    private static (IReadOnlyList<long> Paths, IReadOnlyList<long> Sources)? ReadOrders(string path, string identity, int records, CancellationToken token)
    {
        using var db = OpenRankCache(path, SqliteOpenMode.ReadOnly);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT digest FROM metadata WHERE identity=$identity AND records=$records";
        cmd.Parameters.AddWithValue("$identity", identity);
        cmd.Parameters.AddWithValue("$records", records);
        if (cmd.ExecuteScalar() is not byte[] { Length: 32 } expected)
            return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var paths = new long[records];
        var sources = new long[records];
        cmd.CommandText = "SELECT data FROM ranks ORDER BY block";
        cmd.Parameters.Clear();
        using var reader = cmd.ExecuteReader();
        int start = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(records - start, RanksPerBlock);
            if (count == 0 || reader.GetBytes(0, 0, null, 0, 0) != count * 16)
                return null;
            byte[] block = (byte[])reader[0];
            hash.AppendData(block);
            for (int i = 0; i < count; i++)
            {
                paths[start + i] = BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(i * 16));
                sources[start + i] = BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(i * 16 + 8));
            }
            start += count;
        }
        return start == records && hash.GetHashAndReset().AsSpan().SequenceEqual(expected) ? (paths, sources) : null;
    }

    private static void WriteOrders(string path, string identity, (IReadOnlyList<long> Paths, IReadOnlyList<long> Sources) orders, CancellationToken token)
    {
        using var db = OpenRankCache(path, SqliteOpenMode.ReadWriteCreate);
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "CREATE TABLE ranks(block INTEGER PRIMARY KEY, data BLOB NOT NULL); CREATE TABLE metadata(identity TEXT,records INTEGER,digest BLOB)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO ranks VALUES($block,$data)";
        var number = cmd.Parameters.Add("$block", SqliteType.Integer);
        var data = cmd.Parameters.Add("$data", SqliteType.Blob);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int records = orders.Paths.Count;
        for (int start = 0; start < records;)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(records - start, RanksPerBlock);
            byte[] block = new byte[count * 16];
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(i * 16), orders.Paths[start + i]);
                BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(i * 16 + 8), orders.Sources[start + i]);
            }
            hash.AppendData(block);
            number.Value = start / RanksPerBlock;
            data.Value = block;
            cmd.ExecuteNonQuery();
            start += count;
        }
        cmd.Parameters.Clear();
        cmd.CommandText = "INSERT INTO metadata VALUES($identity,$records,$digest)";
        cmd.Parameters.AddWithValue("$identity", identity);
        cmd.Parameters.AddWithValue("$records", records);
        cmd.Parameters.AddWithValue("$digest", hash.GetHashAndReset());
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
