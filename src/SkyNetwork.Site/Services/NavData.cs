using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Services;

/// <summary>A point of a drawn route: a waypoint, navaid, airport or coordinate, with the airway that led to it ("" for direct).</summary>
public readonly record struct RoutePoint(string Ident, double Lat, double Lon, string Airway, int Altitude);

/// <summary>
/// Waypoints and airways for drawing the routes of flight plans filed without SimBrief. Two sources: a bundled world
/// set (Nav/fixes.dat.gz and Nav/airways.dat.gz, built by tools/airac-import, see Nav/README.txt) and everything seen in the SimBrief plans that
/// members import, kept in the site's database — so the data gets fresher the more the network is used.
/// </summary>
public sealed partial class NavData(Database db, IWebHostEnvironment env, ILogger<NavData> log)
{
    /// <summary>
    /// Leaving a token out of the drawn route costs this much: a fix is kept only when it adds less of a detour.
    /// The same name exists in several places in the world; the wrong one would add hundreds of miles.
    /// </summary>
    private const double SkipCostNm = 180;
    /// <summary>Airways are the truth when they join the fixes: their path counts a little shorter than the straight line.</summary>
    private const double AirwayFactor = 0.9;
    /// <summary>Candidates further than this from the previous point are not even considered.</summary>
    private const double MaxLegNm = 2500;
    /// <summary>Airway segment ends this close together are the same fix.</summary>
    private const double SameFixNm = 2;

    [GeneratedRegex(@"^[A-Z]{1,2}\d{1,4}[A-Z]?$")] private static partial Regex AirwayName();
    [GeneratedRegex(@"^[A-Z]{3,5}\d[A-Z]{0,2}$")] private static partial Regex SidStar();
    [GeneratedRegex(@"^[NMK]\d{3,4}[FSAM]\d{3,4}$")] private static partial Regex SpeedLevel();
    [GeneratedRegex(@"^(\d{2})(\d{2})?([NS])(\d{3})(\d{2})?([EW])$")] private static partial Regex Coordinate();
    [GeneratedRegex(@"^[A-Z0-9]{1,6}$")] private static partial Regex FixName();

    public static bool IsAirway(string s) => AirwayName().IsMatch(s);

    private sealed record Segment(string A, double ALat, double ALon, string B, double BLat, double BLon);

    private sealed record BundledData(
        Dictionary<string, List<(double Lat, double Lon)>> Fixes,
        Dictionary<string, List<Segment>> Airways,
        Dictionary<string, (double Lat, double Lon)> Airports);

    // The bundled files are read once per process, whichever site instance asks first (tests create several).
    private static readonly object BundledLock = new();
    private static BundledData? _bundledCache;
    private static string? _bundledRoot;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<(double Lat, double Lon)>> _learnedFixes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Segment>> _learnedAirways = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AirwayGraph?> _graphs = new(StringComparer.Ordinal);
    private bool _learnedLoaded;

    public (int Fixes, int Airways, int LearnedFixes, int LearnedAirways) Counts
    {
        get
        {
            var b = Bundled;
            lock (_lock) { EnsureLearned(); return (b.Fixes.Count, b.Airways.Count, _learnedFixes.Count, _learnedAirways.Count); }
        }
    }

    // One token of the route worth drawing: a fix (maybe reached along an airway) or a coordinate.
    private sealed record Step(string Token, string? ViaAirway, (double Lat, double Lon)? Coordinate);

    // A way of placing the route up to some token: where it ends, what it cost, what it drew.
    private sealed class State
    {
        public (double Lat, double Lon)? Pos;
        public string Ident = "";
        public double Cost;
        public State? Prev;
        public List<RoutePoint> Added = [];
        public string? Skipped;
    }

