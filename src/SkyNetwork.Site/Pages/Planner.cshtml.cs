using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Pages;

/// <summary>Light flight planner: route suggestion, cruise level, fuel and time estimates.</summary>
public sealed partial class PlannerModel(NavData nav) : PageModel
{
    [GeneratedRegex(@"^[A-Z0-9]{3,4}$")] private static partial Regex IcaoRe();
    [GeneratedRegex(@"^[A-Z0-9]{1,8}$")] private static partial Regex TypeRe();

    [BindProperty(SupportsGet = true)] public string Departure { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Destination { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string AircraftType { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Alternate { get; set; } = "";

    public NavData.FindRouteResult? Route { get; private set; }
    public AircraftPerf.FlightEstimates? Estimates { get; private set; }
    public string? Error { get; private set; }

    public void OnGet()
    {
        if (string.IsNullOrWhiteSpace(Departure)) return;

        Departure = Departure.Trim().ToUpperInvariant();
        Destination = Destination.Trim().ToUpperInvariant();
        AircraftType = AircraftType.Trim().ToUpperInvariant();
        Alternate = Alternate.Trim().ToUpperInvariant();

        if (!IcaoRe().IsMatch(Departure) || !IcaoRe().IsMatch(Destination))
        {
            Error = "Invalid airport code — use 3 or 4 letter ICAO codes.";
            return;
        }
        if (AircraftType.Length > 0 && !TypeRe().IsMatch(AircraftType))
        {
            Error = "Invalid aircraft type code.";
            return;
        }
        if (AircraftType.Length == 0) AircraftType = "A320";

        Route = nav.FindRoute(Departure, Destination);
        var perf = AircraftPerf.Get(AircraftType);
        double depLat = Route.Points.Count > 0 ? Route.Points[0].Lat : 0;
        double depLon = Route.Points.Count > 0 ? Route.Points[0].Lon : 0;
        double destLat = Route.Points.Count > 0 ? Route.Points[^1].Lat : 0;
        double destLon = Route.Points.Count > 0 ? Route.Points[^1].Lon : 0;
        Estimates = AircraftPerf.Estimate(Route.DistanceNm, perf, Alternate, depLat, depLon, destLat, destLon);
    }

    public string EteFormatted => Estimates is { } e
        ? $"{e.EteMinutes / 60}:{e.EteMinutes % 60:D2}"
        : "";
}
