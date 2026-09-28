using Dapper;

namespace SkyNetwork.Site.Data;

public sealed record MemberHours(double PilotHours, double AtcHours, int PilotSessions, int AtcSessions);

public sealed class TopEntry
{
    public long Cid { get; set; }
    public string Name { get; set; } = "";
    public double Hours { get; set; }
}

/// <summary>Statistics from the connection log written by the data feed tracker.</summary>
public sealed class SessionService(Database db)
{
    private const string Seconds = "(COALESCE(ended_at, CAST(strftime('%s','now') AS INTEGER)) - started_at)";

    public MemberHours Hours(long cid)
    {
        using var c = db.Open();
        var rows = c.Query<(string Kind, double Seconds, long Count)>(
            $"SELECT kind, SUM({Seconds}) * 1.0, COUNT(*) FROM network_sessions WHERE cid = @cid GROUP BY kind", new { cid }).ToList();
        double H(string k) => rows.Where(r => r.Kind == k).Sum(r => r.Seconds) / 3600;
        int N(string k) => (int)rows.Where(r => r.Kind == k).Sum(r => r.Count);
        return new MemberHours(H("pilot"), H("atc"), N("pilot"), N("atc"));
    }

    public IReadOnlyList<NetworkSession> Recent(long cid, int limit = 25)
    {
        using var c = db.Open();
        return c.Query<NetworkSession>("SELECT * FROM network_sessions WHERE cid = @cid ORDER BY started_at DESC LIMIT @limit",
            new { cid, limit }).ToList();
    }

    /// <summary>Members with the most hours in the last <paramref name="days"/> days.</summary>
    public IReadOnlyList<TopEntry> Top(string kind, int days = 30, int limit = 10)
    {
        using var c = db.Open();
        long since = Database.Now() - days * 86400L;
        return c.Query<TopEntry>($"""
            SELECT s.cid, COALESCE(m.name, '') AS name, SUM({Seconds}) / 3600.0 AS hours
            FROM network_sessions s LEFT JOIN members m ON m.cid = s.cid
            WHERE s.kind = @kind AND s.started_at >= @since GROUP BY s.cid ORDER BY hours DESC LIMIT @limit
            """, new { kind, since, limit }).ToList();
    }

    // Logbook

    /// <summary>One page of a member's sessions, newest first, with an optional kind and year filter.</summary>
    public IReadOnlyList<NetworkSession> LogbookPage(long cid, string kind, int? year, int page, int pageSize)
    {
        using var c = db.Open();
        var (where, p) = LogbookFilter(cid, kind, year);
        p["limit"] = pageSize;
        p["offset"] = page * pageSize;
        return c.Query<NetworkSession>(
            $"SELECT * FROM network_sessions WHERE {where} ORDER BY started_at DESC LIMIT @limit OFFSET @offset", p).ToList();
    }

    /// <summary>Total session count for the logbook filter (for pagination).</summary>
    public int LogbookCount(long cid, string kind, int? year)
    {
        using var c = db.Open();
        var (where, p) = LogbookFilter(cid, kind, year);
        return c.ExecuteScalar<int>($"SELECT COUNT(*) FROM network_sessions WHERE {where}", p);
    }

    /// <summary>Distinct years that appear in the member's sessions (for the year filter drop-down).</summary>
    public IReadOnlyList<int> LogbookYears(long cid)
    {
        using var c = db.Open();
        return c.Query<int>(
            "SELECT DISTINCT CAST(strftime('%Y', datetime(started_at,'unixepoch')) AS INTEGER) AS y " +
            "FROM network_sessions WHERE cid = @cid ORDER BY y DESC", new { cid }).ToList();
    }

    /// <summary>
    /// Top entries for the member's logbook summary: top aircraft types (pilot), top routes (pilot),
    /// top positions (controller). Each list is limited to <paramref name="limit"/> entries.
    /// </summary>
    public LogbookTopStats LogbookTop(long cid, int limit = 5)
    {
        using var c = db.Open();
        // Details for pilots: "{aircraft} {dep}→{dest}" (or ""). Extract aircraft = first token before space/→.
        var aircraft = c.Query<(string Key, int Count)>("""
            SELECT SUBSTR(details, 1, CASE WHEN INSTR(details,' ') > 0 THEN INSTR(details,' ')-1 ELSE LENGTH(details) END) AS key,
                   COUNT(*) AS cnt
            FROM network_sessions
            WHERE cid = @cid AND kind = 'pilot' AND details != ''
            GROUP BY key ORDER BY cnt DESC LIMIT @limit
            """, new { cid, limit }).ToList();

        // Route: the "dep→dest" part after the first space.
        var routes = c.Query<(string Key, int Count)>("""
            SELECT CASE WHEN INSTR(details,' ') > 0
                        THEN SUBSTR(details, INSTR(details,' ')+1)
                        ELSE '' END AS key,
                   COUNT(*) AS cnt
            FROM network_sessions
            WHERE cid = @cid AND kind = 'pilot' AND details != '' AND INSTR(details,' ') > 0
            GROUP BY key ORDER BY cnt DESC LIMIT @limit
            """, new { cid, limit }).ToList();

        // Positions for controllers: details = "{freq} {rating}", take the callsign suffix for position name.
        // We group by the callsign (the position name is in the callsign, e.g. UUEE_APP).
        var positions = c.Query<(string Key, int Count)>("""
            SELECT callsign AS key, COUNT(*) AS cnt
            FROM network_sessions
            WHERE cid = @cid AND kind = 'atc'
            GROUP BY key ORDER BY cnt DESC LIMIT @limit
            """, new { cid, limit }).ToList();

        return new LogbookTopStats(
            aircraft.Where(r => r.Key.Length > 0).Select(r => (r.Key, r.Count)).ToList(),
            routes.Where(r => r.Key.Length > 0).Select(r => (r.Key, r.Count)).ToList(),
            positions.Select(r => (r.Key, r.Count)).ToList());
    }

    private static (string Where, Dictionary<string, object?> Params) LogbookFilter(long cid, string kind, int? year)
    {
        var parts = new List<string> { "cid = @cid" };
        var p = new Dictionary<string, object?> { ["cid"] = cid };
        if (kind is "pilot" or "atc") { parts.Add("kind = @kind"); p["kind"] = kind; }
        if (year is { } y)
        {
            // Use unix timestamps for the year boundary — index-friendly.
            long lo = new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            long hi = new DateTimeOffset(y + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            parts.Add("started_at >= @yearLo AND started_at < @yearHi");
            p["yearLo"] = lo;
            p["yearHi"] = hi;
        }
        return (string.Join(" AND ", parts), p);
    }

    public (int Today, int Month) SessionCounts()
    {
        using var c = db.Open();
        long now = Database.Now();
        long day = now - now % 86400;
        return (c.ExecuteScalar<int>("SELECT COUNT(*) FROM network_sessions WHERE started_at >= @day", new { day }),
                c.ExecuteScalar<int>("SELECT COUNT(*) FROM network_sessions WHERE started_at >= @since", new { since = now - 30 * 86400L }));
    }
}
