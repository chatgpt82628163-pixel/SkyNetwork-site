using System.Collections.Concurrent;
using System.Text.Json;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Winds and temperatures aloft from Open-Meteo (CC BY 4.0).
/// Attribution: Weather data by Open-Meteo.com
/// </summary>
public sealed class WindsAloftService(IHttpClientFactory http, ILogger<WindsAloftService> log)
{
    // ── Pressure levels available in Open-Meteo ───────────────────────────────
    private static readonly int[] PressureLevels = [1000, 975, 950, 925, 900, 850, 800, 700, 600, 500, 400, 300, 250, 200, 150];

    // Grid-cell cache: key = "lat05,lon05,hourceil" (rounded to 0.5° and hour).
    private readonly ConcurrentDictionary<string, (DateTime At, OpenMeteoHourly Data)> _cache = new();
    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(60);
    private static readonly SemaphoreSlim _sem = new(1, 1);

    // ── ISA helpers ───────────────────────────────────────────────────────────

    /// <summary>Pressure level (hPa) for a given altitude in feet (ISA).</summary>
    public static double FtToPressureHpa(double altFt)
    {
        double altM = altFt * 0.3048;
        if (altM <= 11000)
            return 1013.25 * Math.Pow(1 - altM / 44330.8, 5.25588);
        // Stratosphere: 216.65 K isothermal
        return 226.321 * Math.Exp(-0.0341632 * (altM - 11000));
    }

    /// <summary>Altitude in feet for a given pressure level (ISA).</summary>
    public static double PressureHpaToFt(double hPa)
    {
        if (hPa >= 226.321)
            return (1 - Math.Pow(hPa / 1013.25, 1 / 5.25588)) * 44330.8 / 0.3048;
        return (11000 - Math.Log(hPa / 226.321) / 0.0341632) / 0.3048;
    }

