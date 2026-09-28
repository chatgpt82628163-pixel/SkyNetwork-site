using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SkyNetwork.Site.Services;

/// <summary>METAR and TAF from the Aviation Weather Center (NOAA). METAR cached 5 min, TAF cached 20 min.</summary>
public sealed partial class MetarService(IHttpClientFactory http, ILogger<MetarService> log)
{
    private static readonly TimeSpan MetarMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TafMaxAge   = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<string, (DateTime At, string Text)> _metar = new();
    private readonly ConcurrentDictionary<string, (DateTime At, string Text)> _taf   = new();

    [GeneratedRegex("^[A-Z0-9]{4}$")] private static partial Regex Icao();

    // ── METAR ─────────────────────────────────────────────────────────────────

    /// <returns>The METAR text, "" when the airport has none, null for a malformed code.</returns>
    public async Task<string?> GetAsync(string icao, CancellationToken ct)
    {
        icao = icao.ToUpperInvariant();
        if (!Icao().IsMatch(icao)) return null;
        if (_metar.TryGetValue(icao, out var hit) && DateTime.UtcNow - hit.At < MetarMaxAge) return hit.Text;
        try
        {
            string text = (await http.CreateClient("metar")
                .GetStringAsync($"https://aviationweather.gov/api/data/metar?ids={icao}&format=raw", ct)).Trim();
            text = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            _metar[icao] = (DateTime.UtcNow, text);
            if (_metar.Count > 5000) _metar.Clear();
            return text;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("METAR {Icao}: {Error}", icao, e.Message);
            return hit.Text ?? "";
        }
    }

    // ── TAF ───────────────────────────────────────────────────────────────────

    /// <returns>The TAF text, "" when none, null for a malformed code.</returns>
    public async Task<string?> GetTafAsync(string icao, CancellationToken ct)
    {
        icao = icao.ToUpperInvariant();
        if (!Icao().IsMatch(icao)) return null;
        if (_taf.TryGetValue(icao, out var hit) && DateTime.UtcNow - hit.At < TafMaxAge) return hit.Text;
        try
        {
            string text = (await http.CreateClient("metar")
                .GetStringAsync($"https://aviationweather.gov/api/data/taf?ids={icao}&format=raw", ct)).Trim();
            // The API may return one or multiple lines; collapse continuation lines (lines not starting with a station).
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            text = lines.Length == 0 ? "" : string.Join(' ', lines.Select(l => l.Trim()));
            _taf[icao] = (DateTime.UtcNow, text);
            if (_taf.Count > 5000) _taf.Clear();
            return text;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("TAF {Icao}: {Error}", icao, e.Message);
            return hit.Text ?? "";
        }
    }

    // ── Runway wind components ────────────────────────────────────────────────

    /// <summary>Computes headwind (+) / tailwind (-) and crosswind (positive = from right) components.</summary>
    /// <param name="wind">Decoded wind group.</param>
    /// <param name="runwayTrueHeading">Runway heading in degrees TRUE.</param>
    public static (double HeadwindKt, double CrosswindKt, double HeadwindMs, double CrosswindMs,
                    double? GustHeadKt, double? GustCrossKt) RunwayComponents(WindInfo wind, double runwayTrueHeading)
    {
        if (wind.Variable || wind.Speed == 0)
            return (0, 0, 0, 0, null, null);

        double angle = DegToRad(wind.Direction - runwayTrueHeading);
        double spKt = wind.SpeedKt;
        double spMs = wind.SpeedMs;

        double hwKt  = spKt * Math.Cos(angle);
        double cwKt  = spKt * Math.Sin(angle);
        double hwMs  = spMs * Math.Cos(angle);
        double cwMs  = spMs * Math.Sin(angle);

        double? gHwKt = null, gCwKt = null;
        if (wind.GustKt is { } gKt)
        {
            gHwKt = gKt * Math.Cos(angle);
            gCwKt = gKt * Math.Sin(angle);
        }

        return (hwKt, cwKt, hwMs, cwMs, gHwKt, gCwKt);
    }

    private static double DegToRad(double d) => d * Math.PI / 180.0;
}
