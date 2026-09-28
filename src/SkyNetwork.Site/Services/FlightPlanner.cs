using System.Text.Json;

namespace SkyNetwork.Site.Services;

/// <summary>What the pilot asks the planner for. Everything but the airports and the type has a default.</summary>
public sealed record PlanRequest(
    string Departure, string Destination, string AircraftType,
    string? Alternate = null, int? Pax = null, double? CargoKg = null, double? BaggagePerPaxKg = null,
    int CostIndex = 30, int? FlightLevel = null, double ExtraFuelKg = 0, DateTime? OffBlock = null,
    int Variant = 0, string? DepartureRunway = null, string? ArrivalRunway = null, string? Callsign = null);

/// <summary>A text for the pilot in both site languages.</summary>
public sealed record PlanText(string Code, string En, string Ru);

public sealed record PlanAirport(string Icao, string Name, double Lat, double Lon);

public sealed record PlanRunway(
    string Ident, double HeadingTrue, int LengthM, int WidthM,
    double HeadwindKt, double CrosswindKt, bool Usable,
    double[] Threshold, double[] End);

public sealed record PlanRunways(
    string Icao, bool Available, string? Selected, List<PlanRunway> Runways,
    List<double[][]> Lines, double? WindDir, double? WindKt, double? GustKt, bool WindVariable);

public sealed record PlanWeather(
    string Icao, string? Metar, string? MetarRu, string? MetarEn, string? Category, string? ObservedAt,
    string? Taf, string? TafRu, string? TafEn);

public sealed record PlanPoint(
    string Ident, string Airway, string Kind, double Lat, double Lon,
    int CourseTrue, double LegNm, double CumNm, double RemainingNm,
    int AltitudeFt, int? WindDir, int? WindKt, int? TempC, int? IsaDev,
    int TasKt, int GsKt, double LegMin, double CumMin, DateTime Eta,
    int LegFuelKg, int FuelUsedKg, int FuelRemainingKg);

public sealed record PlanFuel(
    int TaxiKg, int TripKg, int ContingencyKg, string ContingencyRule, int AlternateKg, int FinalReserveKg,
    int ExtraKg, int MinimumTakeoffKg, int BlockKg, int LandingKg, int TankCapacityKg,
    double TripMin, double ContingencyMin, double AlternateMin, double FinalReserveMin, double ExtraMin);

public sealed record PlanWeights(
    int EmptyKg, int Pax, int PaxMassKg, int BaggageKg, int CargoKg, int PayloadKg,
    int ZeroFuelKg, int MaxZeroFuelKg, int TakeoffKg, int MaxTakeoffKg, int LandingKg, int MaxLandingKg,
    int LimitingMarginKg, string Limiting);

public sealed record PlanRouteOption(string Route, double DistanceNm, bool IsDirect);

public sealed record PlanResult(
    string Departure, string Destination, string? Alternate,
    PlanAirport DepartureAirport, PlanAirport DestinationAirport, PlanAirport? AlternateAirport,
    string AircraftIcao, string AircraftName, string AircraftCategory, string Engine,
    string Callsign, string Airac,
    // route
    string Route, string FieldRoute, List<PlanRouteOption> Routes, int Variant,
    double DistanceNm, double GreatCircleNm, int CruiseLevel, int CostIndex, double CruiseMach, int CruiseTasKt,
    double AvgWindComponentKt, int? AvgWindDir, int? AvgWindKt, double AvgIsaDev,
    double TocNm, double TodNm,
    // times
    DateTime OffBlock, DateTime Takeoff, DateTime Landing, DateTime OnBlock, double AirMin, double BlockMin,
    // details
    List<PlanPoint> NavLog, PlanFuel Fuel, PlanWeights Weights,
    PlanRunways DepartureRunways, PlanRunways ArrivalRunways,
    PlanWeather DepartureWeather, PlanWeather DestinationWeather, PlanWeather? AlternateWeather,
    List<PlanAirport> AlternateOptions, List<PlanText> Warnings, bool WindsAloftAvailable);

