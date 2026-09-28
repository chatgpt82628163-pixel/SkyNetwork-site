using SkyNetwork.Site.Localization;

namespace SkyNetwork.Site.Tests;

public class ChartFoxTests
{
    [Fact]
    public void MapTextsContainsChartsKey()
    {
        Assert.Contains("Charts", MapTexts.Keys);
    }

    [Fact]
    public async Task FlightPlanPageShowsChartFoxLinkForValidIcao()
    {
        using var site = new SiteFactory();
        long cid = site.Member("Pilot Charts");
        var c = site.Browser();
        await c.LoginAsync(cid);

        // File a plan with UUEE departure and ULLI destination.
        var plan = new Dictionary<string, string>
        {
            ["Plan.Callsign"] = "AFL001", ["Plan.Rules"] = "IFR", ["Plan.Aircraft"] = "B738", ["Plan.CruiseSpeed"] = "460",
            ["Plan.Departure"] = "UUEE", ["Plan.Destination"] = "ULLI", ["Plan.Alternate"] = "ULLO", ["Plan.DepartureTime"] = "1000",
            ["Plan.CruiseAltitude"] = "FL350", ["Enroute"] = "0110", ["Fuel"] = "0200",
            ["Plan.Route"] = "EVINA M858 NATOR", ["Plan.Remarks"] = "",
        };
        var r = await c.SubmitAsync("/flightplan", plan);
        // After filing, the redirect goes back to the flight plan page with the saved data.
        var html = await c.HtmlAsync("/flightplan");
        Assert.Contains("chartfox.org/UUEE", html);
        Assert.Contains("chartfox.org/ULLI", html);
        Assert.Contains("chartfox.org/ULLO", html);
    }
}
