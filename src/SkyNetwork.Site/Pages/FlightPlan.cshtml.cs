using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Pages;

/// <summary>Flight plan filing; SkyPilot opens /flightplan?callsign=… and reads /api/flightplans/latest.</summary>
public sealed partial class FlightPlanModel(CurrentUser me, FlightPlanService plans, Simbrief simbrief) : PageModel
{
    private const string SimbriefCookie = "simbrief";

    [BindProperty] public FlightPlan Plan { get; set; } = new();
    /// <summary>Time en route and fuel as typed: "02:20", "0220" or minutes (see <see cref="Duration"/>).</summary>
    [BindProperty] public string Enroute { get; set; } = "";
    [BindProperty] public string Fuel { get; set; } = "";
    /// <summary>Cruise speed as typed: "450", "450 kt", "N0450" (knots) or "K0830" (km/h).</summary>
    [BindProperty] public string Speed { get; set; } = "";
    public bool Saved { get; private set; }
    public string? Error { get; private set; }
    public bool Imported { get; private set; }
    /// <summary>SimBrief username or Pilot ID, remembered after the first import.</summary>
    public string SimbriefUser { get; private set; } = "";

    public void OnGet(string? callsign, int? saved,
        string? dep, string? dest, string? type, string? route, string? level, string? alt, int? speed)
    {
        Saved = saved == 1;
        SimbriefUser = Request.Cookies[SimbriefCookie] ?? plans.SimbriefUser(me.Cid);
        // Start from the last plan: most flights are re-filed with small changes.
        Plan = plans.Latest(me.Cid) ?? new FlightPlan { Remarks = "/V/" };
        if (!string.IsNullOrWhiteSpace(callsign)) Plan.Callsign = callsign.Trim().ToUpperInvariant();
        // Prefill from the planner (/planner passes dep, dest, type, route, level, alt, speed).
        if (!string.IsNullOrWhiteSpace(dep)) Plan.Departure = dep.Trim().ToUpperInvariant()[..Math.Min(dep.Trim().Length, 4)];
        if (!string.IsNullOrWhiteSpace(dest)) Plan.Destination = dest.Trim().ToUpperInvariant()[..Math.Min(dest.Trim().Length, 4)];
        if (!string.IsNullOrWhiteSpace(type)) Plan.Aircraft = type.Trim().ToUpperInvariant()[..Math.Min(type.Trim().Length, 8)];
        if (!string.IsNullOrWhiteSpace(route)) Plan.Route = route.Trim()[..Math.Min(route.Trim().Length, 1000)];
        if (!string.IsNullOrWhiteSpace(level)) Plan.CruiseAltitude = level.Trim().ToUpperInvariant()[..Math.Min(level.Trim().Length, 6)];
        if (!string.IsNullOrWhiteSpace(alt)) Plan.Alternate = alt.Trim().ToUpperInvariant()[..Math.Min(alt.Trim().Length, 4)];
        if (speed is > 0 and <= 999) Plan.CruiseSpeed = speed.Value;
        ShowDurations();
    }

    private void ShowDurations()
    {
        Enroute = Duration.Format(Plan.EnrouteMinutes);
        Fuel = Duration.Format(Plan.FuelMinutes);
        Speed = Plan.CruiseSpeed > 0 ? Plan.CruiseSpeed.ToString() : "";
    }

    /// <summary>Fills the form from the latest SimBrief plan; the pilot checks it and files it as usual.</summary>
    public async Task<IActionResult> OnPostSimbriefAsync(string? simbriefUser, CancellationToken ct)
    {
        SimbriefUser = (simbriefUser ?? "").Trim();
        var current = plans.Latest(me.Cid);
        var (imported, error) = await simbrief.FetchAsync(SimbriefUser, ct);
        if (imported == null)
        {
            Error = error;
            Plan = current ?? new FlightPlan { Remarks = "/V/" };
            ShowDurations();
            return Page();
        }
        Response.Cookies.Append(SimbriefCookie, SimbriefUser, new CookieOptions
        {
            MaxAge = TimeSpan.FromDays(365), HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps,
        });
        // Remembered for the map: it finds this member's SimBrief route by itself from now on.
        plans.SetSimbriefUser(me.Cid, SimbriefUser);
        imported.Remarks = current?.Remarks is { Length: > 0 } remarks ? remarks : "/V/";
        Plan = imported;
        ShowDurations();
        Imported = true;
        return Page();
    }