/// <summary>
/// The flight planner: airway route (three variants), runways by wind, cruise level by the semicircular rule,
/// climb and descent integrated on the OpenAP model, winds and temperatures aloft per leg, a navigation log with
/// TOC and TOD, ICAO fuel (taxi, trip, 5 % contingency, alternate, 30 min final reserve, extra) and weights
/// against the limits. For the simulator only.
/// </summary>
public sealed class FlightPlanner(
    NavData nav, AircraftService aircraft, MetarService metar, WindsAloftService aloft,
    AirportLayout layouts, IWebHostEnvironment env, ILogger<FlightPlanner> log)
{
    private const double TaxiMin = 15;
    private const double HoldAltFt = 1500;
    private const double HoldTasKt = 210;
    // The cruise-model fuel flow at 1500 ft overstates a clean holding pattern flown at best-endurance speed.
    private const double HoldingFactor = 0.8;
    private const double AlternateRouteFactor = 1.08;
    private const double ApproachMin = 4;

    // ── airports (names and size ranks for the alternates) ───────────────────

    private static Dictionary<string, (double Lat, double Lon, string Name, int Rank)>? _airports;
    private static readonly object AirportsLock = new();

    private Dictionary<string, (double Lat, double Lon, string Name, int Rank)> Airports
    {
        get
        {
            lock (AirportsLock)
            {
                if (_airports != null) return _airports;
                var d = new Dictionary<string, (double, double, string, int)>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string path = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "data", "airports.json");
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        var a = p.Value;
                        d[p.Name] = (a[0].GetDouble(), a[1].GetDouble(),
                            a.GetArrayLength() > 2 ? a[2].GetString() ?? "" : "",
                            a.GetArrayLength() > 3 ? a[3].GetInt32() : 2);
                    }
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
                {
                    log.LogWarning("Planner airports: {Error}", e.Message);
                }
                return _airports = d;
            }
        }
    }

    public PlanAirport? Airport(string icao) =>
        Airports.TryGetValue(icao, out var a) ? new PlanAirport(icao.ToUpperInvariant(), a.Name, a.Lat, a.Lon) : null;

    /// <summary>
    /// Airports worth taking as the alternate, 30–400 NM from the destination: near first, a major airport counts
    /// nearer than a smaller one, the same region (the first two letters of the code) nearer than a neighbouring one
    /// and much nearer than another country. Air bases are left out.
    /// </summary>
    public List<PlanAirport> AlternateOptions(string destination, int count = 4)
    {
        if (!Airports.TryGetValue(destination, out var d)) return [];
        string dest = destination.ToUpperInvariant();
        double Score(string icao, int rank, double dist) =>
            dist * (rank == 0 ? 1 : 1.5) * (icao[..2] == dest[..2] ? 1 : icao[0] == dest[0] ? 1.3 : 3);
        return Airports
            .Where(kv => !kv.Key.Equals(dest, StringComparison.OrdinalIgnoreCase) && kv.Value.Rank <= 1 && kv.Key.Length == 4
                && !kv.Value.Name.Contains("Air Base", StringComparison.OrdinalIgnoreCase)
                && !kv.Value.Name.Contains("Military", StringComparison.OrdinalIgnoreCase))
            .Select(kv => (Key: kv.Key.ToUpperInvariant(), kv.Value, Dist: NavData.Distance(d.Lat, d.Lon, kv.Value.Lat, kv.Value.Lon)))
            .Where(x => x.Dist is >= 30 and <= 400)
            .OrderBy(x => Score(x.Key, x.Value.Rank, x.Dist))
            .Take(count)
            .Select(x => new PlanAirport(x.Key.ToUpperInvariant(), x.Value.Name, x.Value.Lat, x.Value.Lon))
            .ToList();
    }

    // ── plan ─────────────────────────────────────────────────────────────────

    /// <summary>Plans the flight, or returns the reason it cannot (in both languages).</summary>
    public async Task<(PlanResult? Plan, PlanText? Error)> PlanAsync(PlanRequest q, CancellationToken ct)
    {
        string dep = q.Departure.Trim().ToUpperInvariant(), dest = q.Destination.Trim().ToUpperInvariant();
        var depApt = Airport(dep);
        var destApt = Airport(dest);
        if (depApt == null) return (null, new PlanText("dep", $"Unknown departure airport {dep}", $"Неизвестный аэропорт вылета {dep}"));
        if (destApt == null) return (null, new PlanText("dest", $"Unknown destination airport {dest}", $"Неизвестный аэропорт назначения {dest}"));
        var ac = aircraft.Get(q.AircraftType.Trim());
        if (ac == null) return (null, new PlanText("type", $"No profile for aircraft type {q.AircraftType}", $"Нет профиля для типа {q.AircraftType}"));

        var warnings = new List<PlanText>();
        if (ac.IsMapped)
            warnings.Add(new PlanText("mapped", $"{ac.Icao}: approximate performance (the masses of the type, flown like a similar one)",
                $"{ac.Icao}: характеристики приближённые (массы типа, расход по похожему)"));
        var altOptions = AlternateOptions(dest);
        string? altIcao = string.IsNullOrWhiteSpace(q.Alternate) ? altOptions.FirstOrDefault()?.Icao : q.Alternate.Trim().ToUpperInvariant();
        var altApt = altIcao != null ? Airport(altIcao) : null;
        if (altIcao != null && altApt == null)
            warnings.Add(new PlanText("alt", $"Unknown alternate {altIcao}: no alternate fuel", $"Неизвестный запасной {altIcao}: топливо до запасного не считается"));

        // Route variants.
        var routes = nav.FindRoutes(dep, dest, 3);
        int variant = Math.Clamp(q.Variant, 0, routes.Count - 1);
        var route = routes[variant];
        if (route.IsDirect && dep != dest)
            warnings.Add(new PlanText("dct", "No airway route found: direct", "Маршрут по трассам не найден: напрямую"));

        // Weather and runways in parallel; the layouts may take a while the first time (OpenStreetMap).
        DateTime offBlock = q.OffBlock?.ToUniversalTime() ?? RoundUp(DateTime.UtcNow.AddMinutes(30));
        var wxDep = WeatherAsync(dep, ct);
        var wxDest = WeatherAsync(dest, ct);
        var wxAlt = altApt != null ? WeatherAsync(altApt.Icao, ct) : Task.FromResult<(PlanWeather, DecodedMetar?)>(default);
        var layDep = LayoutAsync(dep);
        var layDest = LayoutAsync(dest);

        // Cruise level: semicircular rule, capped by the type's ceiling and by the distance.
        double gc = NavData.Distance(depApt.Lat, depApt.Lon, destApt.Lat, destApt.Lon);
        double course = Bearing(depApt.Lat, depApt.Lon, destApt.Lat, destApt.Lon);
        int ceilingFl = ac.CeilingFt > 0 ? ac.CeilingFt / 100 : 410;
        int fl = q.FlightLevel is > 0 ? Math.Min(q.FlightLevel.Value, ceilingFl) : AutoLevel(course, route.DistanceNm, ceilingFl);
        if (q.FlightLevel is > 0 && q.FlightLevel.Value > ceilingFl)
            warnings.Add(new PlanText("ceil", $"FL{q.FlightLevel} is above the ceiling of the type: FL{ceilingFl}", $"FL{q.FlightLevel} выше потолка типа: FL{ceilingFl}"));
        else if (q.FlightLevel is > 0 && !Semicircular(course, q.FlightLevel.Value))
            warnings.Add(new PlanText("semi", $"FL{q.FlightLevel} does not follow the semicircular rule for course {course:0}°", $"FL{q.FlightLevel} не по правилу полукруга для курса {course:0}°"));

        // Winds and temperatures aloft at the cruise level along the route (Open-Meteo, up to 50 points).
        var samples = Sample(route.Points, 50);
        IReadOnlyList<AloftPoint> winds = [];
        try { winds = await aloft.GetAloftAsync(samples, fl, offBlock.AddMinutes(TaxiMin + route.DistanceNm / 8), ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            log.LogWarning("Winds aloft for the planner: {Error}", e.Message);
        }
        bool haveWinds = winds.Count == samples.Count && winds.Count > 0;
        if (!haveWinds)
            warnings.Add(new PlanText("wind", "No winds aloft forecast: calm and ISA assumed", "Нет прогноза ветра на высотах: принят штиль и МСА"));

        // Masses.
        int pax = Math.Clamp(q.Pax ?? (int)Math.Round(ac.SeatsTypical * 0.8), 0, Math.Max(ac.SeatsTypical, 1) * 2);
        double bagPerPax = q.BaggagePerPaxKg ?? ac.BaggageMassKg;
        double cargo = Math.Max(0, q.CargoKg ?? 0);
        double paxKg = pax * ac.PaxMassKg, bagKg = pax * bagPerPax;
        double zfw = ac.OewKg + paxKg + bagKg + cargo;
        double mach = CruiseMach(ac, q.CostIndex);

        // Fuel and profile, iterated: the fuel on board changes the mass, the mass the fuel.
        double taxi = Math.Max(TaxiFf(ac), 1) * TaxiMin / 60;
        double extra = Math.Max(0, q.ExtraFuelKg);
        double alternateKg = 0, alternateMin = 0, finalKg = 0, contKg = 0, contMin = 0;
        string contRule = "5%";
        Flight? flight = null;
        double fob = ac.OewKg > 0 ? zfw * 0.1 : 1000; // first guess of the fuel at take-off
        for (int iter = 0; iter < 6; iter++)
        {
            double tow = zfw + fob;
            flight = Fly(route.Points, fl, mach, tow, ac, winds, haveWinds, samples);
            fl = flight.Level;
            double lw = tow - flight.TripKg;

            // Alternate: a missed approach is not modelled; cruise to it low and direct.
            if (altApt != null)
            {
                double altDist = NavData.Distance(destApt.Lat, destApt.Lon, altApt.Lat, altApt.Lon) * AlternateRouteFactor;
                double altCourse = Bearing(destApt.Lat, destApt.Lon, altApt.Lat, altApt.Lon);
                int altFl = Math.Min(AutoLevel(altCourse, altDist, ceilingFl), 250);
                var altFlight = Fly([new RoutePoint(dest, destApt.Lat, destApt.Lon, "", 0), new RoutePoint(altApt.Icao, altApt.Lat, altApt.Lon, "", 0)],
                    altFl, mach, lw, ac, [], false, [], altDist);
                alternateKg = altFlight.TripKg;
                alternateMin = altFlight.TripMin;
            }
            double holdFf = HoldingFf(ac, zfw + 0.3 * fob);
            finalKg = holdFf * 30 / 60;
            double fiveMin = holdFf * 5 / 60;
            contKg = Math.Max(flight.TripKg * 0.05, fiveMin);
            contRule = flight.TripKg * 0.05 >= fiveMin ? "5%" : "5 min";
            contMin = contKg / Math.Max(holdFf, 1) * 60;
            double next = flight.TripKg + contKg + alternateKg + finalKg + extra;
            if (Math.Abs(next - fob) < 5) { fob = next; break; }
            fob = next;
        }
        flight ??= Fly(route.Points, fl, mach, zfw + fob, ac, winds, haveWinds, samples);

        double block = taxi + fob;
        double towKg = zfw + fob, lwKg = towKg - flight.TripKg;
        double holdNow = HoldingFf(ac, zfw);
        double extraMin = extra / Math.Max(holdNow, 1) * 60;

        // Limits.
        if (ac.MfcKg > 0 && block > ac.MfcKg)
            warnings.Add(new PlanText("mfc", $"Block fuel {block:0} kg is more than the tanks hold ({ac.MfcKg:0} kg)", $"Топливо {block:0} кг больше ёмкости баков ({ac.MfcKg:0} кг)"));
        if (ac.MzfwKg > 0 && zfw > ac.MzfwKg)
            warnings.Add(new PlanText("mzfw", $"Zero fuel weight over the limit by {zfw - ac.MzfwKg:0} kg: take off payload", $"Масса без топлива выше предела на {zfw - ac.MzfwKg:0} кг: снимите загрузку"));
        if (ac.MtowKg > 0 && towKg > ac.MtowKg)
            warnings.Add(new PlanText("mtow", $"Take-off weight over the limit by {towKg - ac.MtowKg:0} kg", $"Взлётная масса выше предела на {towKg - ac.MtowKg:0} кг"));
        if (ac.MlwKg > 0 && lwKg > ac.MlwKg)
            warnings.Add(new PlanText("mlw", $"Landing weight over the limit by {lwKg - ac.MlwKg:0} kg", $"Посадочная масса выше предела на {lwKg - ac.MlwKg:0} кг"));
        foreach (var (name, m, lim) in new[] { ("ZFW", zfw, ac.MzfwKg), ("TOW", towKg, ac.MtowKg), ("LW", lwKg, ac.MlwKg) })
            if (lim > 0 && m <= lim && m >= lim * 0.98)
                warnings.Add(new PlanText("near-" + name, $"{name} within 2 % of the maximum", $"{name} у предела: {m / lim * 100:0} % максимума"));
        var margins = new[] { ("ZFW", ac.MzfwKg - zfw), ("TOW", ac.MtowKg - towKg), ("LW", ac.MlwKg - lwKg) };
        var limiting = margins.OrderBy(x => x.Item2).First();

        // Runways by wind.
        var (depWx, depMetar) = await wxDep;
        var (destWx, destMetar) = await wxDest;
        var (altWx, _) = await wxAlt;
        var depRwys = Runways(dep, await layDep, depMetar, q.DepartureRunway, ac, warnings, true);
        var arrRwys = Runways(dest, await layDest, destMetar, q.ArrivalRunway, ac, warnings, false);
        if (depMetar == null) warnings.Add(new PlanText("wx-dep", $"No METAR for {dep}", $"Нет METAR для {dep}"));
        if (destMetar == null) warnings.Add(new PlanText("wx-dest", $"No METAR for {dest}", $"Нет METAR для {dest}"));

        // Times.
        DateTime takeoff = offBlock.AddMinutes(TaxiMin);
        DateTime landing = takeoff.AddMinutes(flight.TripMin);
        DateTime onBlock = landing.AddMinutes(5);

        // Navigation log with times, fuel and the mass going down.
        var log2 = new List<PlanPoint>();
        double used = 0;
        foreach (var l in flight.Log)
        {
            used += l.LegFuel;
            int crs = (int)Math.Round(l.Course) % 360;
            log2.Add(new PlanPoint(l.Ident, l.Airway, l.Kind, Math.Round(l.Lat, 5), Math.Round(l.Lon, 5),
                crs == 0 && l.LegNm > 0 ? 360 : crs,
                Math.Round(l.LegNm, 1), Math.Round(l.CumNm, 1), Math.Round(flight.DistanceNm - l.CumNm, 1),
                (int)Math.Round(l.AltFt), l.WindDir, l.WindKt, l.TempC, l.IsaDev,
                (int)Math.Round(l.Tas), (int)Math.Round(l.Gs), Math.Round(l.LegMin, 1), Math.Round(l.CumMin, 1),
                takeoff.AddMinutes(l.CumMin), (int)Math.Round(l.LegFuel), (int)Math.Round(used),
                (int)Math.Round(fob - used)));
        }

        string depRwy = depRwys.Selected is { } dr ? "/" + dr : "";
        string arrRwy = arrRwys.Selected is { } ar ? "/" + ar : "";
        string fieldRoute = route.IsDirect ? "DCT" : route.Route;
        var result = new PlanResult(
            dep, dest, altApt?.Icao, depApt, destApt, altApt,
            ac.Icao, ac.Name, ac.Category, ac.Engine,
            string.IsNullOrWhiteSpace(q.Callsign) ? "" : q.Callsign.Trim().ToUpperInvariant(), AiracCycle(),
            $"{dep}{depRwy} {fieldRoute} {dest}{arrRwy}", fieldRoute,
            routes.Select(r => new PlanRouteOption(r.Route, Math.Round(r.DistanceNm), r.IsDirect)).ToList(), variant,
            Math.Round(flight.DistanceNm), Math.Round(gc), fl, q.CostIndex, Math.Round(mach, 3), (int)Math.Round(flight.CruiseTas),
            Math.Round(flight.AvgWindComponent), flight.AvgWindDir, flight.AvgWindKt, Math.Round(flight.AvgIsaDev, 1),
            Math.Round(flight.TocNm), Math.Round(flight.TodNm),
            offBlock, takeoff, landing, onBlock, Math.Round(flight.TripMin), Math.Round(flight.TripMin + TaxiMin + 5),
            log2,
            new PlanFuel((int)Math.Round(taxi), (int)Math.Round(flight.TripKg), (int)Math.Round(contKg), contRule,
                (int)Math.Round(alternateKg), (int)Math.Round(finalKg), (int)Math.Round(extra),
                (int)Math.Round(fob - extra), (int)Math.Round(block), (int)Math.Round(fob - flight.TripKg), (int)Math.Round(ac.MfcKg),
                Math.Round(flight.TripMin), Math.Round(contMin), Math.Round(alternateMin), 30, Math.Round(extraMin)),
            new PlanWeights((int)Math.Round(ac.OewKg), pax, (int)Math.Round(ac.PaxMassKg), (int)Math.Round(bagKg), (int)Math.Round(cargo),
                (int)Math.Round(paxKg + bagKg + cargo), (int)Math.Round(zfw), (int)Math.Round(ac.MzfwKg),
                (int)Math.Round(towKg), (int)Math.Round(ac.MtowKg), (int)Math.Round(lwKg), (int)Math.Round(ac.MlwKg),
                (int)Math.Round(limiting.Item2), limiting.Item1),
            depRwys, arrRwys, depWx, destWx, altApt != null ? altWx : null,
            altOptions, warnings, haveWinds);
        return (result, null);
    }

    // ── profile ──────────────────────────────────────────────────────────────

    private sealed record LogRow(string Ident, string Airway, string Kind, double Lat, double Lon, double Course,
        double LegNm, double CumNm, double AltFt, int? WindDir, int? WindKt, int? TempC, int? IsaDev,
        double Tas, double Gs, double LegMin, double CumMin, double LegFuel);

    private sealed record Flight(int Level, double DistanceNm, double TripKg, double TripMin, double TocNm, double TodNm,
        double CruiseTas, double AvgWindComponent, int? AvgWindDir, int? AvgWindKt, double AvgIsaDev, List<LogRow> Log);

    // One step of the climb or descent profile, from the runway: distance, altitude, time and fuel so far.
    private readonly record struct ProfileStep(double Nm, double AltFt, double Min, double Kg);

    /// <summary>
    /// Flies the route at the level: climb and descent integrated in 1000 ft steps (rate, speed and fuel flow of the
    /// type), cruise leg by leg with the wind and temperature of the nearest forecast point. Lowers the level when
    /// the climb and the descent do not fit into the distance.
    /// </summary>
    private Flight Fly(List<RoutePoint> points, int fl, double mach, double tow, AircraftProfile ac,
        IReadOnlyList<AloftPoint> winds, bool haveWinds, List<(double Lat, double Lon)> samples, double? distanceOverride = null)
    {
        // Legs of the route.
        var legs = new List<(RoutePoint A, RoutePoint B, double Nm, double Course)>();
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i];
            legs.Add((a, b, NavData.Distance(a.Lat, a.Lon, b.Lat, b.Lon), Bearing(a.Lat, a.Lon, b.Lat, b.Lon)));
        }
        double total = distanceOverride ?? legs.Sum(l => l.Nm);
        if (distanceOverride is { } over && legs.Count == 1) legs[0] = (legs[0].A, legs[0].B, over, legs[0].Course);
        if (total < 1)
            return new Flight(fl, total, 0, 0, 0, 0, 0, 0, null, null, 0,
                [.. points.Select(p => new LogRow(p.Ident, p.Airway, "apt", p.Lat, p.Lon, 0, 0, 0, 0, null, null, null, null, 0, 0, 0, 0, 0))]);

        (int Dir, double Kt, double Temp, double Isa)? WindAt(double lat, double lon)
        {
            if (!haveWinds) return null;
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < samples.Count; i++)
            {
                double d = NavData.Distance(lat, lon, samples[i].Lat, samples[i].Lon);
                if (d < bd) { bd = d; best = i; }
            }
            var w = winds[best];
            return (w.WindDirection, w.WindSpeedKt, w.TemperatureC, w.IsaDeviationC);
        }
        static double HeadComponent(int dir, double kt, double course) => kt * Math.Cos((dir - course) * Math.PI / 180);

        // Climb and descent profiles; the level comes down until both fit with some cruise between them.
        List<ProfileStep> climb, descent;
        var first = legs[0]; var last = legs[^1];
        while (true)
        {
            double cruiseFt = fl * 100.0;
            var w1 = WindAt(first.B.Lat, first.B.Lon);
            var w2 = WindAt(last.A.Lat, last.A.Lon);
            climb = Profile(ac, cruiseFt, tow, true, w1 is { } a ? HeadComponent(a.Dir, a.Kt, first.Course) : 0, w1?.Isa ?? 0);
            descent = Profile(ac, cruiseFt, tow * 0.9, false, w2 is { } b ? HeadComponent(b.Dir, b.Kt, last.Course) : 0, w2?.Isa ?? 0);
            if (climb[^1].Nm + descent[^1].Nm <= total * 0.9 || fl <= 30) break;
            fl = Math.Max(fl - 20, 30);
        }
        double tocNm = Math.Min(climb[^1].Nm, total);
        double todNm = Math.Max(total - descent[^1].Nm, tocNm);

        // Break the legs at TOC and TOD.
        var marks = new List<(double At, string Name)> { (tocNm, "TOC"), (todNm, "TOD") };
        var rows = new List<(string Ident, string Airway, string Kind, double Lat, double Lon, double Cum, double Course)>();
        double cum = 0;
        rows.Add((points[0].Ident, "", "apt", points[0].Lat, points[0].Lon, 0, legs[0].Course));
        for (int li = 0; li < legs.Count; li++)
        {
            var l = legs[li];
            foreach (var (at, name) in marks)
                if (at > cum + 0.5 && at < cum + l.Nm - 0.5)
                {
                    double f = (at - cum) / l.Nm;
                    var (la, lo) = Interpolate(l.A.Lat, l.A.Lon, l.B.Lat, l.B.Lon, f);
                    rows.Add((name, l.B.Airway, name.ToLowerInvariant(), la, lo, at, l.Course));
                }
            cum += l.Nm;
            rows.Add((l.B.Ident, l.B.Airway, li == legs.Count - 1 ? "apt" : "fix", l.B.Lat, l.B.Lon, cum, l.Course));
        }
        rows = [.. rows.OrderBy(r => r.Cum)];
        // TOC and TOD that fell exactly on a fix are still marked, on that fix's row.

        // Walk the rows.
        double cruiseFt2 = fl * 100.0;
        double mass = tow, min = 0, fuel = 0;
        double windSum = 0, windNm = 0, isaSum = 0, isaNm = 0, dirX = 0, dirY = 0, spdSum = 0;
        double cruiseTas = 0;
        var log = new List<LogRow> { new(rows[0].Ident, "", rows[0].Kind, rows[0].Lat, rows[0].Lon, rows[0].Course, 0, 0, 0,
            null, null, null, null, 0, 0, 0, 0, 0) };
        for (int i = 1; i < rows.Count; i++)
        {
            var from = rows[i - 1]; var to = rows[i];
            double legNm = to.Cum - from.Cum;
            double course = Bearing(from.Lat, from.Lon, to.Lat, to.Lon);
            if (legNm < 0.05) course = to.Course;
            var (mLat, mLon) = Interpolate(from.Lat, from.Lon, to.Lat, to.Lon, 0.5);
            var w = WindAt(mLat, mLon);
            double legMin = 0, legFuel = 0, tas = 0, gs = 0, altEnd;
            double a0 = from.Cum, a1 = to.Cum;

            // Part in the climb, in the cruise and in the descent (a leg can hold all three).
            double climbPart = Math.Max(0, Math.Min(a1, tocNm) - a0);
            double descPart = Math.Max(0, a1 - Math.Max(a0, todNm));
            double cruisePart = Math.Max(0, legNm - climbPart - descPart);
            if (climbPart > 0)
            {
                var s0 = At(climb, a0); var s1 = At(climb, Math.Min(a1, tocNm));
                legMin += s1.Min - s0.Min; legFuel += s1.Kg - s0.Kg;
            }
            if (cruisePart > 0)
            {
                double isa = w?.Isa ?? 0;
                tas = mach * SpeedOfSound(cruiseFt2, isa);
                double head = w is { } ww ? HeadComponent(ww.Dir, ww.Kt, course) : 0;
                gs = Math.Max(tas - head, tas * 0.3);
                double h = cruisePart / gs;
                double ff = AircraftService.FuelFlowKgH(ac, mass - legFuel, cruiseFt2, mach, "cruise", isa);
                legMin += h * 60; legFuel += ff * h;
                cruiseTas = tas;
                windSum += -head * cruisePart; windNm += cruisePart;
                isaSum += isa * cruisePart; isaNm += cruisePart;
                if (w is { } wv)
                {
                    dirX += Math.Sin(wv.Dir * Math.PI / 180) * wv.Kt * cruisePart;
                    dirY += Math.Cos(wv.Dir * Math.PI / 180) * wv.Kt * cruisePart;
                    spdSum += wv.Kt * cruisePart;
                }
            }
            if (descPart > 0)
            {
                // The descent profile is built from the runway up: distance to go.
                double r0 = total - Math.Max(a0, todNm), r1 = total - a1;
                var s0 = At(descent, r0); var s1 = At(descent, Math.Max(r1, 0));
                legMin += s0.Min - s1.Min; legFuel += s0.Kg - s1.Kg;
            }
            if (tas == 0 && legMin > 0) { gs = legNm / (legMin / 60); tas = gs; }
            if (tas > 0 && gs == 0) gs = legMin > 0 ? legNm / (legMin / 60) : tas;

            altEnd = a1 <= tocNm ? At(climb, a1).AltFt
                : a1 >= todNm ? At(descent, Math.Max(total - a1, 0)).AltFt
                : cruiseFt2;
            if (to.Kind == "apt") altEnd = 0;
            mass -= legFuel; min += legMin; fuel += legFuel;
            log.Add(new LogRow(to.Ident, to.Airway, to.Kind, to.Lat, to.Lon, course, legNm, to.Cum, altEnd,
                w?.Dir, w is { } wk ? (int)Math.Round(wk.Kt) : null,
                w is { } wt ? (int)Math.Round(wt.Temp + (cruiseFt2 - altEnd) / 1000 * 2) : null,
                w is { } wi ? (int)Math.Round(wi.Isa) : null,
                tas, gs, legMin, min, legFuel));
        }

        // Approach and landing: a few minutes at low level with flaps out, on the last leg.
        double apprKg = HoldingFf(ac, mass) / HoldingFactor * ApproachMin / 60;
        min += ApproachMin; fuel += apprKg;
        log[^1] = log[^1] with { LegMin = log[^1].LegMin + ApproachMin, CumMin = min, LegFuel = log[^1].LegFuel + apprKg };

        double avgDirDeg = (Math.Atan2(dirX, dirY) * 180 / Math.PI + 360) % 360;
        return new Flight(fl, total, fuel, min, tocNm, todNm, cruiseTas,
            windNm > 0 ? windSum / windNm : 0,
            spdSum > 0 ? (int)Math.Round(avgDirDeg) : null,
            windNm > 0 && spdSum > 0 ? (int)Math.Round(Math.Sqrt(dirX * dirX + dirY * dirY) / windNm) : null,
            isaNm > 0 ? isaSum / isaNm : 0, log);
    }

    /// <summary>
    /// Climb (from the runway up) or descent (from the runway up too, i.e. read backwards) to the cruise altitude
    /// in 1000 ft steps: vertical speed from the type's low and high rates, CAS (250 kt below FL100) turned into
    /// TAS until the climb Mach takes over, fuel flow of the phase at the current mass.
    /// </summary>
    private static List<ProfileStep> Profile(AircraftProfile ac, double cruiseFt, double mass, bool climb, double headKt, double isaDev)
    {
        var steps = new List<ProfileStep> { new(0, 0, 0, 0) };
        double ceiling = Math.Max(ac.CeilingFt, cruiseFt);
        double rateLo = Math.Abs(climb ? ac.ClimbRateLoFpm : ac.DescentRateLoFpm);
        double rateHi = Math.Abs(climb ? ac.ClimbRateHiFpm : ac.DescentRateHiFpm);
        if (rateLo <= 0) rateLo = 500; if (rateHi <= 0) rateHi = Math.Max(rateLo, 800);
        double casHi = climb ? ac.ClimbCasHiKts : ac.DescentCasHiKts;
        double casLo = climb ? ac.ClimbCasLoKts : ac.DescentCasLoKts;
        if (casHi <= 0) casHi = 250; if (casLo <= 0) casLo = casHi * 0.7;
        double machCap = climb ? ac.ClimbMach : ac.DescentMach;
        if (machCap <= 0) machCap = ac.CruiseMachTyp > 0 ? ac.CruiseMachTyp : 0.8;
        double nm = 0, min = 0, kg = 0, m = mass;
        const double step = 1000;
        for (double h = 0; h < cruiseFt - 1; h += step)
        {
            double dh = Math.Min(step, cruiseFt - h), mid = h + dh / 2;
            // Climb: fast near the ground, slower high up. Descent: the other way round.
            double f = mid / ceiling;
            double rate = climb ? rateHi - (rateHi - rateLo) * f : rateLo + (rateHi - rateLo) * f;
            rate = Math.Max(rate, 300);
            double cas = mid < 10000 ? Math.Min(250, casHi) : casHi;
            if (mid < 3000) cas = Math.Max(casLo, Math.Min(cas, casLo + 40));
            double tas = Math.Min(CasToTas(cas, mid, isaDev), machCap * SpeedOfSound(mid, isaDev));
            double gs = Math.Max(tas - headKt * mid / Math.Max(cruiseFt, 1), 60);
            double dt = dh / rate; // minutes
            double ff = AircraftService.FuelFlowKgH(ac, m, mid, tas, climb ? "climb" : "descent", isaDev);
            double dKg = ff * dt / 60;
            nm += gs * dt / 60; min += dt; kg += dKg; m += climb ? -dKg : dKg;
            steps.Add(new(nm, h + dh, min, kg));
        }
        return steps;
    }

    // Interpolates a profile at a distance from its start.
    private static ProfileStep At(List<ProfileStep> p, double nm)
    {
        if (nm <= 0) return p[0];
        for (int i = 1; i < p.Count; i++)
            if (p[i].Nm >= nm)
            {
                var a = p[i - 1]; var b = p[i];
                double f = b.Nm - a.Nm > 0 ? (nm - a.Nm) / (b.Nm - a.Nm) : 1;
                return new(nm, a.AltFt + (b.AltFt - a.AltFt) * f, a.Min + (b.Min - a.Min) * f, a.Kg + (b.Kg - a.Kg) * f);
            }
        return p[^1];
    }

    // ── levels, speeds, fuel flows ───────────────────────────────────────────

    /// <summary>
    /// Semicircular rule: eastbound (0–179°) odd thousands, westbound even, up to FL410 (RVSM); above it every
    /// 4000 ft — east FL450, 490…, west FL430, 470…
    /// </summary>
    public static bool Semicircular(double courseTrue, int fl)
    {
        bool east = courseTrue % 360 < 180;
        if (fl > 410) return east ? (fl - 450) % 40 == 0 : (fl - 430) % 40 == 0;
        return east ? fl % 20 == 10 : fl % 20 == 0;
    }

    /// <summary>The highest semicircular level under the ceiling and under what the distance allows.</summary>
    public static int AutoLevel(double courseTrue, double distanceNm, int ceilingFl)
    {
        int maxByDistance = (int)(60 + distanceNm * 0.8);
        int max = Math.Min(Math.Max(ceilingFl - 10, 30), Math.Max(maxByDistance, 50));
        for (int fl = max; fl >= 30; fl--)
            if (fl % 10 == 0 && Semicircular(courseTrue, fl)) return fl;
        return 30;
    }

    /// <summary>Cost index 0 flies the low end of the cruise Mach range of the type, 100 and more the high end.</summary>
    public static double CruiseMach(AircraftProfile ac, int costIndex)
    {
        double lo = ac.CruiseMachLo > 0 ? ac.CruiseMachLo : ac.CruiseMachTyp;
        double hi = ac.CruiseMachHi > 0 ? ac.CruiseMachHi : ac.CruiseMachTyp;
        if (lo <= 0) lo = hi = 0.3;
        return lo + (hi - lo) * Math.Clamp(costIndex / 100.0, 0, 1);
    }

    private static double TaxiFf(AircraftProfile ac) =>
        Math.Max(ac.NomCruiseFfKgH * (ac.PhaseFf.TryGetValue("taxi", out var t) ? t : 0.05), ac.NomCruiseFfKgH * 0.28);

    private static double HoldingFf(AircraftProfile ac, double mass) =>
        AircraftService.FuelFlowKgH(ac, mass, HoldAltFt, Math.Min(HoldTasKt, Math.Max(ac.ClimbCasLoKts, 90)), "cruise") * HoldingFactor;

    /// <summary>Speed of sound in knots at a pressure altitude and ISA deviation.</summary>
    public static double SpeedOfSound(double altFt, double isaDev)
    {
        double t = WindsAloftService.IsaTempC(altFt) + isaDev + 273.15;
        return 38.967854 * Math.Sqrt(t);
    }

    private static double CasToTas(double cas, double altFt, double isaDev)
    {
        double delta = AircraftService.DeltaAt(altFt);
        double theta = (WindsAloftService.IsaTempC(altFt) + isaDev + 273.15) / 288.15;
        double sigma = delta / theta;
        return cas / Math.Sqrt(Math.Max(sigma, 0.05));
    }

    // ── runways ──────────────────────────────────────────────────────────────

    private static int MinRunwayM(string category) => category switch
    {
        "ga" => 500, "light" => 1200, "medium" => 1800, "heavy" => 2400, "super" => 2800, _ => 1500,
    };

    private static double CrosswindLimitKt(string category) => category switch { "ga" => 15, "light" => 25, _ => 30 };

    private PlanRunways Runways(string icao, string? layoutJson, DecodedMetar? m, string? wanted, AircraftProfile ac,
        List<PlanText> warnings, bool departure)
    {
        var list = new List<PlanRunway>();
        var lines = new List<double[][]>();
        var wind = m?.Wind;
        double? wdir = wind is { Variable: false } ? wind.Direction : null;
        double wkt = wind?.SpeedKt ?? 0;
        double? gust = wind?.GustKt;
        if (layoutJson != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(layoutJson);
                if (doc.RootElement.TryGetProperty("runways", out var rwys))
                    foreach (var r in rwys.EnumerateArray())
                    {
                        if (!r.TryGetProperty("line", out var line) || line.GetArrayLength() < 2) continue;
                        var pts = line.EnumerateArray().Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() }).ToArray();
                        lines.Add(pts);
                        var p0 = pts[0]; var p1 = pts[^1];
                        double hdg = Bearing(p0[0], p0[1], p1[0], p1[1]);
                        int length = (int)Math.Round(NavData.Distance(p0[0], p0[1], p1[0], p1[1]) * 1852);
                        int width = r.TryGetProperty("width", out var wd) && wd.ValueKind == JsonValueKind.Number ? (int)Math.Round(wd.GetDouble()) : 45;
                        string rf = r.TryGetProperty("ref", out var rr) ? rr.GetString() ?? "" : "";
                        var ids = rf.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (ids.Length == 0) continue;
                        string a = ids[0], b = ids.Length > 1 ? ids[1] : Opposite(a);
                        // Which end is which: the number is the (magnetic) heading in tens of degrees.
                        bool aForward = !int.TryParse(new string(a.TakeWhile(char.IsDigit).ToArray()), out int na)
                            || Math.Abs(AngleDiff(hdg, na * 10)) <= 90;
                        var (aThr, aEnd, aHdg) = aForward ? (p0, p1, hdg) : (p1, p0, (hdg + 180) % 360);
                        var (bThr, bEnd, bHdg) = aForward ? (p1, p0, (hdg + 180) % 360) : (p0, p1, hdg);
                        foreach (var (id, thr, end, h) in new[] { (a, aThr, aEnd, aHdg), (b, bThr, bEnd, bHdg) })
                        {
                            double head = wdir is { } d ? wkt * Math.Cos((d - h) * Math.PI / 180) : 0;
                            double cross = wdir is { } d2 ? wkt * Math.Sin((d2 - h) * Math.PI / 180) : 0;
                            bool usable = length >= MinRunwayM(ac.Category) * (departure ? 1.0 : 0.8);
                            list.Add(new PlanRunway(id, Math.Round(h), length, width, Math.Round(head, 1), Math.Round(cross, 1), usable, thr, end));
                        }
                    }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or IndexOutOfRangeException)
            {
                log.LogWarning("Runways of {Icao}: {Error}", icao, e.Message);
            }
        }
        // Several OSM ways can make one runway: keep the longest per ident.
        list = [.. list.GroupBy(r => r.Ident).Select(g => g.OrderByDescending(r => r.LengthM).First()).OrderBy(r => r.Ident)];

        string? selected = null;
        if (!string.IsNullOrWhiteSpace(wanted) && list.Any(r => r.Ident.Equals(wanted.Trim(), StringComparison.OrdinalIgnoreCase)))
            selected = list.First(r => r.Ident.Equals(wanted.Trim(), StringComparison.OrdinalIgnoreCase)).Ident;
        else if (list.Count > 0)
            selected = list.OrderByDescending(r => r.Usable).ThenByDescending(r => r.HeadwindKt).ThenByDescending(r => r.LengthM).First().Ident;

        if (selected != null && list.FirstOrDefault(r => r.Ident == selected) is { } s)
        {
            string what = departure ? "Departure" : "Arrival", whatRu = departure ? "Вылет" : "Посадка";
            if (s.HeadwindKt < -0.5)
                warnings.Add(new PlanText("tail", $"{what} {icao} {s.Ident}: tailwind {-s.HeadwindKt:0} kt", $"{whatRu} {icao} {s.Ident}: попутный ветер {-s.HeadwindKt:0} уз"));
            double gustCross = gust is { } g && wdir is { } d3 ? Math.Abs(g * Math.Sin((d3 - s.HeadingTrue) * Math.PI / 180)) : Math.Abs(s.CrosswindKt);
            if (Math.Max(Math.Abs(s.CrosswindKt), gustCross) > CrosswindLimitKt(ac.Category))
                warnings.Add(new PlanText("cross", $"{what} {icao} {s.Ident}: crosswind {Math.Max(Math.Abs(s.CrosswindKt), gustCross):0} kt", $"{whatRu} {icao} {s.Ident}: боковой ветер {Math.Max(Math.Abs(s.CrosswindKt), gustCross):0} уз"));
            if (!s.Usable)
                warnings.Add(new PlanText("short", $"{what} {icao} {s.Ident}: runway {s.LengthM} m may be short for the type", $"{whatRu} {icao} {s.Ident}: полоса {s.LengthM} м может быть коротка для типа"));
        }
        return new PlanRunways(icao, list.Count > 0, selected, list, lines, wdir, wind != null ? Math.Round(wkt, 1) : null, gust, wind?.Variable == true);
    }

    private static string Opposite(string ident)
    {
        string digits = new(ident.TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out int n)) return ident + "'";
        int o = (n + 18 - 1) % 36 + 1;
        string side = ident[digits.Length..] switch { "L" => "R", "R" => "L", var x => x };
        return o.ToString("00") + side;
    }

    private async Task<string?> LayoutAsync(string icao)
    {
        // The first fetch of an airport can take long: wait a little, let it finish and be cached for the next plan.
        var task = layouts.GetAsync(icao, CancellationToken.None);
        try
        {
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) == task) return await task;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            log.LogWarning("Layout of {Icao} for the planner: {Error}", icao, e.Message);
        }
        return null;
    }

    private async Task<(PlanWeather, DecodedMetar?)> WeatherAsync(string icao, CancellationToken ct)
    {
        string? rawMetar = null, rawTaf = null;
        try
        {
            var mt = metar.GetAsync(icao, ct);
            var tt = metar.GetTafAsync(icao, ct);
            rawMetar = await mt;
            rawTaf = await tt;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Weather of {Icao} for the planner: {Error}", icao, e.Message);
        }
        DecodedMetar? dm = null;
        DecodedTaf? dt = null;
        try { if (!string.IsNullOrWhiteSpace(rawMetar)) dm = WeatherDecoder.DecodeMetar(rawMetar); } catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException) { }
        try { if (!string.IsNullOrWhiteSpace(rawTaf)) dt = WeatherDecoder.DecodeTaf(rawTaf); } catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException) { }
        return (new PlanWeather(icao,
            string.IsNullOrWhiteSpace(rawMetar) ? null : rawMetar, dm?.HumanRu, dm?.HumanEn, dm?.FlightCategory,
            dm?.ObsTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            string.IsNullOrWhiteSpace(rawTaf) ? null : rawTaf, dt?.HumanRu, dt?.HumanEn), dm);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string AiracCycle()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(env.ContentRootPath, "Nav", "airac", "cycle.json")));
            return doc.RootElement.GetProperty("cycle").GetString() ?? "";
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "";
        }
    }

    private static List<(double Lat, double Lon)> Sample(List<RoutePoint> points, int max)
    {
        if (points.Count <= max) return [.. points.Select(p => (p.Lat, p.Lon))];
        var list = new List<(double, double)>();
        for (int i = 0; i < max; i++)
        {
            var p = points[(int)Math.Round(i * (points.Count - 1) / (double)(max - 1))];
            list.Add((p.Lat, p.Lon));
        }
        return list;
    }

    private static DateTime RoundUp(DateTime t) =>
        new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc).AddMinutes(Math.Ceiling(t.Minute / 5.0) * 5);

    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        double r = Math.PI / 180;
        double y = Math.Sin((lon2 - lon1) * r) * Math.Cos(lat2 * r);
        double x = Math.Cos(lat1 * r) * Math.Sin(lat2 * r) - Math.Sin(lat1 * r) * Math.Cos(lat2 * r) * Math.Cos((lon2 - lon1) * r);
        return (Math.Atan2(y, x) / r + 360) % 360;
    }

    private static double AngleDiff(double a, double b) => ((a - b + 540) % 360) - 180;

    private static (double Lat, double Lon) Interpolate(double lat1, double lon1, double lat2, double lon2, double f)
    {
        double dLon = lon2 - lon1;
        if (dLon > 180) dLon -= 360; else if (dLon < -180) dLon += 360;
        double lon = lon1 + dLon * f;
        if (lon > 180) lon -= 360; else if (lon < -180) lon += 360;
        return (lat1 + (lat2 - lat1) * f, lon);
    }
}
