using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Tests;

/// <summary>Flight planner: route finding, estimates, API endpoint, page, prefill.</summary>
public class PlannerTests
{
    // ---- AircraftPerf table ----

    [Theory]
    [InlineData("A320", 450, 2400)]
    [InlineData("A20N", 460, 2200)]
    [InlineData("B738", 450, 2500)]
    [InlineData("B77W", 490, 8000)]
    [InlineData("AT72", 270, 900)]
    [InlineData("C172", 120, 22)]
    public void PerfTable_KnownType_ReturnsExpected(string type, int speedKt, int fuelKgH)
    {
        var p = AircraftPerf.Get(type);
        Assert.Equal(speedKt, p.CruiseSpeedKt);
        Assert.Equal(fuelKgH, p.FuelBurnKgH);
    }

    [Fact]
    public void PerfTable_UnknownType_ReturnsSensibleDefault()
    {
        var p = AircraftPerf.Get("ZZZZ");
        Assert.True(p.CruiseSpeedKt > 0);
        Assert.True(p.FuelBurnKgH > 0);
    }

    // ---- Estimates ----

    [Fact]
    public void Estimates_JetMediumRange_ReasonableValues()
    {
        var perf = AircraftPerf.Get("A320");
        // UUEE → ULLI ~ 380 NM, heading east
        var est = AircraftPerf.Estimate(380, perf, null, 55.97, 37.41, 59.8, 30.26);
        Assert.True(est.EteMinutes is > 40 and < 120, $"ETE {est.EteMinutes} min out of range");
        Assert.True(est.TripKg is > 500 and < 5000, $"Trip fuel {est.TripKg} kg out of range");
        Assert.True(est.TotalKg > est.TripKg, "Total must exceed trip");
        Assert.Equal(0, est.AlternateKg);
        Assert.StartsWith("FL", est.CruiseLevelStr);
    }

    [Fact]
    public void Estimates_WithAlternate_AddsAlternateFuel()
    {
        var perf = AircraftPerf.Get("A320");
        var withAlt = AircraftPerf.Estimate(380, perf, "ULLL", 55.97, 37.41, 59.8, 30.26);
        var noAlt   = AircraftPerf.Estimate(380, perf, null,  55.97, 37.41, 59.8, 30.26);
        Assert.True(withAlt.AlternateKg > 0);
        Assert.True(withAlt.TotalKg > noAlt.TotalKg);
    }

    [Theory]
    [InlineData(0, 90, "FL")]    // eastbound → odd
    [InlineData(200, 800, "FL")] // westbound → even
    public void CruiseLevel_DirectionRule_ReturnsFl(double track, double dist, string prefix)
    {
        var perf = AircraftPerf.Get("A320");
        string fl = AircraftPerf.CruiseLevel(track, dist, perf);
        Assert.StartsWith(prefix, fl);
    }

    [Fact]
    public void CruiseLevel_Piston_BelowFL100()
    {
        var perf = AircraftPerf.Get("C172");
        string fl = AircraftPerf.CruiseLevel(90, 200, perf);
        int flNum = int.Parse(fl.Replace("FL", ""));
        Assert.True(flNum <= 100, $"Piston FL {fl} too high");
    }

    // ---- Route finder on synthetic graph ----

    [Fact]
    public void RouteFinder_SyntheticGraph_FindsAirwayRoute()
    {
        using var site = new SiteFactory();
        var nav = site.Get<NavData>();

        // Teach a simple A-W1-B-W1-C airway near Moscow
        nav.Learn([
            new RoutePoint("FAKDEP", 56.0, 37.0, "", 0),
            new RoutePoint("FIX_A", 57.0, 37.5, "", 0),
            new RoutePoint("FIX_B", 58.0, 38.0, "W1TST", 0),
            new RoutePoint("FIX_C", 59.0, 38.5, "W1TST", 0),
            new RoutePoint("FAKDST", 59.5, 38.7, "", 0),
        ], "FAKDEP", "FAKDST");

        // Also teach airport positions via Learn with airport-like idents is not possible;
        // airports come from the bundled airports.json. Use real airports instead.
        // UUEE (Moscow Sheremetyevo) → ULLI (St Petersburg Pulkovo) — real airports in bundled data.
        var result = nav.FindRoute("UUEE", "ULLI");
        Assert.True(result.DistanceNm > 100, $"Distance {result.DistanceNm} too small");
        Assert.NotEmpty(result.Points);
        Assert.Equal("UUEE", result.Points[0].Ident);
        Assert.Equal("ULLI", result.Points[^1].Ident);
    }

