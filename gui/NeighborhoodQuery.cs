using System.IO;
using Microsoft.Data.Sqlite;

namespace JournalDesk;

internal sealed record NeighborhoodCounts(long Records, long Files, long AnchorIndex);
internal sealed record NeighborhoodPage(NeighborhoodCounts Counts, long Number, IReadOnlyList<GlobalLogRow> Rows);

internal sealed class NeighborhoodQuery(string database)
{
    private const int DefaultPageSize = 200;
    private readonly GlobalLogQuery index = new(database);

    public Task<NeighborhoodPage> ReadAsync(Entry anchor, int radius, JournalTimeZone utc, CancellationToken token,
        long? page = null, NeighborhoodCounts? previousCounts = null, int pageSize = DefaultPageSize)
    {
        bool timed = anchor.Timestamp.HasValue;
        if (timed ? radius is not (1 or 5 or 30) : radius is not (5 or 25 or 100))
            throw new ArgumentOutOfRangeException(nameof(radius));

        return Task.Run(() =>
        {
            using var db = index.Open(token);
            using var cmd = db.CreateCommand();
            cmd.Parameters.AddWithValue("$pos", anchor.Position);
            cmd.Parameters.AddWithValue("$usn", anchor.Usn);
            string membership;
            string order;
            string beforeAnchor;
            if (anchor.Timestamp is long stamp)
            {
                long span = radius * TimeSpan.TicksPerSecond;
                long from = stamp < long.MinValue + span ? long.MinValue : stamp - span;
                cmd.Parameters.AddWithValue("$stamp", stamp);
                cmd.Parameters.AddWithValue("$from", from);
                string upper = "";
                if (stamp <= long.MaxValue - span)
                {
                    cmd.Parameters.AddWithValue("$until", stamp + span);
                    upper = "AND stamp < $until";
                }
                membership = $"SELECT pos,usn,stamp,file FROM records WHERE stamp >= $from {upper}";
                order = "stamp,usn,pos";
                beforeAnchor = "(stamp,usn,pos) < ($stamp,$usn,$pos)";
            }
            else
            {
                cmd.Parameters.AddWithValue("$segment", anchor.Segment);
                cmd.Parameters.AddWithValue("$radius", radius);
                membership = """
                    WITH preceding AS (
                        SELECT pos,usn,stamp,file FROM records
                        WHERE segment=$segment AND (usn,pos)<($usn,$pos)
                        ORDER BY usn DESC,pos DESC LIMIT $radius
                    ), following AS (
                        SELECT pos,usn,stamp,file FROM records
                        WHERE segment=$segment AND (usn,pos)>($usn,$pos)
                        ORDER BY usn,pos LIMIT $radius
                    )
                    SELECT * FROM preceding UNION ALL
                    SELECT pos,usn,stamp,file FROM records WHERE segment=$segment AND pos=$pos UNION ALL
                    SELECT * FROM following
                    """;
                order = "usn,pos";
                beforeAnchor = "(usn,pos) < ($usn,$pos)";
            }

            var counts = previousCounts ?? Count(cmd, membership, beforeAnchor);
            long lastPage = (counts.Records - 1) / pageSize;
            long number = Math.Clamp(page ?? counts.AnchorIndex / pageSize, 0, lastPage);
            cmd.Parameters.AddWithValue("$offset", number * pageSize);
            cmd.Parameters.AddWithValue("$take", pageSize);
            cmd.CommandText = $"""
                WITH matches AS ({membership}), page AS MATERIALIZED (
                    SELECT pos FROM matches ORDER BY {order} LIMIT $take OFFSET $offset
                )
                SELECT {GlobalLogQuery.RecordProjection}, v.*
                FROM page p JOIN records r ON r.pos=p.pos LEFT JOIN range_records v ON v.pos=r.pos
                ORDER BY {(timed ? "r.stamp," : "")}r.usn,r.pos
                """;
            using var reader = cmd.ExecuteReader();
            var rows = new List<GlobalLogRow>(pageSize);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                rows.Add(GlobalLogQuery.Row(reader, utc));
            }
            token.ThrowIfCancellationRequested();
            return new NeighborhoodPage(counts, number, rows);
        }, token).ContinueOnInterrupt(token);
    }

    private static NeighborhoodCounts Count(SqliteCommand cmd, string membership, string beforeAnchor)
    {
        cmd.CommandText = $"""
            WITH matches AS ({membership})
            SELECT count(*),count(DISTINCT file),coalesce(sum({beforeAnchor}),0),coalesce(max(pos=$pos),0)
            FROM matches
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(3) != 1)
            throw new InvalidDataException(L.T("현재 색인에서 기준 기록을 확인할 수 없습니다.", "The reference record is unavailable in this index."));
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}
