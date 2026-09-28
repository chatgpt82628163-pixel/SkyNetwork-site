using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Airport diagrams for the map when zoomed in: runways, taxiways, aprons, buildings, gates and stands from
/// OpenStreetMap (Overpass API), reduced to a compact JSON and cached on disk for a month.
/// </summary>
public sealed partial class AirportLayout(IHttpClientFactory http, IOptions<SiteOptions> options, IWebHostEnvironment env, ILogger<AirportLayout> log)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    // Public Overpass servers, tried in order.
    private static readonly string[] Servers =
    [
        "https://overpass-api.de/api/interpreter",
        "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
        "https://overpass.openstreetmap.fr/api/interpreter",
        "https://overpass.osm.rambler.ru/cgi/interpreter",
    ];
    // Overpass asks for few parallel requests; the same airport is fetched once however many visitors look at it.
    private readonly SemaphoreSlim _overpass = new(2);
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();
    // Remember when a fetch failed so visitors don't hammer the servers repeatedly.
    private readonly ConcurrentDictionary<string, DateTime> _failures = new();
    private static readonly TimeSpan BackoffWindow = TimeSpan.FromMinutes(10);

    [GeneratedRegex("^[A-Z0-9]{3,4}$")] private static partial Regex Icao();

    /// <returns>The diagram JSON, or null when OpenStreetMap could not be reached.</returns>
    public async Task<string?> GetAsync(string icao, CancellationToken ct)
    {
        icao = icao.ToUpperInvariant();
        if (!Icao().IsMatch(icao)) return null;
        var file = new FileInfo(Path.Combine(CacheDir, icao + ".json"));
        // Fresh cache: serve immediately.
        if (file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < MaxAge) return await File.ReadAllTextAsync(file.FullName, ct);

        // Stale cache: return it right away and refresh in the background so the caller doesn't wait.
        if (file.Exists)
        {
            TriggerRefreshAsync(icao, file.FullName);
            return await File.ReadAllTextAsync(file.FullName, ct);
        }

        // No cache: fetch, but honour the backoff window to avoid a storm of requests.
        if (_failures.TryGetValue(icao, out var lastFail) && DateTime.UtcNow - lastFail < BackoffWindow)
            return null;

        var task = _inFlight.GetOrAdd(icao, _ => FetchAsync(icao, file.FullName));
        try { return await task.WaitAsync(ct); }
        finally { if (task.IsCompleted) _inFlight.TryRemove(icao, out _); }
    }

    // Kick off a non-blocking background refresh; errors are logged inside FetchAsync.
    private void TriggerRefreshAsync(string icao, string path)
    {
        _inFlight.GetOrAdd(icao, key =>
        {
            var t = FetchAsync(icao, path);
            t.ContinueWith(completed => _inFlight.TryRemove(icao, out _), TaskContinuationOptions.ExecuteSynchronously);
            return t;
        });
    }

    private async Task<string?> FetchAsync(string icao, string path)
    {
        string query = $$"""
            [out:json][timeout:85];
            nwr["aeroway"="aerodrome"]["icao"="{{icao}}"]->.ad;
            .ad map_to_area->.a;
            (
              way(area.a)["aeroway"~"^(runway|taxiway|taxilane|apron|terminal|hangar|parking_position)$"];
              relation(area.a)["aeroway"~"^(apron|terminal|hangar)$"];
              node(area.a)["aeroway"~"^(gate|parking_position)$"];
            );
            out tags geom qt;
            """;
        await _overpass.WaitAsync();
        try
        {
            string? answer = null;
            foreach (var server in Servers)
            {
                try
                {
                    using var content = new FormUrlEncodedContent([new("data", query)]);
                    using var r = await http.CreateClient("overpass").PostAsync(server, content);
                    if (r.IsSuccessStatusCode) { answer = await r.Content.ReadAsStringAsync(); break; }
                    log.LogWarning("Airport {Icao}: {Server} {Status}", icao, new Uri(server).Host, (int)r.StatusCode);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    log.LogWarning("Airport {Icao}: {Server} {Error}", icao, new Uri(server).Host, e.Message);
                }
            }
            if (answer == null)
            {
                _failures[icao] = DateTime.UtcNow;
                return null;
            }
            _failures.TryRemove(icao, out _);
            string json = Reduce(icao, answer);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return json;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            log.LogWarning("Airport {Icao}: {Error}", icao, e.Message);
            _failures[icao] = DateTime.UtcNow;
            return null;
        }
        finally { _overpass.Release(); }
    }

    /// <summary>Returns true when the cache file exists and is fresh (no fetch needed).</summary>
    public bool IsCacheFresh(string icao)
    {
        icao = icao.ToUpperInvariant();
        if (!Icao().IsMatch(icao)) return false;
        var file = new FileInfo(Path.Combine(CacheDir, icao + ".json"));
        return file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < MaxAge;
    }

    /// <summary>
    /// Overpass answer → {"runways":[{ref,width,line}], "taxiways":[{ref,width,lane,line}], "areas":[{kind,ring}],
    /// "stands":[{ref,gate,at}]}, coordinates as [lat, lon] rounded to about a metre. Also used by tests.
    /// </summary>
    public static string Reduce(string icao, string overpassJson)
    {
        var runways = new JsonArray();
        var taxiways = new JsonArray();
        var areas = new JsonArray();
        var stands = new JsonArray();
        using var doc = JsonDocument.Parse(overpassJson);
        foreach (var e in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            if (!e.TryGetProperty("tags", out var tags)) continue;
            string kind = Tag(tags, "aeroway"), reference = Tag(tags, "ref");
            string type = e.GetProperty("type").GetString() ?? "";
            if (type == "node")
            {
                if (e.TryGetProperty("lat", out var lat) && e.TryGetProperty("lon", out var lon))
                    stands.Add(new JsonObject { ["ref"] = reference, ["gate"] = kind == "gate", ["at"] = Point(lat.GetDouble(), lon.GetDouble()) });
                continue;
            }
            if (type == "relation")
            {
                foreach (var ring in Rings(e)) areas.Add(new JsonObject { ["kind"] = kind, ["ring"] = Line(ring) });
                continue;
            }
            var line = Geometry(e);
            if (line.Count < 2) continue;
            double width = double.TryParse(Tag(tags, "width").Replace(',', '.').Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 0;
            switch (kind)
            {
                case "runway":
                    runways.Add(new JsonObject { ["ref"] = reference, ["width"] = width > 0 ? width : 45, ["line"] = Line(line) });
                    break;
                case "taxiway" or "taxilane":
                    taxiways.Add(new JsonObject { ["ref"] = reference, ["width"] = width > 0 ? width : kind == "taxilane" ? 12 : 23, ["lane"] = kind == "taxilane", ["line"] = Line(line) });
                    break;
                case "parking_position":
                    // A stand drawn as a line ends where the aircraft stops.
                    stands.Add(new JsonObject { ["ref"] = reference, ["gate"] = false, ["at"] = Point(line[^1].Lat, line[^1].Lon) });
                    break;
                default:
                    areas.Add(new JsonObject { ["kind"] = kind, ["ring"] = Line(line) });
                    break;
            }
        }
        return new JsonObject { ["icao"] = icao, ["runways"] = runways, ["taxiways"] = taxiways, ["areas"] = areas, ["stands"] = stands }.ToJsonString();
    }

    private static List<(double Lat, double Lon)> Geometry(JsonElement e)
    {
        var list = new List<(double, double)>();
        if (e.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Array)
            foreach (var p in g.EnumerateArray())
                if (p.ValueKind == JsonValueKind.Object) list.Add((p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble()));
        return list;
    }

    // Outer ways of a multipolygon joined end to end into closed rings.
    private static IEnumerable<List<(double Lat, double Lon)>> Rings(JsonElement relation)
    {
        if (!relation.TryGetProperty("members", out var members)) yield break;
        var parts = members.EnumerateArray()
            .Where(m => m.GetProperty("type").GetString() == "way" && (m.TryGetProperty("role", out var role) ? role.GetString() : "") is "outer" or "")
            .Select(Geometry).Where(l => l.Count > 1).ToList();
        while (parts.Count > 0)
        {
            var ring = parts[0];
            parts.RemoveAt(0);
            for (bool grew = true; grew && ring[0] != ring[^1];)
            {
                grew = false;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p[0] == ring[^1]) ring.AddRange(p.Skip(1));
                    else if (p[^1] == ring[^1]) ring.AddRange(Enumerable.Reverse(p).Skip(1));
                    else continue;
                    parts.RemoveAt(i);
                    grew = true;
                    break;
                }
            }
            yield return ring;
        }
    }

    private static JsonArray Point(double lat, double lon) => [Math.Round(lat, 5), Math.Round(lon, 5)];
    private static JsonArray Line(List<(double Lat, double Lon)> line) => new(line.Select(p => (JsonNode)Point(p.Lat, p.Lon)).ToArray());
    private static string Tag(JsonElement tags, string name) => tags.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private string CacheDir
    {
        get
        {
            // Next to the database (a directory the site can write to).
            string db = Path.GetFullPath(options.Value.Database, env.ContentRootPath);
            return Path.Combine(Path.GetDirectoryName(db)!, "airports");
        }
    }
}
