namespace SkyNetwork.Site.Services;

/// <summary>
/// Aircraft type performance table for flight estimates.
/// Speeds and fuel burns are representative cruise values — clearly labelled as estimates throughout the UI.
/// </summary>
public static class AircraftPerf
{
    public enum Category { Piston, Turboprop, SmallJet, NarrowBody, WideBody }

    public sealed record PerfData(int CruiseSpeedKt, int FuelBurnKgH, Category Cat, int MaxFl);

    private static readonly Dictionary<string, PerfData> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // Narrow-body jets
        ["A319"] = new(445, 2100, Category.NarrowBody, 390),
        ["A320"] = new(450, 2400, Category.NarrowBody, 390),
        ["A321"] = new(450, 2500, Category.NarrowBody, 390),
        ["A20N"] = new(460, 2200, Category.NarrowBody, 390),
        ["A21N"] = new(460, 2500, Category.NarrowBody, 390),
        ["B737"] = new(440, 2600, Category.NarrowBody, 370),
        ["B738"] = new(450, 2500, Category.NarrowBody, 390),
        ["B739"] = new(450, 2600, Category.NarrowBody, 390),
        ["B38M"] = new(450, 2200, Category.NarrowBody, 390),
        ["B752"] = new(460, 3500, Category.NarrowBody, 390),
        ["SU95"] = new(430, 2000, Category.NarrowBody, 390),
        // Wide-body jets
        ["A332"] = new(480, 5500, Category.WideBody, 410),
        ["A333"] = new(480, 5800, Category.WideBody, 410),
        ["A359"] = new(490, 5200, Category.WideBody, 410),
        ["B744"] = new(490, 10000, Category.WideBody, 410),
        ["B748"] = new(490, 9500, Category.WideBody, 410),
        ["B763"] = new(470, 5000, Category.WideBody, 390),
        ["B772"] = new(490, 7500, Category.WideBody, 410),
        ["B77W"] = new(490, 8000, Category.WideBody, 410),
        ["B788"] = new(488, 5000, Category.WideBody, 410),
        ["B789"] = new(488, 5200, Category.WideBody, 410),
        // Small jets (regional)
        ["E170"] = new(420, 1500, Category.SmallJet, 370),
        ["E190"] = new(430, 1800, Category.SmallJet, 370),
        ["CRJ2"] = new(380, 1400, Category.SmallJet, 370),
        ["CRJ9"] = new(420, 1700, Category.SmallJet, 370),
        // Turboprops
        ["AT72"] = new(270, 900, Category.Turboprop, 240),
        ["DH8D"] = new(290, 950, Category.Turboprop, 240),
        // Pistons
        ["C172"] = new(120, 22, Category.Piston, 95),
        ["PA28"] = new(110, 22, Category.Piston, 95),
        // Turboprop single
        ["TBM9"] = new(310, 200, Category.Turboprop, 280),
    };

    public static PerfData Get(string type)
    {
        type = type.Trim().ToUpperInvariant();
        if (Table.TryGetValue(type, out var p)) return p;
        // Guess by prefix
        if (type.StartsWith("A3") || type.StartsWith("A4") || type.StartsWith("B7") || type.StartsWith("B8"))
            return new(450, 2500, Category.NarrowBody, 390);
        if (type.StartsWith("AT") || type.StartsWith("DH") || type.StartsWith("TBM"))
            return new(280, 900, Category.Turboprop, 240);
        if (type.StartsWith("C1") || type.StartsWith("PA") || type.StartsWith("C17"))
            return new(115, 22, Category.Piston, 95);
        return new(450, 2500, Category.NarrowBody, 390);
    }

    /// <summary>
    /// Compute the cruise flight level for the given track (0–360°) and aircraft type.
    /// Uses the ICAO odd/even rule (east = odd, west = even) and caps at the type's certified ceiling.
    /// For pistons and short legs stays below FL100.
    /// </summary>
    public static string CruiseLevel(double trackDeg, double distanceNm, PerfData perf)
    {
        bool eastbound = trackDeg is >= 0 and < 180;
        if (perf.Cat == Category.Piston)
        {
            // Below FL100, VFR-style altitude (use 9500 ft E, 8500 ft W to stay even below Class A)
            return eastbound ? "FL095" : "FL085";
        }
        if (perf.Cat == Category.Turboprop)
        {
            // Cap FL240; short hops can be lower
            int fl = eastbound ? 210 : 200;
            if (distanceNm < 150) fl = eastbound ? 130 : 140;
            else if (distanceNm < 300) fl = eastbound ? 170 : 180;
            return $"FL{Math.Min(fl, perf.MaxFl):D3}";
        }
        // Jets: RVSM levels start FL290; below that use FL250/FL270 for short hops
        int jet = eastbound ? 350 : 360;
        if (distanceNm < 200) jet = eastbound ? 250 : 240;
        else if (distanceNm < 500) jet = eastbound ? 290 : 300;
        else if (distanceNm < 1000) jet = eastbound ? 330 : 340;
        return $"FL{Math.Min(jet, perf.MaxFl):D3}";
    }

    /// <summary>Bearing in degrees [0,360) from point A to point B.</summary>
    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        const double Rad = Math.PI / 180;
        double dLon = (lon2 - lon1) * Rad;
        double y = Math.Sin(dLon) * Math.Cos(lat2 * Rad);
        double x = Math.Cos(lat1 * Rad) * Math.Sin(lat2 * Rad) - Math.Sin(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Cos(dLon);
        return (Math.Atan2(y, x) / Rad + 360) % 360;
    }

    public sealed record FlightEstimates(
        int EteMinutes,
        int TripKg,
        int ContingencyKg,
        int FinalReserveKg,
        int AlternateKg,
        int TotalKg,
        string CruiseLevelStr,
        int CruiseSpeedKt);

    public static FlightEstimates Estimate(double distanceNm, PerfData perf, string? alternateIcao,
        double depLat, double depLon, double destLat, double destLon, double? altDistanceNm = null)
    {
        // Climb/descent adds roughly 10–15% of distance up to 200 NM equivalent
        double cdAllowance = Math.Min(distanceNm * 0.12, 200);
        double effectiveDist = distanceNm + cdAllowance;
        double eteHours = effectiveDist / perf.CruiseSpeedKt;
        int ete = (int)Math.Round(eteHours * 60);
        int trip = (int)Math.Round(eteHours * perf.FuelBurnKgH);
        int contingency = (int)Math.Round(trip * 0.05);
        int finalReserve = (int)Math.Round(perf.FuelBurnKgH * 0.5); // 30 min
        int alternate = 0;
        if (alternateIcao is { Length: > 0 })
        {
            double altDist = altDistanceNm ?? distanceNm * 0.3; // rough guess if no coords
            double altHours = altDist / perf.CruiseSpeedKt;
            alternate = (int)Math.Round(altHours * perf.FuelBurnKgH);
        }
        int total = trip + contingency + finalReserve + alternate;
        double track = Bearing(depLat, depLon, destLat, destLon);
        string level = CruiseLevel(track, distanceNm, perf);
        return new FlightEstimates(ete, trip, contingency, finalReserve, alternate, total, level, perf.CruiseSpeedKt);
    }
}
