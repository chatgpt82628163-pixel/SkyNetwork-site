using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Background service that pre-fetches airport layouts, so a diagram is there when someone zooms in instead of
/// OpenStreetMap being asked then (a first fetch can take minutes when its servers are busy):
/// <list type="bullet">
/// <item>every 2 minutes the airports of what is on the network now — departure, destination and alternate of each
/// pilot, the airport of each controller — so a new flight has its diagrams before it pushes back;</item>
/// <item>shortly after startup and then daily a static list, recent flights and the world's major airports.</item>
/// </list>
/// One airport at a time, with a pause between requests, to be polite to the Overpass servers.
/// </summary>
public sealed partial class AirportLayoutWarmup(
    AirportLayout layout,
    NetworkFeed feed,
    Database db,
    IOptions<SiteOptions> options,
    IWebHostEnvironment env,
    ILogger<AirportLayoutWarmup> log) : BackgroundService
{
    private static readonly TimeSpan LiveInterval = TimeSpan.FromMinutes(2);
    // One fetch at a time between the live and the daily pass.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan PauseBetween = TimeSpan.FromSeconds(5);

    [GeneratedRegex("^[A-Z0-9]{3,4}$")] private static partial Regex ValidIcao();
    // Details field for pilots looks like "B737 UUEE→UUDD"; this captures both airports.
    [GeneratedRegex(@"\b([A-Z]{3,4})\s*[→>]\s*([A-Z]{3,4})\b")] private static partial Regex RoutePattern();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait for the app to fully start before touching the network.
        try { await Task.Delay(StartDelay, ct); } catch (OperationCanceledException) { return; }

        var live = LiveLoopAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            await WarmupAsync(CollectAirports(), ct);
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { break; }
        }
        await live;
    }

    private async Task LiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = LiveAirports();
            if (now.Any(icao => !layout.IsCacheFresh(icao))) await WarmupAsync(now, ct, quiet: true);
            try { await Task.Delay(LiveInterval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task WarmupAsync(IEnumerable<string> airports, CancellationToken ct, bool quiet = false)
    {
        var list = airports.ToList();
        if (!quiet) log.LogInformation("Airport layout warmup: {Count} airports to check", list.Count);
        int fetched = 0, skipped = 0;
        foreach (var icao in list)
        {
            if (ct.IsCancellationRequested) break;
            if (layout.IsCacheFresh(icao)) { skipped++; continue; }
            log.LogDebug("Airport layout warmup: fetching {Icao}", icao);
            string? data;
            try { await _gate.WaitAsync(ct); } catch (OperationCanceledException) { break; }
            try { data = layout.IsCacheFresh(icao) ? null : await layout.GetAsync(icao, ct); }
            catch (OperationCanceledException) { break; }
            finally { _gate.Release(); }
            fetched++;
            // Skip the inter-fetch pause when GetAsync returned null without making HTTP requests
            // (backoff hit). Applying the pause only after real fetches avoids inflating warmup
            // duration when many airports have recent backoff entries.
            if (data != null)
                try { await Task.Delay(PauseBetween, ct); } catch (OperationCanceledException) { return; }
        }
        if (!quiet || fetched > 0)
            log.LogInformation("Airport layout warmup done: {Fetched} fetched, {Skipped} skipped (fresh cache)", fetched, skipped);
    }

    /// <summary>The airports of what is on the network right now: pilots' departure, destination and alternate, controllers' airports.</summary>
    public List<string> LiveAirports()
    {
        var list = new List<string>();
        void Add(string? code)
        {
            code = code?.Trim().ToUpperInvariant();
            if (code != null && ValidIcao().IsMatch(code) && !list.Contains(code)) list.Add(code);
        }
        var snapshot = feed.Current;
        foreach (var pilot in snapshot.Pilots)
            if (pilot.FlightPlan is { } fp) { Add(fp.Departure); Add(fp.Destination); Add(fp.Alternate); }
        foreach (var ctrl in snapshot.Controllers)
        {
            var pos = ctrl.Callsign.IndexOf('_');
            if (pos >= 3) Add(ctrl.Callsign[..pos]);
        }
        return list;
    }

    /// <summary>The major airports of the world (rank 0 in wwwroot/data/airports.json).</summary>
    private IEnumerable<string> MajorAirports()
    {
        try
        {
            string path = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "data", "airports.json");
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateObject()
                .Where(p => p.Value.GetArrayLength() > 3 && p.Value[3].GetInt32() == 0 && ValidIcao().IsMatch(p.Name))
                .Select(p => p.Name).ToList();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogWarning("Airport layout warmup: major airports: {Error}", e.Message);
            return [];
        }
    }

    /// <summary>Collects a deduplicated set of ICAO codes from all sources.</summary>
    public HashSet<string> CollectAirports()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        // 1. Static list from configuration.
        foreach (var part in options.Value.AirportWarmup.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = part.ToUpperInvariant();
            if (ValidIcao().IsMatch(code)) set.Add(code);
        }

        // 2. Controller callsigns currently on the network: "UWWW_APP" → "UWWW".
        var snapshot = feed.Current;
        foreach (var ctrl in snapshot.Controllers)
        {
            var pos = ctrl.Callsign.IndexOf('_');
            if (pos >= 3)
            {
                var code = ctrl.Callsign[..pos].ToUpperInvariant();
                if (ValidIcao().IsMatch(code)) set.Add(code);
            }
        }

        // 3. Pilot flight plans on the network right now.
        foreach (var pilot in snapshot.Pilots)
        {
            if (pilot.FlightPlan is { } fp)
            {
                var dep = fp.Departure.ToUpperInvariant();
                var dst = fp.Destination.ToUpperInvariant();
                if (ValidIcao().IsMatch(dep)) set.Add(dep);
                if (ValidIcao().IsMatch(dst)) set.Add(dst);
            }
        }

        // 4. Recent network sessions (last 30 days): parse departure/destination from Details.
        try
        {
            using var c = db.Open();
            long since = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
            var rows = c.Query<string>(
                "SELECT details FROM network_sessions WHERE kind = 'pilot' AND started_at >= @since",
                new { since });
            foreach (var details in rows)
            {
                var m = RoutePattern().Match(details);
                if (!m.Success) continue;
                set.Add(m.Groups[1].Value);
                set.Add(m.Groups[2].Value);
            }
        }
        catch (Exception e)
        {
            log.LogWarning("Airport layout warmup: could not read recent sessions: {Error}", e.Message);
        }

        // 5. The world's major airports, after everything that is actually flown.
        foreach (var code in MajorAirports()) set.Add(code);

        return set;
    }
}
