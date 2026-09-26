using System.Globalization;
using System.Text.Json;

namespace SkyNetwork.Site.Services;

public sealed record FeedFlightPlan(string Rules, string Aircraft, int CruiseSpeed, string Departure, string DepartureTime,
    string CruiseAltitude, string Destination, int EnrouteMinutes, int FuelMinutes, string Alternate, string Remarks, string Route);

public sealed record PilotOnline(long Cid, string Name, string Callsign, double? Latitude, double? Longitude, int Altitude,
    int Groundspeed, string Transponder, double? Heading, DateTime LogonTime, FeedFlightPlan? FlightPlan, bool OnGround = false);

public sealed partial record ControllerOnline(long Cid, string Name, string Callsign, string Rating, string Frequency, int Facility,
    int VisualRange, double? Latitude, double? Longitude, DateTime LogonTime, IReadOnlyList<string>? TextAtis = null)
{
    /// <summary>An ATIS logs in as an observer; its callsign (UUEE_ATIS) tells it apart.</summary>
    public bool IsAtis => Callsign.EndsWith("_ATIS", StringComparison.OrdinalIgnoreCase);

    public string FacilityName => IsAtis ? "ATIS" : Facility switch
    {
        1 => "FSS", 2 => "DEL", 3 => "GND", 4 => "TWR", 5 => "APP", 6 => "CTR", _ => "OBS",
    };

    /// <summary>The ATIS text (or a controller's info lines) as the server last received it.</summary>
    public IReadOnlyList<string> Text => TextAtis ?? [];

    /// <summary>The ATIS letter from "INFORMATION K" / "INFORMATION KILO" / "ИНФОРМАЦИЯ K", or empty.</summary>
    public string AtisCode
    {
        get
        {
            if (!IsAtis) return "";
            foreach (var line in Text)
                if (Code().Match(line) is { Success: true } m)
                    return m.Groups[1].Value.ToUpperInvariant()[..1];
            return "";
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:INFORMATION|ИНФОРМАЦИЯ|ИНФОРМАЦИИ)\s+(ALFA|ALPHA|BRAVO|CHARLIE|DELTA|ECHO|FOXTROT|GOLF|HOTEL|INDIA|JULIETT?|KILO|LIMA|MIKE|NOVEMBER|OSCAR|PAPA|QUEBEC|ROMEO|SIERRA|TANGO|UNIFORM|VICTOR|WHISKEY|X-?RAY|YANKEE|ZULU|[A-Z])\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Code();
}

/// <summary>One point of an aircraft's flown track (unix seconds).</summary>
public readonly record struct TrackPoint(double Latitude, double Longitude, int Altitude, int Groundspeed, long Time);

/// <summary>Who is online, from the FSD server's data feed.</summary>
public sealed record OnlineSnapshot(DateTime Updated, string Server, bool Available, IReadOnlyList<PilotOnline> Pilots,
    IReadOnlyList<ControllerOnline> Controllers)
{
    public static readonly OnlineSnapshot Empty = new(DateTime.MinValue, "", false, [], []);
}

/// <summary>Parses the FSD server's /data.json.</summary>
public static class FeedParser
{
    public static OnlineSnapshot Parse(string json, OnlineSnapshot? previous = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var general = root.GetProperty("general");
        var updated = DateTimeOffset.FromUnixTimeSeconds(general.GetProperty("update_timestamp").GetInt64()).UtcDateTime;
        string server = general.TryGetProperty("server", out var s) ? s.GetString() ?? "" : "";

        var before = previous?.Pilots.ToDictionary(p => p.Callsign, StringComparer.OrdinalIgnoreCase) ?? [];
        var pilots = new List<PilotOnline>();
        foreach (var p in root.GetProperty("pilots").EnumerateArray())
        {
            string callsign = p.GetProperty("callsign").GetString() ?? "";
            double? lat = Number(p, "latitude"), lon = Number(p, "longitude");
            // The heading comes with the feed (the server reads it from the position packet); an older server has none,
            // then the track from the previous position, or the old value, is used.
            double? heading = Number(p, "heading");
            bool onGround = p.TryGetProperty("on_ground", out var og) && og.ValueKind == JsonValueKind.True;
            if (heading == null && before.TryGetValue(callsign, out var old))
            {
                heading = old.Heading;
                if (lat is { } la && lon is { } lo && old.Latitude is { } pla && old.Longitude is { } plo && Distance(pla, plo, la, lo) > 0.05)
                    heading = Bearing(pla, plo, la, lo);
            }
            pilots.Add(new PilotOnline(
                p.GetProperty("cid").GetInt64(), p.GetProperty("name").GetString() ?? "", callsign, lat, lon,
                Int(p, "altitude"), Int(p, "groundspeed"), p.TryGetProperty("transponder", out var t) ? t.GetString() ?? "" : "",
                heading, Logon(p),
                p.TryGetProperty("flight_plan", out var fp) && fp.ValueKind == JsonValueKind.String ? ParsePlan(fp.GetString()!) : null, onGround));
        }

        var controllers = new List<ControllerOnline>();
        foreach (var c in root.GetProperty("controllers").EnumerateArray())
            controllers.Add(new ControllerOnline(
                c.GetProperty("cid").GetInt64(), c.GetProperty("name").GetString() ?? "", c.GetProperty("callsign").GetString() ?? "",
                c.TryGetProperty("rating", out var r) ? r.GetString() ?? "" : "", c.TryGetProperty("frequency", out var f) ? f.GetString() ?? "" : "",
                Int(c, "facility"), Int(c, "visual_range"), Number(c, "latitude"), Number(c, "longitude"), Logon(c),
                c.TryGetProperty("text_atis", out var ta) && ta.ValueKind == JsonValueKind.Array
                    ? ta.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : null));

        return new OnlineSnapshot(updated, server, true,
            pilots.OrderBy(p => p.Callsign).ToList(), controllers.OrderBy(c => c.Callsign).ToList());
    }

    /// <summary>"*A:I:A320:450:UUEE:1200:0:FL350:ULLI:1:10:3:0:ULLO:/V/:DCT" (the $FP body the server stores).</summary>
    public static FeedFlightPlan? ParsePlan(string raw)
    {
        var f = raw.Split(':');
        if (f.Length < 16) return null;
        int I(string x) => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        string rules = f[1].ToUpperInvariant() switch { "I" => "IFR", "V" => "VFR", var x => x };
        return new FeedFlightPlan(rules, f[2], I(f[3]), f[4], f[5], f[7], f[8], I(f[9]) * 60 + I(f[10]), I(f[11]) * 60 + I(f[12]),
            f[13], f[14], string.Join(':', f.Skip(15)));
    }

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static DateTime Logon(JsonElement e) =>
        DateTimeOffset.FromUnixTimeSeconds(e.TryGetProperty("logon_time", out var v) ? v.GetInt64() : 0).UtcDateTime;

    private const double Rad = Math.PI / 180;

    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Rad, dLon = (lon2 - lon1) * Rad;
        double a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * 3440.065 * Math.Asin(Math.Sqrt(a));
    }

    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        double y = Math.Sin((lon2 - lon1) * Rad) * Math.Cos(lat2 * Rad);
        double x = Math.Cos(lat1 * Rad) * Math.Sin(lat2 * Rad) - Math.Sin(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Cos((lon2 - lon1) * Rad);
        return (Math.Atan2(y, x) / Rad + 360) % 360;
    }
}
