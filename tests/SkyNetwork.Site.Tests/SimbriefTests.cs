using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Tests;

public class SimbriefTests
{
    // The parts of a SimBrief OFP (json=v2) the import reads.
    private const string Ofp = """
        {"fetch":{"userid":"123456","status":"Success"},
         "params":{"airac":"2609"},
         "general":{"icao_airline":"AFL","flight_number":"1234","cruise_tas":"447","cruise_mach":"0.78","initial_altitude":"35000",
                    "route":"ARTIM  UL603 NEVEM DCT GOLSA","stepclimb_string":"UUEE/0350 GOLSA/0370","route_distance":"1204"},
         "origin":{"icao_code":"UUEE","pos_lat":"55.972642","pos_long":"37.414589"},
         "destination":{"icao_code":"EDDF","pos_lat":"50.033306","pos_long":"8.570456"},
         "alternate":[{"icao_code":"EDDK"},{"icao_code":"EDDL"}],
         "aircraft":{"icao_code":"A20N","reg":"VP-BXX","name":"A20N"},
         "atc":{"callsign":"AFL1234"},
         "times":{"sched_out":"1790251200","sched_off":"1790252100","sched_on":"1790265600","est_time_enroute":"13500","endurance":"19800"},
         "navlog":{"fix":[
            {"ident":"ARTIM","type":"wpt","via_airway":"DCT","pos_lat":"55.931","pos_long":"36.915","altitude_feet":"8000"},
            {"ident":"TOC","type":"ltlg","via_airway":"UL603","pos_lat":"55.5","pos_long":"33.0","altitude_feet":"35000"},
            {"ident":"NEVEM","type":"wpt","via_airway":"UL603","pos_lat":"54.1","pos_long":"28.2","altitude_feet":"35000"},
            {"ident":"GOLSA","type":"wpt","via_airway":"DCT","pos_lat":"51.2","pos_long":"12.1","altitude_feet":"35000"},
            {"ident":"EDDF","type":"apt","via_airway":"DCT","pos_lat":"50.033306","pos_long":"8.570456","altitude_feet":"364"}]}}
        """;

    [Fact]
    public void ReadsTheLatestPlan_WithRoutePointsAndExtras()
    {
        var (plan, error) = Simbrief.Parse(Ofp);
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal(("AFL1234", "A20N", 447), (plan.Callsign, plan.Aircraft, plan.CruiseSpeed));
        Assert.Equal(("UUEE", "EDDF", "EDDK"), (plan.Departure, plan.Destination, plan.Alternate));
        Assert.Equal(("1200", "FL350", 225, 330), (plan.DepartureTime, plan.CruiseAltitude, plan.EnrouteMinutes, plan.FuelMinutes));
        Assert.Equal("ARTIM UL603 NEVEM DCT GOLSA", plan.Route);

        var stored = StoredRoute.Parse(plan.Waypoints);
        Assert.NotNull(stored);
        // Departure first, top of climb left out, destination once at the end; the airway that led to each point.
        Assert.Equal(["UUEE", "ARTIM", "NEVEM", "GOLSA", "EDDF"], stored.Points.Select(p => p.Ident));
        Assert.Equal(["", "", "UL603", "", ""], stored.Points.Select(p => p.Airway));
        Assert.Equal(35000, stored.Points[2].Altitude);
        Assert.Equal(("AFL", "1234", "VP-BXX", "2609", 1204), (stored.Airline, stored.Flight, stored.Registration, stored.Airac, stored.RouteDistance));
        Assert.Equal("UUEE/0350 GOLSA/0370", stored.StepClimbs);
        Assert.Equal(1790252100, stored.OffTime);
        Assert.False(stored.Extras().ContainsKey("points"));
    }