    /// <summary>ISA temperature in °C at the given altitude in feet.</summary>
    public static double IsaTempC(double altFt)
    {
        double altM = altFt * 0.3048;
        return altM <= 11000 ? 15.0 - 6.5 * altM / 1000 : -56.5;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns wind and temperature aloft for each (lat, lon) point at the given flight level and time.
    /// </summary>
    public async Task<IReadOnlyList<AloftPoint>> GetAloftAsync(
        IReadOnlyList<(double Lat, double Lon)> points, int flightLevel, DateTime time, CancellationToken ct)
    {
        double altFt = flightLevel * 100.0;
        double targetHpa = FtToPressureHpa(altFt);

        // Find the two bracketing pressure levels.
        int upper = PressureLevels.FirstOrDefault(p => p <= targetHpa, PressureLevels[^1]);
        int lower = PressureLevels.LastOrDefault(p => p >= targetHpa, PressureLevels[0]);

        var result = new List<AloftPoint>();
        foreach (var (lat, lon) in points)
        {
            // Round to 0.5° grid cell.
            double gridLat = Math.Round(lat * 2) / 2;
            double gridLon = Math.Round(lon * 2) / 2;
            long hourFloor = (long)(time.ToUniversalTime() - DateTime.UnixEpoch).TotalHours;
            string key = $"{gridLat:F1},{gridLon:F1},{hourFloor},{upper},{lower}";

            if (!_cache.TryGetValue(key, out var cached) || DateTime.UtcNow - cached.At > CacheAge)
            {
                cached = await FetchAsync(gridLat, gridLon, upper, lower, ct);
                _cache[key] = cached;
                if (_cache.Count > 10000) _cache.Clear();
            }

            var data = cached.Data;
            // Find the closest hourly index.
            int idx = FindTimeIndex(data.Time, time);
            if (idx < 0) continue;

            // Interpolate wind/temperature between the two pressure levels.
            double t = InterpolateT(upper, lower, targetHpa);
            double dir = InterpolateAngle(
                data.WindDir.TryGetValue(upper, out var d1) ? d1[idx] : double.NaN,
                data.WindDir.TryGetValue(lower, out var d2) ? d2[idx] : double.NaN, t);
            double spd = Lerp(
                data.WindSpd.TryGetValue(upper, out var s1) ? s1[idx] : double.NaN,
                data.WindSpd.TryGetValue(lower, out var s2) ? s2[idx] : double.NaN, t);
            double temp = Lerp(
                data.Temp.TryGetValue(upper, out var tt1) ? tt1[idx] : double.NaN,
                data.Temp.TryGetValue(lower, out var tt2) ? tt2[idx] : double.NaN, t);

            double isa = IsaTempC(altFt);
            result.Add(new AloftPoint(lat, lon, flightLevel,
                time, (int)Math.Round(dir), Math.Round(spd, 1), Math.Round(temp, 1), Math.Round(temp - isa, 1)));
        }
        return result;
    }

    // ── Open-Meteo fetch ──────────────────────────────────────────────────────

    private async Task<(DateTime At, OpenMeteoHourly Data)> FetchAsync(
        double lat, double lon, int upper, int lower, CancellationToken ct)
    {
        await _sem.WaitAsync(ct);
        try
        {
            string levels = upper == lower
                ? upper.ToString()
                : $"{lower},{upper}";
            // Open-Meteo returns wind in m/s by default; we ask for knots.
            string vars = string.Join(",", new[] { lower, upper }.Distinct().SelectMany(p => new[]
            {
                $"wind_speed_{p}hPa",
                $"wind_direction_{p}hPa",
                $"temperature_{p}hPa"
            }));
            string url = $"https://api.open-meteo.com/v1/forecast" +
                         $"?latitude={lat:F2}&longitude={lon:F2}" +
                         $"&hourly={vars}" +
                         $"&wind_speed_unit=kn" +
                         $"&forecast_days=3&past_days=1" +
                         $"&timezone=UTC" +
                         $"&models=gfs_seamless";

            string json = await http.CreateClient("aloft").GetStringAsync(url, ct);
            return (DateTime.UtcNow, ParseOpenMeteo(json, [lower, upper]));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("WindsAloft {Lat},{Lon}: {Error}", lat, lon, e.Message);
            return (DateTime.UtcNow, new OpenMeteoHourly([], [], [], []));
        }
        finally
        {
            _sem.Release();
        }
    }

    // ── JSON parsing ──────────────────────────────────────────────────────────

    internal static OpenMeteoHourly ParseOpenMeteo(string json, int[] levels)
    {
        using var doc = JsonDocument.Parse(json);
        var hourlyObj = doc.RootElement.GetProperty("hourly");

        List<DateTime> times = [];
        foreach (var t in hourlyObj.GetProperty("time").EnumerateArray())
            times.Add(DateTime.Parse(t.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());

        Dictionary<int, double[]> dir = [], spd = [], temp = [];
        foreach (int lvl in levels)
        {
            dir[lvl]  = ParseArray(hourlyObj, $"wind_direction_{lvl}hPa");
            spd[lvl]  = ParseArray(hourlyObj, $"wind_speed_{lvl}hPa");
            temp[lvl] = ParseArray(hourlyObj, $"temperature_{lvl}hPa");
        }
        return new OpenMeteoHourly(times, dir, spd, temp);
    }

    private static double[] ParseArray(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var el)) return [];
        return el.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? double.NaN : v.GetDouble()).ToArray();
    }

    // ── Math ──────────────────────────────────────────────────────────────────

    private static int FindTimeIndex(List<DateTime> times, DateTime target)
    {
        if (times.Count == 0) return -1;
        int best = 0;
        double bestDiff = Math.Abs((times[0] - target).TotalHours);
        for (int i = 1; i < times.Count; i++)
        {
            double diff = Math.Abs((times[i] - target).TotalHours);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return bestDiff <= 2 ? best : -1;
    }

    /// <summary>t=0 → upper (lower pressure = higher altitude), t=1 → lower (higher pressure = lower altitude).</summary>
    private static double InterpolateT(int upper, int lower, double target)
    {
        if (upper == lower) return 0;
        // Interpolate in log-pressure space.
        double logU = Math.Log(upper), logL = Math.Log(lower), logT = Math.Log(target);
        return (logT - logU) / (logL - logU);
    }

    private static double Lerp(double a, double b, double t)
    {
        if (double.IsNaN(a)) return b;
        if (double.IsNaN(b)) return a;
        return a + (b - a) * t;
    }

    private static double InterpolateAngle(double a, double b, double t)
    {
        if (double.IsNaN(a)) return b;
        if (double.IsNaN(b)) return a;
        // Shortest path interpolation.
        double diff = ((b - a + 540) % 360) - 180;
        return (a + diff * t + 360) % 360;
    }
}

/// <summary>Parsed hourly arrays from Open-Meteo (internal).</summary>
internal sealed record OpenMeteoHourly(
    List<DateTime> Time,
    Dictionary<int, double[]> WindDir,
    Dictionary<int, double[]> WindSpd,
    Dictionary<int, double[]> Temp);
