using System.IO.Compression;
using System.Text.Json;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Runways of the world from OurAirports (public domain), bundled as Nav/runways.csv.gz by tools/runways/build.js.
/// An approximate airport diagram — runways only — for when the OpenStreetMap one (<see cref="AirportLayout"/>)
/// is not there yet, in the same shape so the map and the planner draw and use it the same way.
/// </summary>
public sealed class RunwayData(IWebHostEnvironment env, ILogger<RunwayData> log)
{
    public sealed record Runway(string Le, string He, double Lat1, double Lon1, double Lat2, double Lon2, int LengthM, int WidthM, string Surface, bool Exact);

    private static readonly object LoadLock = new();
    private static Dictionary<string, List<Runway>>? _byAirport;
    private static string? _root;

    private Dictionary<string, List<Runway>> ByAirport
    {
        get
        {
            lock (LoadLock)
            {
                if (_byAirport != null && _root == env.ContentRootPath) return _byAirport;
                var d = new Dictionary<string, List<Runway>>(StringComparer.OrdinalIgnoreCase);
                string file = Path.Combine(env.ContentRootPath, "Nav", "runways.csv.gz");
                try
                {
                    if (File.Exists(file))
                    {
                        using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
                        using var reader = new StreamReader(gz);
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        while (reader.ReadLine() is { } line)
                        {
                            var f = line.Split(',');
                            if (f.Length != 11) continue;
                            if (!d.TryGetValue(f[0], out var list)) d[f[0]] = list = [];
                            list.Add(new Runway(f[1], f[2], double.Parse(f[3], inv), double.Parse(f[4], inv), double.Parse(f[5], inv),
                                double.Parse(f[6], inv), int.Parse(f[7], inv), int.Parse(f[8], inv), f[9], f[10] == "1"));
                        }
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or FormatException)
                {
                    log.LogWarning("Runway data: {Error}", e.Message);
                }
                _root = env.ContentRootPath;
                return _byAirport = d;
            }
        }
    }

    public IReadOnlyList<Runway> Of(string icao) => ByAirport.TryGetValue(icao, out var l) ? l : [];

    /// <summary>
    /// The runways as an airport diagram (the <see cref="AirportLayout"/> JSON shape, with "approx": true), or null
    /// when the airport has none in the data.
    /// </summary>
    public string? ApproxLayoutJson(string icao)
    {
        var list = Of(icao);
        if (list.Count == 0) return null;
        return JsonSerializer.Serialize(new
        {
            approx = true,
            runways = list.Select(r => new
            {
                @ref = r.He.Length > 0 ? $"{r.Le}/{r.He}" : r.Le,
                width = r.WidthM > 0 ? r.WidthM : 45,
                line = new[] { new[] { r.Lat1, r.Lon1 }, new[] { r.Lat2, r.Lon2 } },
            }),
            taxiways = Array.Empty<object>(),
            areas = Array.Empty<object>(),
            stands = Array.Empty<object>(),
        });
    }

    /// <summary>
    /// The OpenStreetMap diagram if it comes within <paramref name="wait"/> (it goes on loading and is cached for
    /// the next time), otherwise the approximate one. Approx is true for the latter.
    /// </summary>
    public async Task<(string? Json, bool Approx)> LayoutAsync(AirportLayout layouts, string icao, TimeSpan wait, CancellationToken ct)
    {
        var fetch = layouts.GetAsync(icao, CancellationToken.None);
        try
        {
            if (await Task.WhenAny(fetch, Task.Delay(wait, ct)) == fetch && await fetch is { } json) return (json, false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            log.LogWarning("Layout of {Icao}: {Error}", icao, e.Message);
        }
        var approx = ApproxLayoutJson(icao);
        return (approx, approx != null);
    }
}