    // Some OFPs (seen with a Fenix A320 plan UUDD–LOWW) list the points as {"0": {...}, "1": {...}} instead of {"fix": [...]}.
    private const string OfpIndexed = """
        {"fetch":{"status":"Success"},
         "origin":{"icao_code":"UUEE","pos_lat":"55.972642","pos_long":"37.414589"},
         "destination":{"icao_code":"EDDF","pos_lat":"50.033306","pos_long":"8.570456"},
         "aircraft":{"icao_code":"A20N"},
         "general":{"route":"ARTIM UL603 NEVEM DCT GOLSA"},
         "navlog":{
            "0":{"ident":"ARTIM","type":"wpt","via_airway":"DCT","pos_lat":"55.931","pos_long":"36.915","altitude_feet":"8000"},
            "1":{"ident":"TOC","type":"ltlg","via_airway":"UL603","pos_lat":"55.5","pos_long":"33.0","altitude_feet":"35000"},
            "2":{"ident":"NEVEM","type":"wpt","via_airway":"UL603","pos_lat":"54.1","pos_long":"28.2","altitude_feet":"35000"},
            "3":{"ident":"GOLSA","type":"wpt","via_airway":"DCT","pos_lat":"51.2","pos_long":"12.1","altitude_feet":"35000"},
            "4":{"ident":"EDDF","type":"apt","via_airway":"DCT","pos_lat":"50.033306","pos_long":"8.570456","altitude_feet":"364"}}}
        """;

    [Fact]
    public void ReadsANavlogKeyedByIndex()
    {
        var (plan, error) = Simbrief.Parse(OfpIndexed);
        Assert.Null(error);
        var stored = StoredRoute.Parse(plan!.Waypoints);
        Assert.NotNull(stored);
        Assert.Equal(["UUEE", "ARTIM", "NEVEM", "GOLSA", "EDDF"], stored.Points.Select(p => p.Ident));

        // A plain array works too.
        string asArray = OfpIndexed.Replace("\"navlog\":{", "\"navlog\":[").Replace("\"364\"}}}", "\"364\"}]}");
        for (int i = 0; i < 5; i++) asArray = asArray.Replace($"\"{i}\":{{", "{");
        var (plan2, _) = Simbrief.Parse(asArray);
        Assert.Equal(5, StoredRoute.Parse(plan2!.Waypoints)!.Points.Count);
    }

    [Fact]
    public void ReadsTimesGivenAsClockStrings()
    {
        // Another OFP variant: durations as "03:45:00" and moments as ISO 8601 instead of seconds.
        string ofp = Ofp.Replace("\"sched_out\":\"1790251200\"", "\"sched_out\":\"2026-09-24T12:00:00Z\"").Replace("\"sched_off\":\"1790252100\"", "\"sched_off\":\"2026-09-24T12:15:00Z\"")
            .Replace("\"est_time_enroute\":\"13500\"", "\"est_time_enroute\":\"03:45:00\"").Replace("\"endurance\":\"19800\"", "\"endurance\":\"05:30:00\"");
        var (plan, error) = Simbrief.Parse(ofp);
        Assert.Null(error);
        Assert.Equal(("1200", 225, 330), (plan!.DepartureTime, plan.EnrouteMinutes, plan.FuelMinutes));
        Assert.Equal(1790252100, StoredRoute.Parse(plan.Waypoints)!.OffTime);
    }

    [Fact]
    public void ExplainsWhatWentWrong()
    {
        var (plan, error) = Simbrief.Parse("""{"fetch":{"userid":"","status":"Error: Unknown UserID"}}""");
        Assert.Null(plan);
        Assert.Contains("no such SimBrief user", error);
        Assert.NotNull(Simbrief.Parse("<html>").Error);
        Assert.NotNull(Simbrief.Parse("[]").Error);
    }

    [Fact]
    public void KeepsOnlyWellFormedRoutesFromTheForm()
    {
        // The older plain list is still accepted and stored in the current shape.
        Assert.Equal("""{"points":[["UUEE",55.9726,37.4146,"",0],["ULLI",59.8,30.26,"",0]]}""",
            Simbrief.Clean("""[["uuee",55.972642,37.414589],["ulli",59.8,30.26]]"""));
        string full = """{"points":[["UUEE",55.97,37.41,"",0],["ARTIM",55.93,36.91,"UL603",35000]],"reg":"VP-BXX","dist":1204}""";
        Assert.Equal(full, Simbrief.Clean(full));
        Assert.Equal("", Simbrief.Clean("""[["UUEE",95,37]]"""));
        Assert.Equal("", Simbrief.Clean("""{"a":1}"""));
        Assert.Equal("", Simbrief.Clean("""[["UUEE","x",1],["B",1,1]]"""));
        Assert.Equal("", Simbrief.Clean("""[["UUEE",55,37]]"""));
        Assert.Equal("", Simbrief.Clean("not json"));
        Assert.Equal("", Simbrief.Clean(null));
    }

