using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;
using Microsoft.Extensions.DependencyInjection;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Tests;

public class PublicPagesTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/map")]
    [InlineData("/online")]
    [InlineData("/events")]
    [InlineData("/news")]
    [InlineData("/bookings")]
    [InlineData("/docs")]
    [InlineData("/docs/software")]
    [InlineData("/docs/ratings")]
    [InlineData("/developers")]
    [InlineData("/register")]
    [InlineData("/login")]
    [InlineData("/support")]
    public async Task PagesRender(string url)
    {
        using var site = new SiteFactory();
        var html = await site.Browser().HtmlAsync(url);
        Assert.Contains("SkyNetwork", html);
        Assert.DoesNotContain("/staff", html); // the staff area is never linked for visitors
    }

    [Fact]
    public async Task UnknownPageIs404_AndPrivatePagesAskToLogIn()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        var r = await c.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("There is no such page", await r.Content.ReadAsStringAsync());
        r = await c.GetAsync("/flightplan?callsign=AFL1");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("/login", r.Headers.Location!.PathAndQuery);
    }
}

public class AccountTests
{
    [Fact]
    public async Task RegisterWithoutCountry_AsksForIt()
    {
        using var site = new SiteFactory();
        // An empty field used to arrive as null and crash the page.
        var r = await site.Browser().SubmitAsync("/register", new Dictionary<string, string>
        {
            ["Name"] = "Ivan Petrov", ["Email"] = "ivan@example.com", ["Country"] = "",
            ["Password"] = "secret123", ["Confirm"] = "secret123",
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Choose a country from the list", await r.Content.ReadAsStringAsync());
        Assert.Equal(0, site.Get<MemberService>().Count());
    }

    [Fact]
    public async Task RegisterGivesCid_LoginWorks_AndBadPasswordIsRejected()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        var fields = new Dictionary<string, string>
        {
            ["Name"] = "Ivan Petrov", ["Email"] = "ivan@example.com", ["Country"] = "Россия",
            ["Password"] = "secret123", ["Confirm"] = "secret123",
        };
        var r = await c.SubmitAsync("/register", fields);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/account?welcome=1", r.Headers.Location!.OriginalString);
        var html = await c.HtmlAsync("/account?welcome=1");
        Assert.Contains("<div class=\"big-cid\">1</div>", html);

        // Same email again is refused.
        var again = await site.Browser().SubmitAsync("/register", fields);
        Assert.Contains("already registered", await again.Content.ReadAsStringAsync());

        var fresh = site.Browser();
        var bad = await fresh.SubmitAsync("/login", new Dictionary<string, string> { ["Cid"] = "1", ["Password"] = "wrong" });
        Assert.Contains("Wrong CID or password", await bad.Content.ReadAsStringAsync());
        await fresh.LoginAsync(1, "secret123");
        Assert.Contains("Ivan Petrov", await fresh.HtmlAsync("/account"));
    }

    [Fact]
    public async Task FlightPlanIsServedToSkyPilot()
    {
        using var site = new SiteFactory();
        long cid = site.Member("Pilot One");
        var c = site.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/flightplans/latest?cid={cid}")).StatusCode);
        await c.LoginAsync(cid);
        Assert.Contains("value=\"AFL123\"", await c.HtmlAsync("/flightplan?callsign=afl123"));

        var plan = new Dictionary<string, string>
        {
            ["Plan.Callsign"] = "afl123", ["Plan.Rules"] = "IFR", ["Plan.Aircraft"] = "a20n", ["Plan.CruiseSpeed"] = "450",
            ["Plan.Departure"] = "uuee", ["Plan.Destination"] = "ulli", ["Plan.Alternate"] = "ullo", ["Plan.DepartureTime"] = "1200",
            ["Plan.CruiseAltitude"] = "fl350", ["Enroute"] = "0110", ["Fuel"] = "03:00",
            ["Plan.Route"] = "n0450f350  demo5 dm100", ["Plan.Remarks"] = "/V/",
        };
        var bad = await c.SubmitAsync("/flightplan", new Dictionary<string, string>(plan) { ["Plan.Departure"] = "SVO" });
        Assert.Contains("4-letter ICAO codes", await bad.Content.ReadAsStringAsync());
        var r = await c.SubmitAsync("/flightplan", plan);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var json = await site.Browser().GetFromJsonAsync<JsonElement>($"/api/flightplans/latest?cid={cid}");
        Assert.Equal("IFR", json.GetProperty("rules").GetString());
        Assert.Equal("A20N", json.GetProperty("aircraft").GetString());
        Assert.Equal(450, json.GetProperty("cruiseSpeed").GetInt32());
        Assert.Equal(("UUEE", "ULLI", "ULLO"), (json.GetProperty("departure").GetString(), json.GetProperty("destination").GetString(),
            json.GetProperty("alternate").GetString()));
        Assert.Equal(("1200", "FL350", 70, 180), (json.GetProperty("departureTime").GetString(), json.GetProperty("cruiseAltitude").GetString(),
            json.GetProperty("enrouteMinutes").GetInt32(), json.GetProperty("fuelMinutes").GetInt32()));
        Assert.Equal("N0450F350 DEMO5 DM100", json.GetProperty("route").GetString());
    }

    [Fact]
    public async Task BookingsNeedRating_AndDoNotOverlap()
    {
        using var site = new SiteFactory();
        long obs = site.Member("New Member");
        long s2 = site.Member("Tower Controller", Ratings.S2);
        var c = site.Browser();
        await c.LoginAsync(obs);
        Assert.Contains("once you hold the S1 rating", await c.HtmlAsync("/bookings"));

        var atc = site.Browser();
        await atc.LoginAsync(s2);
        string date = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var booking = new Dictionary<string, string> { ["Callsign"] = "uuee_twr", ["Date"] = date, ["From"] = "18:00", ["To"] = "20:00" };
        Assert.Equal(HttpStatusCode.Redirect, (await atc.SubmitAsync("/bookings", booking)).StatusCode);
        var overlap = await atc.SubmitAsync("/bookings", new Dictionary<string, string>(booking) { ["From"] = "19:00", ["To"] = "21:00" });
        Assert.Contains("is already booked", await overlap.Content.ReadAsStringAsync());
        var api = await site.Browser().GetFromJsonAsync<JsonElement>("/api/v1/bookings");
        Assert.Equal("UUEE_TWR", api[0].GetProperty("callsign").GetString());
        Assert.Equal("S2", api[0].GetProperty("rating").GetString());
    }
}

public class StaffAreaTests
{
    [Fact]
    public async Task StaffAreaIsA404ForEveryoneElse()
    {
        using var site = new SiteFactory();
        long member = site.Member("Plain Member");
        foreach (var url in new[] { "/staff", "/staff/members", "/staff/audit", $"/staff/members/{member}" })
        {
            var anon = await site.Browser().GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, anon.StatusCode); // not a login redirect: nothing to discover
        }
        var c = site.Browser();
        await c.LoginAsync(member);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/staff")).StatusCode);
        Assert.DoesNotContain("/staff", await c.HtmlAsync("/"));
        // Posting to a staff handler is refused the same way.
        var post = await c.PostAsync($"/staff/members/{member}?handler=Rating", new FormUrlEncodedContent(new Dictionary<string, string> { ["rating"] = "12" }));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(Ratings.OBS, site.Get<MemberService>().Find(member)!.Rating);
    }