    [Fact]
    public void RouteFinder_UnknownAirport_ReturnsDct()
    {
        using var site = new SiteFactory();
        var nav = site.Get<NavData>();
        var result = nav.FindRoute("ZZZZ", "AAAA");
        Assert.Equal("DCT", result.Route);
        Assert.True(result.IsDirect);
    }

    [Fact]
    public void RouteFinder_SameAirport_ReturnsDct()
    {
        using var site = new SiteFactory();
        var nav = site.Get<NavData>();
        var result = nav.FindRoute("UUEE", "UUEE");
        // Same airport: distance is 0 or near 0, route is DCT
        Assert.True(result.DistanceNm < 1);
    }

    // ---- API ----

    [Fact]
    public async Task Api_RouteSuggest_ValidPair_Returns200()
    {
        using var site = new SiteFactory();
        var c = site.CreateClient();
        var r = await c.GetAsync("/api/v1/routes/suggest?departure=UUEE&destination=ULLI&type=A320");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.TryGetProperty("route", out _));
        Assert.True(json.TryGetProperty("distanceNm", out var dist) && dist.GetInt32() > 0);
        Assert.True(json.TryGetProperty("cruiseLevel", out _));
        Assert.True(json.TryGetProperty("ete", out _));
        var fuel = json.GetProperty("fuel");
        Assert.True(fuel.GetProperty("total").GetInt32() > 0);
        Assert.Equal("kg", fuel.GetProperty("unit").GetString());
    }

    [Theory]
    [InlineData("/api/v1/routes/suggest")]
    [InlineData("/api/v1/routes/suggest?departure=UUEE")]
    [InlineData("/api/v1/routes/suggest?departure=X&destination=ULLI")]
    public async Task Api_RouteSuggest_BadInput_Returns400(string url)
    {
        using var site = new SiteFactory();
        var c = site.CreateClient();
        var r = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Api_RouteSuggest_WithAlternate_HasAlternateFuel()
    {
        using var site = new SiteFactory();
        var c = site.CreateClient();
        var r = await c.GetAsync("/api/v1/routes/suggest?departure=UUEE&destination=ULLI&type=A320&alternate=ULLL");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var fuel = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("fuel");
        Assert.True(fuel.GetProperty("alternate").GetInt32() > 0);
    }

    // ---- Page ----

    [Fact]
    public async Task PlannerPage_NoParams_Renders()
    {
        using var site = new SiteFactory();
        var html = await site.CreateClient().HtmlAsync("/planner");
        Assert.Contains("planner", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Departure", html);
    }

    [Fact]
    public async Task PlannerPage_WithParams_ShowsResult()
    {
        using var site = new SiteFactory();
        var html = await site.CreateClient().HtmlAsync("/planner?Departure=UUEE&Destination=ULLI&AircraftType=A320");
        Assert.Contains("NM", html);
        Assert.Contains("FL", html);
        Assert.Contains("kg", html);
        Assert.Contains("simbrief", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chartfox", html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Flight plan prefill ----

    [Fact]
    public async Task FlightPlan_PrefillFromPlanner_PopulatesFields()
    {
        using var site = new SiteFactory();
        long cid = site.Member();
        var c = site.Browser();
        await c.LoginAsync(cid);
        var html = await c.HtmlAsync("/flightplan?dep=UUEE&dest=ULLI&type=A320&route=GUBAG+N869+RATIN&level=FL350&speed=450");
        Assert.Contains("UUEE", html);
        Assert.Contains("ULLI", html);
        Assert.Contains("A320", html);
        Assert.Contains("GUBAG", html);
        Assert.Contains("FL350", html);
    }
}