    [Fact]
    public async Task PlanLoadedFromSimbrief_IsFiledByTheFilePlanButton()
    {
        // After a SimBrief import the page is at /flightplan?handler=Simbrief. The plan form there used to post back to that
        // address: "File plan" imported again ("Enter your SimBrief username") and the loaded plan was gone.
        using var site = new SiteFactory();
        long cid = site.Member("Import Pilot");
        var c = site.Browser();
        await c.LoginAsync(cid);
        // An import without a username answers at once (SimBrief is not asked) and renders the page at the import address.
        var imported = await c.SubmitAsync("/flightplan", new Dictionary<string, string> { ["simbriefUser"] = "" }, "/flightplan?handler=Simbrief");
        string html = await imported.Content.ReadAsStringAsync();
        var planForm = System.Text.RegularExpressions.Regex.Match(html, @"<form[^>]*class=""form wide""[^>]*>");
        Assert.True(planForm.Success, "no plan form on the page");
        string action = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(planForm.Value, @"action=""([^""]*)""").Groups[1].Value);
        Assert.Equal("/flightplan", action);

        var filed = await c.SubmitAsync("/flightplan", new Dictionary<string, string>
        {
            ["Plan.Callsign"] = "afl123", ["Plan.Rules"] = "IFR", ["Plan.Aircraft"] = "a20n", ["Plan.CruiseSpeed"] = "450",
            ["Plan.Departure"] = "uuee", ["Plan.Destination"] = "ulli", ["Plan.Alternate"] = "ullo", ["Plan.DepartureTime"] = "1200",
            ["Plan.CruiseAltitude"] = "fl350", ["Enroute"] = "0110", ["Fuel"] = "03:00",
            ["Plan.Route"] = "n0450f350  demo5 dm100", ["Plan.Remarks"] = "/V/",
        }, action);
        Assert.Equal(HttpStatusCode.Redirect, filed.StatusCode);
        Assert.Equal("/flightplan?saved=1", filed.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task MapGetsTheRoute_FromSimbriefOrWorkedOut_AndTheFlownTrack()
    {
        using var site = new SiteFactory();
        long cid = site.Member("Route Pilot"), other = site.Member("Plain Pilot");
        var c = site.Browser();
        await c.LoginAsync(cid);
        var r = await c.SubmitAsync("/flightplan", new Dictionary<string, string>
        {
            ["Plan.Callsign"] = "AFL1234", ["Plan.Rules"] = "IFR", ["Plan.Aircraft"] = "A20N", ["Plan.CruiseSpeed"] = "447",
            ["Plan.Departure"] = "UUEE", ["Plan.Destination"] = "EDDF", ["Plan.Alternate"] = "", ["Plan.DepartureTime"] = "0920",
            ["Plan.CruiseAltitude"] = "FL350", ["Enroute"] = "03:45", ["Fuel"] = "05:30",
            ["Plan.Route"] = "ARTIM UL603 NEVEM", ["Plan.Remarks"] = "/V/",
            ["Plan.Waypoints"] = """{"points":[["UUEE",55.97,37.41],["ARTIM",55.93,36.91,"",8000],["EDDF",50.03,8.57]],"reg":"VP-BXX","airac":"2609"}""",
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var feed = site.Get<NetworkFeed>();
        string Feed(double lat, int alt, int gs) => $$"""
            {"general":{"server":"SkyNetwork","update_timestamp":1790000000},
             "pilots":[{"cid":{{cid}},"name":"Route Pilot","callsign":"AFL1234","logon_time":1789999000,"latitude":{{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"longitude":37.3,
                        "altitude":{{alt}},"groundspeed":{{gs}},"transponder":"2000","flight_plan":"*A:I:A20N:447:UUEE:0920:0:FL350:EDDF:3:45:5:30::/V/:ARTIM UL603 NEVEM"},
                       {"cid":{{other}},"name":"Plain Pilot","callsign":"SBI55","logon_time":1789999000,"latitude":43.5,"longitude":40.0,
                        "altitude":12000,"groundspeed":300,"transponder":"2000","flight_plan":"*A:I:B738:440:URSS:1200:0:FL330:UUEE:2:0:3:0::/V/:BINOL G487 ERSAR DCT ZZZZZ"}],
             "controllers":[]}
            """;
        feed.Ingest(Feed(55.9, 4500, 250));
        feed.Ingest(Feed(55.9, 4600, 250));   // no real change: not a new point
        feed.Ingest(Feed(55.8, 6000, 280));

        var route = await c.GetFromJsonAsync<JsonElement>("/api/v1/pilots/afl1234/route");
        Assert.Equal("simbrief", route.GetProperty("source").GetString());
        Assert.Equal(["UUEE", "ARTIM", "EDDF"], route.GetProperty("waypoints").EnumerateArray().Select(p => p[0].GetString()));
        Assert.Equal(8000, route.GetProperty("waypoints")[1][4].GetInt32());
        Assert.Equal("VP-BXX", route.GetProperty("extras").GetProperty("reg").GetString());
        var track = route.GetProperty("track").EnumerateArray().ToList();
        Assert.Equal(2, track.Count);
        Assert.Equal((55.8, 6000, 280), (track[1][0].GetDouble(), track[1][2].GetInt32(), track[1][3].GetInt32()));

        // No SimBrief: the route text is worked out with the bundled airways; unknown tokens are named.
        var plain = await c.GetFromJsonAsync<JsonElement>("/api/v1/pilots/SBI55/route");
        Assert.Equal("route", plain.GetProperty("source").GetString());
        Assert.Equal(["URSS", "BINOL", "GUBOR", "ERSAR", "UUEE"], plain.GetProperty("waypoints").EnumerateArray().Select(p => p[0].GetString()));
        Assert.Equal("G487", plain.GetProperty("waypoints")[2][3].GetString());
        Assert.Equal(["ZZZZZ"], plain.GetProperty("unresolved").EnumerateArray().Select(u => u.GetString()));
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("extras").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/pilots/NOBODY/route")).StatusCode);
        feed.Ingest("""{"general":{"server":"SkyNetwork","update_timestamp":1790000030},"pilots":[],"controllers":[]}""");
        Assert.Empty(feed.Track("AFL1234"));
    }

    [Fact]
    public void OlderDatabasesGetTheRoutePointsColumn()
    {
        using var site = new SiteFactory();
        using (var old = new SqliteConnection($"Data Source={site.DatabasePath}"))
        {
            old.Execute("""
                CREATE TABLE flight_plans (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, cid INTEGER NOT NULL, callsign TEXT NOT NULL,
                    rules TEXT NOT NULL, aircraft TEXT NOT NULL, cruise_speed INTEGER NOT NULL,
                    departure TEXT NOT NULL, destination TEXT NOT NULL, alternate TEXT NOT NULL DEFAULT '',
                    departure_time TEXT NOT NULL, cruise_altitude TEXT NOT NULL,
                    enroute_minutes INTEGER NOT NULL, fuel_minutes INTEGER NOT NULL,
                    route TEXT NOT NULL, remarks TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL);
                INSERT INTO flight_plans (cid, callsign, rules, aircraft, cruise_speed, departure, destination, departure_time,
                    cruise_altitude, enroute_minutes, fuel_minutes, route, created_at)
                VALUES (7, 'OLD1', 'IFR', 'B738', 440, 'UUEE', 'ULLI', '1200', 'FL330', 70, 150, 'DCT', 1);
                """);
        }
        SqliteConnection.ClearAllPools();
        var plans = site.Get<FlightPlanService>();
        Assert.Equal("", plans.Latest(7)!.Waypoints);
    }
}