    [Fact]
    public async Task EachStaffPageNeedsItsOwnPermission()
    {
        using var site = new SiteFactory();
        long sup = site.Member("Sergey Supervisor", Ratings.SUP);
        long editor = site.Member("News Editor");
        site.Get<MemberService>().SetRoles(0, editor, ["news"]);

        var s = site.Browser();
        await s.LoginAsync(sup);
        Assert.Contains("Management", await s.HtmlAsync("/"));
        await s.HtmlAsync("/staff");
        await s.HtmlAsync("/staff/members");
        await s.HtmlAsync("/staff/audit");
        var card = await s.HtmlAsync($"/staff/members/{editor}");
        Assert.Contains("Suspend", card);
        Assert.DoesNotContain("Website roles", card);   // only administrators manage roles
        Assert.DoesNotContain("Change rating", card); // supervisors do not change ratings

        var e = site.Browser();
        await e.LoginAsync(editor);
        await e.HtmlAsync("/staff/news");
        await e.HtmlAsync("/staff/news/new");
        Assert.Equal(HttpStatusCode.NotFound, (await e.GetAsync("/staff/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await e.GetAsync("/staff/audit")).StatusCode);
    }

    [Fact]
    public async Task SuspensionLocksTheMemberOut_AndIsAudited()
    {
        using var site = new SiteFactory();
        long sup = site.Member("Sergey Supervisor", Ratings.SUP);
        long bad = site.Member("Rule Breaker");
        var victim = site.Browser();
        await victim.LoginAsync(bad);

        var s = site.Browser();
        await s.LoginAsync(sup);
        var r = await s.SubmitPageFormAsync($"/staff/members/{bad}", "Suspend", new Dictionary<string, string> { ["reason"] = "Blocking the frequency", ["days"] = "0" });
        Assert.Contains("Member suspended", await r.Content.ReadAsStringAsync());
        Assert.True(site.Get<MemberService>().IsSuspended(bad));

        // The open session ends and a new login is refused with a clear message.
        Assert.Equal(HttpStatusCode.Redirect, (await victim.GetAsync("/account")).StatusCode);
        var login = await site.Browser().SubmitAsync("/login", new Dictionary<string, string> { ["Cid"] = bad.ToString(), ["Password"] = "password1" });
        Assert.Contains("Your account is suspended. Reason: Blocking the frequency", await login.Content.ReadAsStringAsync());

        var audit = await s.HtmlAsync("/staff/audit");
        Assert.Contains("Suspension", audit);
        Assert.Contains("Blocking the frequency", audit);
    }

    [Fact]
    public async Task TrainingSectionIsGone()
    {
        using var site = new SiteFactory();
        long instructor = site.Member("Ilya Instructor", Ratings.I1);
        var i = site.Browser();
        await i.LoginAsync(instructor);
        Assert.Equal(HttpStatusCode.NotFound, (await i.GetAsync("/training")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await i.GetAsync("/staff/training")).StatusCode);
        Assert.DoesNotContain("/training\"", await i.HtmlAsync("/account"));
    }
}

public class PermissionTests
{
    [Fact]
    public void RatingsAndRolesGivePermissions()
    {
        Assert.Equal(Perm.All, Permissions.For(Ratings.OBS, Ratings.ADM, []));
        Assert.True(Permissions.For(Ratings.C1, Ratings.SUP, []).HasFlag(Perm.Suspend));
        Assert.False(Permissions.For(Ratings.C1, Ratings.SUP, []).HasFlag(Perm.ManageRoles));
        // A facility supervisor sees the staff area and who is online, nothing more on the website.
        Assert.Equal(Perm.StaffArea | Perm.Online, Permissions.For(Ratings.S3, 0, ["fsup"]));
        Assert.False(Permissions.For(Ratings.C1, Ratings.SUP, []).HasFlag(Perm.EditRatings));
        // A supervisor who is also an instructor gets both sets.
        var supInstructor = Permissions.For(Ratings.I1, Ratings.SUP, []);
        Assert.True(supInstructor.HasFlag(Perm.Suspend) && supInstructor.HasFlag(Perm.EditRatings));
        Assert.True(Permissions.For(Ratings.I2, 0, []).HasFlag(Perm.EditRatings));
        Assert.Equal(Perm.None, Permissions.For(Ratings.C3, 0, []));
        Assert.Equal(Perm.StaffArea | Perm.Events, Permissions.For(Ratings.OBS, 0, ["events"]));

        var instructor = Permissions.For(Ratings.I1, 0, []);
        Assert.True(Permissions.CanSetRating(0, instructor, Ratings.S1, Ratings.S2));
        Assert.False(Permissions.CanSetRating(0, instructor, Ratings.S1, Ratings.I1));   // up to C3 only
        Assert.False(Permissions.CanSetRating(0, instructor, Ratings.I2, Ratings.OBS));  // cannot demote instructors
        Assert.False(Permissions.CanSetRating(Ratings.ADM, Perm.All, Ratings.C1, Ratings.SUP)); // ranks are not ratings
        Assert.True(Permissions.CanSetRating(Ratings.ADM, Perm.All, Ratings.I3, Ratings.OBS));
    }
}

public class FeedTests
{
    private const string Feed1 = """
        {"general":{"server":"SkyNetwork","update_timestamp":1790000000},
         "pilots":[{"cid":1000012,"name":"Dmitry Volkov","callsign":"AFL1234","logon_time":1789999000,"latitude":55.9,"longitude":37.3,
                    "altitude":4500,"groundspeed":280,"transponder":"2000","flight_plan":"*A:I:A20N:450:UUEE:1200:0:FL350:ULLI:1:10:3:0:ULLO:/V/:DEMO5 DM100"}],
         "controllers":[{"cid":1000010,"name":"Ivan Petrov","callsign":"UUEE_TWR","logon_time":1789998000,"latitude":55.97,"longitude":37.41,
                    "rating":"S3","frequency":"131.500","facility":4,"visual_range":50}]}
        """;

    private const string Feed2 = """
        {"general":{"server":"SkyNetwork","update_timestamp":1790000015},
         "pilots":[{"cid":1000012,"name":"Dmitry Volkov","callsign":"AFL1234","logon_time":1789999000,"latitude":56.0,"longitude":37.3,
                    "altitude":5000,"groundspeed":280,"transponder":"2000","flight_plan":null}],
         "controllers":[]}
        """;

    [Fact]
    public void ParsesFeed_ComputesHeading_AndPlan()
    {
        var a = FeedParser.Parse(Feed1);
        var p = Assert.Single(a.Pilots);
        Assert.Null(p.Heading);
        Assert.Equal(("IFR", "A20N", "UUEE", "ULLI", 70, 180, "DEMO5 DM100"),
            (p.FlightPlan!.Rules, p.FlightPlan.Aircraft, p.FlightPlan.Departure, p.FlightPlan.Destination,
             p.FlightPlan.EnrouteMinutes, p.FlightPlan.FuelMinutes, p.FlightPlan.Route));
        Assert.Equal("TWR", a.Controllers[0].FacilityName);
        var b = FeedParser.Parse(Feed2, a);
        Assert.Equal(0, b.Pilots[0].Heading!.Value, 1); // moved due north
    }

    private const string AtisFeed = """
        {"general":{"server":"SkyNetwork","update_timestamp":1790000000},"pilots":[],
         "controllers":[{"cid":1000010,"name":"Ivan Petrov","callsign":"UUEE_ATIS","logon_time":1789998000,"latitude":55.97,"longitude":37.41,
                    "rating":"S3","frequency":"128.125","facility":0,"visual_range":50,
                    "text_atis":["SHEREMETYEVO ATIS INFORMATION KILO 1200Z","RWY 24R IN USE"]},
                    {"cid":1000011,"name":"Old Server","callsign":"UUEE_TWR","logon_time":1789998000,"rating":"S3","frequency":"131.500","facility":4}]}
        """;

    [Fact]
    public async Task AtisIsShownWithItsLetterAndText()
    {
        var snap = FeedParser.Parse(AtisFeed);
        var atis = snap.Controllers.First(c => c.Callsign == "UUEE_ATIS");
        Assert.Equal(("ATIS", "K"), (atis.FacilityName, atis.AtisCode));
        Assert.Equal(2, atis.Text.Count);
        Assert.Empty(snap.Controllers.First(c => c.Callsign == "UUEE_TWR").Text); // a server without text_atis

        using var site = new SiteFactory();
        site.Get<NetworkFeed>().Ingest(AtisFeed);
        var json = await site.Browser().HtmlAsync("/api/v1/online");
        Assert.Contains("\"facility\":\"ATIS\"", json);
        Assert.Contains("\"atisCode\":\"K\"", json);
        Assert.Contains("RWY 24R IN USE", json);
        var online = await site.Browser().HtmlAsync("/online");
        Assert.Contains("RWY 24R IN USE", online);
        // An ATIS is connected but is not a controller on a position: it is counted apart.
        Assert.Contains("Controllers · 1", online);
        Assert.Contains("ATIS · 1", online);
        var stats = System.Text.Json.JsonDocument.Parse(await site.Browser().HtmlAsync("/api/v1/stats")).RootElement;
        Assert.Equal((1, 1), (stats.GetProperty("controllersOnline").GetInt32(), stats.GetProperty("atisOnline").GetInt32()));
    }

    [Fact]
    public async Task TracksSessions_AndSurvivesRestart()
    {
        using var site = new SiteFactory();
        var feed = site.Get<NetworkFeed>();
        feed.Ingest(Feed1);
        var sessions = site.Get<SessionService>();
        Assert.Single(sessions.Recent(1000010));
        feed.Ingest(Feed2); // the controller left
        Assert.NotNull(sessions.Recent(1000010)[0].EndedAt);
        Assert.Null(sessions.Recent(1000012)[0].EndedAt);

        // A new feed instance (site restart) continues the pilot's session instead of opening a second one.
        var restarted = ActivatorUtilities.CreateInstance<NetworkFeed>(site.Services);
        restarted.Ingest(Feed2);
        Assert.Single(sessions.Recent(1000012));

        var online = await site.Browser().GetFromJsonAsync<JsonElement>("/api/v1/online");
        Assert.Equal("AFL1234", online.GetProperty("pilots")[0].GetProperty("callsign").GetString());
    }
}