    /// <summary>
    /// The route as points: airports, fixes, navaids and coordinates in order, airways expanded fix by fix. Names that
    /// exist in several places get the one that fits the rest of the route; a fix that would only add a detour, and any
    /// token nothing is known about, is left out and returned in <c>Unresolved</c>.
    /// </summary>
    public (List<RoutePoint> Points, List<string> Unresolved) Decode(string departure, string destination, string route)
    {
        var b = Bundled;
        departure = departure.ToUpperInvariant();
        destination = destination.ToUpperInvariant();
        (double Lat, double Lon)? depPos = b.Airports.TryGetValue(departure, out var dp) ? dp : null;
        (double Lat, double Lon)? destPos = b.Airports.TryGetValue(destination, out var ap) ? ap : null;
        var unresolved = new List<string>();
        var points = new List<RoutePoint>();

        var tokens = route.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Split('/')[0]).Where(t => t.Length > 0).ToList();
        lock (_lock)
        {
            EnsureLearned();

            // 1. Tokens worth drawing, with the airway that leads to each fix; speeds, procedures and noise dropped.
            var steps = new List<Step>();
            string? via = null;
            foreach (var tok in tokens)
            {
                if (tok == departure || tok == destination || tok is "DCT" or "DIRECT" or "SID" or "STAR" or "IFR" or "VFR") { via = null; continue; }
                if (SpeedLevel().IsMatch(tok)) continue;
                if (TryCoordinate(tok, out var pos)) { steps.Add(new Step(tok, null, pos)); via = null; continue; }
                if (HasAirway(tok) && steps.Count > 0 && steps[^1].Coordinate == null) { via = tok; continue; }
                if (IsAirway(tok) && !HasFix(tok)) { via = null; continue; }
                if (SidStar().IsMatch(tok) && !HasFix(tok)) continue;
                if (!HasFix(tok) && !(tok.Length == 4 && b.Airports.ContainsKey(tok))) { unresolved.Add(tok); via = null; continue; }
                steps.Add(new Step(tok, via, null));
                via = null;
            }

            // 2. The cheapest placement of all steps together (shortest total path, a penalty for every step left out).
            var states = new List<State>
            {
                new() { Pos = depPos, Ident = departure, Added = depPos is { } d0 ? [new RoutePoint(departure, d0.Lat, d0.Lon, "", 0)] : [] },
            };
            foreach (var step in steps)
            {
                var next = new List<State>();
                foreach (var s in states)
                {
                    if (step.Coordinate is { } c)
                    {
                        next.Add(new State { Pos = c, Ident = step.Token, Cost = s.Cost + Leg(s.Pos, c), Prev = s, Added = [new RoutePoint(step.Token, c.Lat, c.Lon, "", 0)] });
                        continue;
                    }
                    if (step.ViaAirway != null && s.Pos is { } from && s.Ident != departure)
                    {
                        var along = Expand(step.ViaAirway, new RoutePoint(s.Ident, from.Lat, from.Lon, "", 0), step.Token);
                        if (along != null)
                        {
                            double length = Leg(from, (along[0].Lat, along[0].Lon));
                            for (int k = 1; k < along.Count; k++) length += Distance(along[k - 1].Lat, along[k - 1].Lon, along[k].Lat, along[k].Lon);
                            next.Add(new State { Pos = (along[^1].Lat, along[^1].Lon), Ident = step.Token, Cost = s.Cost + length * AirwayFactor, Prev = s, Added = along });
                        }
                    }
                    foreach (var cand in Candidates(step.Token))
                    {
                        if (s.Pos is { } p && Distance(p.Lat, p.Lon, cand.Lat, cand.Lon) > MaxLegNm) continue;
                        next.Add(new State { Pos = cand, Ident = step.Token, Cost = s.Cost + Leg(s.Pos, cand), Prev = s, Added = [new RoutePoint(step.Token, cand.Lat, cand.Lon, "", 0)] });
                    }
                    next.Add(new State { Pos = s.Pos, Ident = s.Ident, Cost = s.Cost + SkipCostNm, Prev = s, Skipped = step.Token });
                }
                // One state per place is enough: the cheapest way of getting there.
                states = next.GroupBy(x => (x.Ident, Math.Round(x.Pos?.Lat ?? 999, 1), Math.Round(x.Pos?.Lon ?? 999, 1)))
                    .Select(g => g.MinBy(x => x.Cost)!).ToList();
            }

            var best = states.MinBy(s => s.Cost + (destPos is { } dd ? Leg(s.Pos, dd) : 0));
            var chain = new List<State>();
            for (var s = best; s != null; s = s.Prev) chain.Add(s);
            chain.Reverse();
            var skipped = new List<string>();
            foreach (var s in chain)
            {
                points.AddRange(s.Added);
                if (s.Skipped != null) skipped.Add(s.Skipped);
            }
            unresolved = tokens.Where(t => unresolved.Contains(t) || skipped.Contains(t)).Distinct().ToList();
        }
        if (destPos is { } dest && (points.Count == 0 || points[^1].Ident != destination)) points.Add(new RoutePoint(destination, dest.Lat, dest.Lon, "", 0));
        return (points, unresolved);
    }

    private static double Leg((double Lat, double Lon)? from, (double Lat, double Lon) to) =>
        from is { } f ? Distance(f.Lat, f.Lon, to.Lat, to.Lon) : 0;

    /// <summary>Keeps the fixes and airway segments of an imported SimBrief route for later routes.</summary>
    public void Learn(IReadOnlyList<RoutePoint> route, string departure, string destination)
    {
        var newFixes = new List<RoutePoint>();
        var newSegments = new List<(string Name, RoutePoint A, RoutePoint B)>();
        lock (_lock)
        {
            EnsureLearned();
            for (int i = 0; i < route.Count; i++)
            {
                var p = route[i];
                string ident = p.Ident.ToUpperInvariant();
                if (ident == departure.ToUpperInvariant() || ident == destination.ToUpperInvariant() || ident is "TOC" or "TOD") continue;
                if (!FixName().IsMatch(ident) || Coordinate().IsMatch(ident) || Math.Abs(p.Lat) > 90 || Math.Abs(p.Lon) > 180) continue;
                if (!_learnedFixes.TryGetValue(ident, out var known)) _learnedFixes[ident] = known = [];
                if (!known.Any(k => Distance(k.Lat, k.Lon, p.Lat, p.Lon) < 1))
                {
                    known.Add((p.Lat, p.Lon));
                    newFixes.Add(p with { Ident = ident });
                }
                if (i > 0 && IsAirway(p.Airway) && route[i - 1].Ident != departure)
                {
                    var a = route[i - 1];
                    if (!_learnedAirways.TryGetValue(p.Airway, out var segs)) _learnedAirways[p.Airway] = segs = [];
                    if (!segs.Any(s => s.A == a.Ident && s.B == ident || s.A == ident && s.B == a.Ident))
                    {
                        segs.Add(new Segment(a.Ident, a.Lat, a.Lon, ident, p.Lat, p.Lon));
                        newSegments.Add((p.Airway, a, p with { Ident = ident }));
                        _graphs.Remove(p.Airway);
                    }
                }
            }
        }
        if (newFixes.Count == 0 && newSegments.Count == 0) return;
        try
        {
            using var c = db.Open();
            using var tx = c.BeginTransaction();
            long now = Database.Now();
            foreach (var f in newFixes)
                c.Execute("INSERT OR IGNORE INTO nav_fixes (ident, lat, lon, seen_at) VALUES (@Ident, @Lat, @Lon, @now)",
                    new { f.Ident, Lat = Math.Round(f.Lat, 4), Lon = Math.Round(f.Lon, 4), now }, tx);
            foreach (var (name, a, bb) in newSegments)
                c.Execute("""
                    INSERT OR IGNORE INTO nav_airways (name, a, a_lat, a_lon, b, b_lat, b_lon, seen_at)
                    VALUES (@name, @a, @aLat, @aLon, @b, @bLat, @bLon, @now)
                    """, new { name, a = a.Ident, aLat = Math.Round(a.Lat, 4), aLon = Math.Round(a.Lon, 4), b = bb.Ident, bLat = Math.Round(bb.Lat, 4), bLon = Math.Round(bb.Lon, 4), now }, tx);
            tx.Commit();
            log.LogInformation("Learned {Fixes} fixes and {Segments} airway segments from a SimBrief route", newFixes.Count, newSegments.Count);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            log.LogWarning("Nav data not saved: {Error}", e.Message);
        }
    }

    // ---- lookups ----

    private BundledData Bundled
    {
        get
        {
            lock (BundledLock)
            {
                if (_bundledCache == null || _bundledRoot != env.ContentRootPath)
                {
                    _bundledCache = Load(env.ContentRootPath, log);
                    _bundledRoot = env.ContentRootPath;
                }
                return _bundledCache;
            }
        }
    }

    private void EnsureLearned()
    {
        if (_learnedLoaded) return;
        _learnedLoaded = true;
        try
        {
            using var c = db.Open();
            foreach (var f in c.Query<(string Ident, double Lat, double Lon)>("SELECT ident, lat, lon FROM nav_fixes"))
            {
                if (!_learnedFixes.TryGetValue(f.Ident, out var list)) _learnedFixes[f.Ident] = list = [];
                list.Add((f.Lat, f.Lon));
            }
            foreach (var s in c.Query<(string Name, string A, double ALat, double ALon, string B, double BLat, double BLon)>(
                         "SELECT name, a, a_lat, a_lon, b, b_lat, b_lon FROM nav_airways"))
            {
                if (!_learnedAirways.TryGetValue(s.Name, out var list)) _learnedAirways[s.Name] = list = [];
                list.Add(new Segment(s.A, s.ALat, s.ALon, s.B, s.BLat, s.BLon));
            }
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            log.LogWarning("Nav data not loaded: {Error}", e.Message);
        }
    }

    private bool HasAirway(string name) => _learnedAirways.ContainsKey(name) || Bundled.Airways.ContainsKey(name);
    private bool HasFix(string ident) => _learnedFixes.ContainsKey(ident) || Bundled.Fixes.ContainsKey(ident);

    // Learned positions first: they are the current ones.
    private List<(double Lat, double Lon)> Candidates(string ident)
    {
        var list = new List<(double, double)>();
        if (_learnedFixes.TryGetValue(ident, out var learned)) list.AddRange(learned);
        if (Bundled.Fixes.TryGetValue(ident, out var bundled)) list.AddRange(bundled);
        if (ident.Length == 4 && Bundled.Airports.TryGetValue(ident, out var apt)) list.Add(apt);
        return list;
    }

    private static bool TryCoordinate(string t, out (double Lat, double Lon) pos)
    {
        pos = default;
        var m = Coordinate().Match(t);
        if (!m.Success) return false;
        double lat = int.Parse(m.Groups[1].Value) + (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) / 60.0 : 0);
        double lon = int.Parse(m.Groups[4].Value) + (m.Groups[5].Success ? int.Parse(m.Groups[5].Value) / 60.0 : 0);
        if (lat > 90 || lon > 180) return false;
        pos = (m.Groups[3].Value == "S" ? -lat : lat, m.Groups[6].Value == "W" ? -lon : lon);
        return true;
    }

    // ---- airways ----

    private sealed class AirwayGraph
    {
        public sealed class Node(string ident, double lat, double lon)
        {
            public readonly string Ident = ident;
            public readonly double Lat = lat, Lon = lon;
            public readonly List<Node> Links = [];
        }

        public readonly Dictionary<string, List<Node>> ByIdent = new(StringComparer.Ordinal);

        private Node NodeFor(string ident, double lat, double lon)
        {
            if (!ByIdent.TryGetValue(ident, out var list)) ByIdent[ident] = list = [];
            foreach (var n in list) if (Distance(n.Lat, n.Lon, lat, lon) < SameFixNm) return n;
            var node = new Node(ident, lat, lon);
            list.Add(node);
            return node;
        }

        public void Add(Segment s)
        {
            var a = NodeFor(s.A, s.ALat, s.ALon);
            var b = NodeFor(s.B, s.BLat, s.BLon);
            if (a == b) return;
            if (!a.Links.Contains(b)) a.Links.Add(b);
            if (!b.Links.Contains(a)) b.Links.Add(a);
        }
    }

    private AirwayGraph? Graph(string name)
    {
        if (_graphs.TryGetValue(name, out var cached)) return cached;
        AirwayGraph? g = null;
        foreach (var source in new[] { _learnedAirways, Bundled.Airways })
            if (source.TryGetValue(name, out var segs))
            {
                g ??= new AirwayGraph();
                foreach (var s in segs) g.Add(s);
            }
        return _graphs[name] = g;
    }

    /// <summary>The fixes along an airway after the entry fix up to and including the exit fix; null when the airway does not join them.</summary>
    private List<RoutePoint>? Expand(string airway, RoutePoint from, string exit)
    {
        var g = Graph(airway);
        if (g == null || !g.ByIdent.TryGetValue(from.Ident, out var entries) || !g.ByIdent.ContainsKey(exit)) return null;
        var start = entries.MinBy(n => Distance(n.Lat, n.Lon, from.Lat, from.Lon))!;
        if (Distance(start.Lat, start.Lon, from.Lat, from.Lon) > SameFixNm * 5) return null;
        var previous = new Dictionary<AirwayGraph.Node, AirwayGraph.Node?> { [start] = null };
        var queue = new Queue<AirwayGraph.Node>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n.Ident == exit && n != start)
            {
                var path = new List<RoutePoint>();
                for (var at = n; at != null && at != start; at = previous[at]) path.Add(new RoutePoint(at.Ident, at.Lat, at.Lon, airway, 0));
                path.Reverse();
                return path;
            }
            if (previous.Count > 2000) break;
            foreach (var next in n.Links)
                if (!previous.ContainsKey(next)) { previous[next] = n; queue.Enqueue(next); }
        }
        return null;
    }

    // ---- bundled files ----

    private static BundledData Load(string root, ILogger log)
    {
        var fixes = new Dictionary<string, List<(double, double)>>(StringComparer.Ordinal);
        var airways = new Dictionary<string, List<Segment>>(StringComparer.Ordinal);
        var airports = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        var inv = CultureInfo.InvariantCulture;
        try
        {
            foreach (var line in Lines(Path.Combine(root, "Nav", "fixes.dat.gz")))
            {
                var f = line.Split(' ');
                if (f.Length != 3) continue;
                if (!fixes.TryGetValue(f[0], out var list)) fixes[f[0]] = list = [];
                list.Add((double.Parse(f[1], inv), double.Parse(f[2], inv)));
            }
            foreach (var line in Lines(Path.Combine(root, "Nav", "airways.dat.gz")))
            {
                var f = line.Split(' ');
                if (f.Length != 7) continue;
                if (!airways.TryGetValue(f[0], out var list)) airways[f[0]] = list = [];
                list.Add(new Segment(f[1], double.Parse(f[2], inv), double.Parse(f[3], inv), f[4], double.Parse(f[5], inv), double.Parse(f[6], inv)));
            }
            // VOR and NDB (OurAirports): the same file the map uses.
            string navaids = Path.Combine(root, "wwwroot", "data", "navaids.json");
            if (File.Exists(navaids))
                using (var doc = JsonDocument.Parse(File.ReadAllText(navaids)))
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (!fixes.TryGetValue(p.Name, out var list)) fixes[p.Name] = list = [];
                        foreach (var pos in p.Value.EnumerateArray()) list.Add((pos[0].GetDouble(), pos[1].GetDouble()));
                    }
            string apts = Path.Combine(root, "wwwroot", "data", "airports.json");
            if (File.Exists(apts))
                using (var doc = JsonDocument.Parse(File.ReadAllText(apts)))
                    foreach (var p in doc.RootElement.EnumerateObject())
                        airports[p.Name] = (p.Value[0].GetDouble(), p.Value[1].GetDouble());
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException)
        {
            log.LogWarning("Nav data files: {Error}", e.Message);
        }
        log.LogInformation("Nav data: {Fixes} fixes, {Airways} airways, {Airports} airports", fixes.Count, airways.Count, airports.Count);
        return new BundledData(fixes, airways, airports);
    }

    private static IEnumerable<string> Lines(string gzipPath)
    {
        if (!File.Exists(gzipPath)) yield break;
        using var file = File.OpenRead(gzipPath);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private const double Rad = Math.PI / 180;

    /// <summary>Nautical miles.</summary>
    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Rad, dLon = (lon2 - lon1) * Rad;
        double a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * 3440.065 * Math.Asin(Math.Sqrt(Math.Min(1, a)));
    }

    // ---- route finder ----

    /// <summary>Result of <see cref="FindRoute"/>: an airway route or a DCT segment.</summary>
    public sealed record FindRouteResult(
        string Route,
        List<RoutePoint> Points,
        double DistanceNm,
        bool IsDirect);

    /// <summary>
    /// Finds the shortest airway route between two airports using A* over the global airway graph.
    /// Falls back to a direct (DCT) route if no airway path is found.
    /// The route string is in ICAO format (e.g. "GUBAG N869 RATIN DCT LEPTA").
    /// </summary>
    public FindRouteResult FindRoute(string departure, string destination)
    {
        departure = departure.ToUpperInvariant();
        destination = destination.ToUpperInvariant();
        var b = Bundled;

        (double Lat, double Lon)? depPos = b.Airports.TryGetValue(departure, out var dp) ? dp : null;
        (double Lat, double Lon)? destPos = b.Airports.TryGetValue(destination, out var ap) ? ap : null;

        if (depPos == null || destPos == null)
        {
            // Unknown airport(s): just DCT
            double dist = depPos != null && destPos != null
                ? Distance(depPos.Value.Lat, depPos.Value.Lon, destPos.Value.Lat, destPos.Value.Lon) : 0;
            var pts = BuildDirectPoints(departure, destination, depPos, destPos);
            return new FindRouteResult("DCT", pts, dist, true);
        }

        double totalDist = Distance(depPos.Value.Lat, depPos.Value.Lon, destPos.Value.Lat, destPos.Value.Lon);

        lock (_lock)
        {
            EnsureLearned();
            var graph = BuildOrGetGlobalGraph(b);
            var result = AStarRoute(departure, depPos.Value, destination, destPos.Value, graph, totalDist);
            if (result != null) return result;
        }

        // No airway route found: DCT
        var directPts = BuildDirectPoints(departure, destination, depPos, destPos);
        return new FindRouteResult("DCT", directPts, totalDist, true);
    }

    // Global graph for the route finder. Built lazily; invalidated when learned data grows.
    private GlobalGraph? _globalGraph;
    private int _globalGraphLearnedCount;

    private sealed class GlobalGraph
    {
        public sealed class Node(string ident, double lat, double lon)
        {
            public readonly string Ident = ident;
            public readonly double Lat = lat, Lon = lon;
            // (neighbour, geographic distance NM, airway name)
            public readonly List<(Node Nb, double Dist, string Airway)> Links = [];
        }

        // Key: rounded position → node (same ident different position = different node)
        public readonly Dictionary<long, Node> ByPos = new();
        // ident → all nodes with that name
        public readonly Dictionary<string, List<Node>> ByIdent = new(StringComparer.Ordinal);

        private static long Key(double lat, double lon) =>
            ((long)(Math.Round(lat, 2) * 100 + 9000) << 20) | (long)(Math.Round(lon, 2) * 100 + 18000);

        public Node GetOrAdd(string ident, double lat, double lon)
        {
            long k = Key(lat, lon);
            if (!ByPos.TryGetValue(k, out var n))
            {
                n = new Node(ident, lat, lon);
                ByPos[k] = n;
                if (!ByIdent.TryGetValue(ident, out var list)) ByIdent[ident] = list = [];
                list.Add(n);
            }
            return n;
        }

        public void AddSegment(string airway, Segment s)
        {
            var a = GetOrAdd(s.A, s.ALat, s.ALon);
            var b = GetOrAdd(s.B, s.BLat, s.BLon);
            if (a == b) return;
            double dist = Distance(a.Lat, a.Lon, b.Lat, b.Lon);
            if (!a.Links.Any(x => x.Nb == b)) a.Links.Add((b, dist, airway));
            if (!b.Links.Any(x => x.Nb == a)) b.Links.Add((a, dist, airway));
        }
    }

    private GlobalGraph BuildOrGetGlobalGraph(BundledData b)
    {
        int total = _learnedAirways.Sum(kv => kv.Value.Count);
        if (_globalGraph != null && _globalGraphLearnedCount == total) return _globalGraph;

        var g = new GlobalGraph();
        foreach (var (name, segs) in b.Airways)
            foreach (var s in segs) g.AddSegment(name, s);
        foreach (var (name, segs) in _learnedAirways)
            foreach (var s in segs) g.AddSegment(name, s);

        _globalGraph = g;
        _globalGraphLearnedCount = total;
        return g;
    }

    private const double NearAirportNm = 40;
    private const int AStarMaxNodes = 200_000;

    private FindRouteResult? AStarRoute(
        string depIdent, (double Lat, double Lon) depPos,
        string destIdent, (double Lat, double Lon) destPos,
        GlobalGraph graph, double totalDistNm)
    {
        // Collect entry fixes near departure and exit fixes near destination
        var entryNodes = NearbyNodes(graph, depPos.Lat, depPos.Lon, NearAirportNm);
        var exitNodes = NearbyNodes(graph, destPos.Lat, destPos.Lon, NearAirportNm);
        if (entryNodes.Count == 0 || exitNodes.Count == 0) return null;

        var exitSet = new HashSet<GlobalGraph.Node>(exitNodes);

        // A* — cost: NM flown; heuristic: great-circle to destination
        var dist = new Dictionary<GlobalGraph.Node, double>();
        var prev = new Dictionary<GlobalGraph.Node, (GlobalGraph.Node? From, double EntryDist, string Airway)>();
        var pq = new PriorityQueue<GlobalGraph.Node, double>();

        foreach (var n in entryNodes)
        {
            double g = Distance(depPos.Lat, depPos.Lon, n.Lat, n.Lon);
            double h = Distance(n.Lat, n.Lon, destPos.Lat, destPos.Lon);
            dist[n] = g;
            prev[n] = (null, g, "");
            pq.Enqueue(n, g + h);
        }

        GlobalGraph.Node? goal = null;
        int explored = 0;

        while (pq.Count > 0 && explored < AStarMaxNodes)
        {
            var cur = pq.Dequeue();
            if (exitSet.Contains(cur)) { goal = cur; break; }
            double curCost = dist.TryGetValue(cur, out var c) ? c : double.MaxValue;
            explored++;

            foreach (var (nb, segDist, airway) in cur.Links)
            {
                double newCost = curCost + segDist;
                if (dist.TryGetValue(nb, out var oldCost) && oldCost <= newCost) continue;
                dist[nb] = newCost;
                prev[nb] = (cur, newCost, airway);
                double h = Distance(nb.Lat, nb.Lon, destPos.Lat, destPos.Lon);
                pq.Enqueue(nb, newCost + h);
            }
        }

        if (goal == null) return null;

        // Reconstruct path
        var path = new List<(GlobalGraph.Node Node, string Airway)>();
        for (var at = goal; at != null;)
        {
            var (fromNode, _, airway) = prev[at];
            path.Add((at, airway));
            at = fromNode;
        }
        path.Reverse();

        // Build points list and compute total distance
        double totalNm = Distance(depPos.Lat, depPos.Lon, path[0].Node.Lat, path[0].Node.Lon);
        for (int i = 1; i < path.Count; i++)
            totalNm += Distance(path[i - 1].Node.Lat, path[i - 1].Node.Lon, path[i].Node.Lat, path[i].Node.Lon);
        totalNm += Distance(path[^1].Node.Lat, path[^1].Node.Lon, destPos.Lat, destPos.Lon);

        var points = new List<RoutePoint>();
        points.Add(new RoutePoint(depIdent, depPos.Lat, depPos.Lon, "", 0));
        foreach (var (node, airway) in path)
            points.Add(new RoutePoint(node.Ident, node.Lat, node.Lon, airway, 0));
        points.Add(new RoutePoint(destIdent, destPos.Lat, destPos.Lon, "", 0));

        string route = BuildRouteString(path, depIdent, destIdent);
        return new FindRouteResult(route, points, totalNm, false);
    }

    /// <summary>
    /// Build an ICAO route string from a reconstructed A* path.
    /// path[i].Airway = the airway used on the edge from path[i-1] to path[i] ("" = direct).
    /// Produces: ENTRY_FIX AIRWAY EXIT_FIX ... with DCT between non-airway legs.
    /// </summary>
    private static string BuildRouteString(List<(GlobalGraph.Node Node, string Airway)> path, string dep, string dest)
    {
        if (path.Count == 0) return "DCT";
        var parts = new List<string>();
        string? lastEmitted = null;

        int i = 0;
        while (i < path.Count)
        {
            string airway = path[i].Airway;

            if (airway.Length == 0)
            {
                // Direct leg to path[i]: emit it if not DEP or DEST
                string fix = path[i].Node.Ident;
                if (fix != dep && fix != dest) { parts.Add(fix); lastEmitted = fix; }
                i++;
                continue;
            }

            // Find the full run of this airway
            int j = i;
            while (j < path.Count && path[j].Airway == airway) j++;
            // path[i-1] is the entry fix into the airway (or DEP if i==0)
            string entryFix = i > 0 ? path[i - 1].Node.Ident : dep;
            string exitFix = path[j - 1].Node.Ident;

            // Emit entry fix if not already the last thing we emitted
            if (entryFix != dep && entryFix != dest && entryFix != lastEmitted)
            {
                if (lastEmitted != null) parts.Add("DCT");
                parts.Add(entryFix);
            }
            parts.Add(airway);
            if (exitFix != dep && exitFix != dest) { parts.Add(exitFix); lastEmitted = exitFix; }
            i = j;
        }
        return parts.Count > 0 ? string.Join(" ", parts) : "DCT";
    }

    private static List<GlobalGraph.Node> NearbyNodes(GlobalGraph g, double lat, double lon, double maxNm)
    {
        var result = new List<GlobalGraph.Node>();
        foreach (var node in g.ByPos.Values)
            if (Distance(lat, lon, node.Lat, node.Lon) <= maxNm)
                result.Add(node);
        // Limit to closest 200 to bound memory in dense areas
        if (result.Count > 200)
            result = [.. result.OrderBy(n => Distance(lat, lon, n.Lat, n.Lon)).Take(200)];
        return result;
    }

    private static List<RoutePoint> BuildDirectPoints(string dep, string dest,
        (double Lat, double Lon)? depPos, (double Lat, double Lon)? destPos)
    {
        var pts = new List<RoutePoint>();
        if (depPos is { } d) pts.Add(new RoutePoint(dep, d.Lat, d.Lon, "", 0));
        if (destPos is { } a) pts.Add(new RoutePoint(dest, a.Lat, a.Lon, "", 0));
        return pts;
    }
}
