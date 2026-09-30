using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SkyNetwork.Site.Pages;

namespace SkyNetwork.Site.Tests;

/// <summary>The flight plan form takes what pilots actually type: N0450, 12:00, FL 350, metric levels.</summary>
public class FlightPlanFormTests
{
    [Theory]
    [InlineData("450", 450)]
    [InlineData("450 kt", 450)]
    [InlineData("N0450", 450)]
    [InlineData("n450", 450)]
    [InlineData("K0830", 448)]
    [InlineData("830 km/h", 448)]
    public void Speed_IsReadInKnots(string text, int knots)
    {
        Assert.True(FlightPlanModel.TryParseSpeed(text, out var value));
        Assert.Equal(knots, value);
    }

    [Theory]
    [InlineData("M078")]
    [InlineData("fast")]
    [InlineData("")]
    public void Speed_RejectsWhatIsNotKnots(string text) => Assert.False(FlightPlanModel.TryParseSpeed(text, out _));

    [Fact]
    public async Task Form_AcceptsCommonFormats()
    {
        using var site = new SiteFactory();
        long cid = site.Member("Pilot One");
        var c = site.Browser();
        await c.LoginAsync(cid);
        var plan = new Dictionary<string, string>
        {
            ["Plan.Callsign"] = "AFL1492", ["Plan.Rules"] = "IFR", ["Plan.Aircraft"] = "A20N", ["Speed"] = "N0450",
            ["Plan.Departure"] = "UUEE", ["Plan.Destination"] = "URSS", ["Plan.Alternate"] = "", ["Plan.DepartureTime"] = "12:00",
            ["Plan.CruiseAltitude"] = "S1010", ["Enroute"] = "2:10", ["Fuel"] = "03:30",
            ["Plan.Route"] = "OKLOT R114 LAMKA", ["Plan.Remarks"] = "/V/",
        };
        var r = await c.SubmitAsync("/flightplan", plan);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var json = await site.Browser().GetFromJsonAsync<JsonElement>($"/api/flightplans/latest?cid={cid}");
        Assert.Equal(450, json.GetProperty("cruiseSpeed").GetInt32());
        Assert.Equal("1200", json.GetProperty("departureTime").GetString());
        Assert.Equal("S1010", json.GetProperty("cruiseAltitude").GetString());

        var bad = await c.SubmitAsync("/flightplan", new Dictionary<string, string>(plan) { ["Speed"] = "M078" });
        Assert.Contains("N0450", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Phraseology_ShowsBothLanguages()
    {
        using var site = new SiteFactory();
        string html = await site.Browser().HtmlAsync("/docs/phraseology");
        Assert.Contains("cleared for take-off", html);
        Assert.Contains("взлёт разрешаю", html);
        Assert.Contains("href=\"/docs/phraseology\"", html);
    }
}