    public IActionResult OnPost()
    {
        var p = Plan;
        // Empty inputs arrive as null.
        static string U(string? v) => (v ?? "").Trim().ToUpperInvariant();
        p.Cid = me.Cid;
        p.Callsign = U(p.Callsign);
        p.Rules = p.Rules == "VFR" ? "VFR" : "IFR";
        p.Aircraft = U(p.Aircraft);
        p.Departure = U(p.Departure);
        p.Destination = U(p.Destination);
        p.Alternate = U(p.Alternate);
        // "12:00", "1200Z", "12 00" are all 1200.
        p.DepartureTime = Regex.Replace(U(p.DepartureTime), @"[\s:]|Z$", "");
        // "FL 350", "fl350" are FL350; metric levels as S1010 (tens of metres) are kept.
        p.CruiseAltitude = Regex.Replace(U(p.CruiseAltitude), @"\s+", "");
        if (Speed.Trim().Length > 0)
        {
            if (!TryParseSpeed(Speed, out var knots))
            {
                Error = "Speed: knots, for example 450 or N0450";
                return Page();
            }
            p.CruiseSpeed = knots;
        }
        p.Route = Regex.Replace(U(p.Route), @"\s+", " ");
        p.Remarks = (p.Remarks ?? "").Trim();
        p.Waypoints = Simbrief.Clean(p.Waypoints);
        SimbriefUser = Request.Cookies[SimbriefCookie] ?? "";
        if (!Duration.TryParseMinutes(Enroute, out var enroute) || !Duration.TryParseMinutes(Fuel, out var fuel))
        {
            Error = "Time en route and fuel: hours:minutes, for example 02:20";
            return Page();
        }
        p.EnrouteMinutes = enroute;
        p.FuelMinutes = fuel;
        Error = Validate(p);
        if (Error != null) return Page();
        plans.File(p);
        return Redirect("/flightplan?saved=1");
    }

    [GeneratedRegex("^[A-Z0-9]{2,10}$")] private static partial Regex Callsign();
    [GeneratedRegex("^[A-Z0-9]{4}$")] private static partial Regex Icao();
    [GeneratedRegex("^([01][0-9]|2[0-3])[0-5][0-9]$")] private static partial Regex Hhmm();
    [GeneratedRegex("^(FL[0-9]{2,3}|[0-9]{3,5}|[AF][0-9]{3}|[SM][0-9]{4})$")] private static partial Regex Level();
    [GeneratedRegex(@"^([NK])?0*([0-9]{2,4})(KTS?|KT|KMH|KM/H)?$")] private static partial Regex SpeedText();

    /// <summary>Knots from "450", "450 KT", "N0450" or "K0830" (km/h); Mach numbers are not accepted.</summary>
    public static bool TryParseSpeed(string text, out int knots)
    {
        knots = 0;
        var m = SpeedText().Match(Regex.Replace(text.Trim().ToUpperInvariant(), @"\s+", ""));
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var value)) return false;
        bool kmh = m.Groups[1].Value == "K" || m.Groups[3].Value.StartsWith("KM");
        knots = kmh ? (int)Math.Round(value / 1.852) : value;
        return true;
    }

    private static string? Validate(FlightPlan p)
    {
        if (!Callsign().IsMatch(p.Callsign)) return "Callsign: 2–10 Latin letters and digits";
        if (p.Aircraft.Length < 2 || p.Aircraft.Length > 8) return "Enter the ICAO aircraft type code";
        if (p.CruiseSpeed is < 30 or > 3000) return "Speed: 30 to 3000 knots";
        if (!Icao().IsMatch(p.Departure) || !Icao().IsMatch(p.Destination)) return "Airports: 4-letter ICAO codes";
        if (p.Alternate.Length > 0 && !Icao().IsMatch(p.Alternate)) return "Alternate: a 4-letter ICAO code";
        if (!Hhmm().IsMatch(p.DepartureTime)) return "Departure time: HHMM in UTC, for example 1200";
        if (!Level().IsMatch(p.CruiseAltitude)) return "Cruise level: for example FL350, 9000 or S1010";
        if (p.EnrouteMinutes is < 1 or > 2400 || p.FuelMinutes is < 1 or > 3000) return "Check the en route time and fuel";
        if (p.FuelMinutes < p.EnrouteMinutes) return "Less fuel than en route time";
        if (p.Route.Length == 0 || p.Route.Contains(':') || p.Remarks.Contains(':')) return "Route and remarks cannot contain colons";
        return null;
    }
}
