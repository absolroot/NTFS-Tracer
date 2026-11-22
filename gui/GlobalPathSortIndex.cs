using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace JournalDesk;

public sealed partial class GlobalLogQuery
{
    internal Task<(IReadOnlyList<long> Paths, IReadOnlyList<long> Sources)> BuildPathOrdersAsync(
        GlobalLogFilter filter, Func<GlobalLogRow, (string Path, string Source)> resolve, CancellationToken token,
        IProgress<int>? progress = null) =>
        Task.Run<(IReadOnlyList<long>, IReadOnlyList<long>)>(() =>
        {
            var paths = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var interval = Stopwatch.StartNew();
            int count = 0;
            using var db = Open(token);
            using var command = db.CreateCommand();
            command.CommandText = $"""
                SELECT {RecordProjection},v.* FROM records r LEFT JOIN range_records v ON v.pos=r.pos
                {FilterClause(command, filter, token)} ORDER BY r.pos
                """;
            try
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var row = Row(reader, false);
                    var keys = resolve(row);
                    AddOffset(paths, keys.Path, row.Position);
                    AddOffset(sources, keys.Source, row.Position);
                    count++;
                    if (progress != null && interval.ElapsedMilliseconds >= 250)
                    {
                        progress.Report(count);
                        interval.Restart();
                    }
                }
            }
            catch (SqliteException) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            token.ThrowIfCancellationRequested();
            progress?.Report(count);
            return (Order(paths, count, token), Order(sources, count, token));
        }, token);

    internal Task<(IReadOnlyList<long> Paths, IReadOnlyList<long> Sources)> FilterPathOrdersAsync(
        GlobalLogFilter filter, (IReadOnlyList<long> Paths, IReadOnlyList<long> Sources) all, CancellationToken token) =>
        Task.Run<(IReadOnlyList<long>, IReadOnlyList<long>)>(() =>
        {
            using var db = Open(token);
            using var command = db.CreateCommand();
            command.CommandText = $"SELECT pos FROM records r {FilterClause(command, filter, token)}";
            using var reader = command.ExecuteReader();
            var matching = new HashSet<long>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                matching.Add(reader.GetInt64(0));
            }
            List<long> Filter(IReadOnlyList<long> order)
            {
                var result = new List<long>(matching.Count);
                foreach (var position in order)
                {
                    token.ThrowIfCancellationRequested();
                    if (matching.Contains(position))
                    {
                        result.Add(position);
                        if (result.Count == matching.Count)
                            break;
                    }
                }
                return result;
            }
            return matching.Count == 0 ? (Array.Empty<long>(), Array.Empty<long>()) : (Filter(all.Paths), Filter(all.Sources));
        }, token).ContinueOnInterrupt(token);

    private static void AddOffset(Dictionary<string, List<long>> groups, string key, long position)
    {
        if (!groups.TryGetValue(key, out var offsets))
            groups.Add(key, offsets = []);
        offsets.Add(position);
    }

    private static List<long> Order(Dictionary<string, List<long>> groups, int count, CancellationToken token)
    {
        var order = new List<long>(count);
        foreach (var group in groups.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            order.AddRange(group.Value);
        }
        token.ThrowIfCancellationRequested();
        return order;
    }

}
