using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Background service that pre-fetches airport layouts for airports seen in the network feed and recent activity,
/// plus a configurable static list. Runs once shortly after startup, then once every 24 hours.
/// Fetches one airport at a time with a pause between requests to be polite to the Overpass servers.
/// </summary>
public sealed partial class AirportLayoutWarmup(
    AirportLayout layout,
    NetworkFeed feed,
    Database db,
    IOptions<SiteOptions> options,
    ILogger<AirportLayoutWarmup> log) : BackgroundService
{
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

        while (!ct.IsCancellationRequested)
        {
            await WarmupAsync(ct);
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task WarmupAsync(CancellationToken ct)
    {
        var airports = CollectAirports();
        log.LogInformation("Airport layout warmup: {Count} airports to check", airports.Count);
        int fetched = 0, skipped = 0;
        foreach (var icao in airports)
        {
            if (ct.IsCancellationRequested) break;
            if (layout.IsCacheFresh(icao)) { skipped++; continue; }
            log.LogDebug("Airport layout warmup: fetching {Icao}", icao);
            var data = await layout.GetAsync(icao, ct);
            fetched++;
            // Skip the inter-fetch pause when GetAsync returned null without making HTTP requests
            // (backoff hit). Applying the pause only after real fetches avoids inflating warmup
            // duration when many airports have recent backoff entries.
            if (data != null)
                try { await Task.Delay(PauseBetween, ct); } catch (OperationCanceledException) { return; }
        }
        log.LogInformation("Airport layout warmup done: {Fetched} fetched, {Skipped} skipped (fresh cache)", fetched, skipped);
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

        return set;
    }
}
