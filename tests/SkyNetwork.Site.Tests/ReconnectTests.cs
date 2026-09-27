using Dapper;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Tests;

/// <summary>A pilot who drops off and comes back with the same callsign is still on the same flight.</summary>
public class ReconnectTests
{
    private const string ToUlli = "\"*A:I:A20N:450:UUEE:1200:0:FL350:ULLI:1:10:3:0:ULLO:/V/:DEMO5 DM100\"";
    private const string BackToUuee = "\"*A:I:A20N:450:ULLI:1500:0:FL350:UUEE:1:10:3:0:UUDD:/V/:DCT\"";
    private const string Nobody = """{"general":{"server":"SkyNetwork","update_timestamp":1790000030},"pilots":[],"controllers":[]}""";

    private static string Pilot(string plan, double lat) => $$"""
        {"general":{"server":"SkyNetwork","update_timestamp":1790000000},
         "pilots":[{"cid":1000012,"name":"Dmitry Volkov","callsign":"AFL1234","logon_time":1789999000,
                    "latitude":{{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"longitude":37.3,
                    "altitude":4500,"groundspeed":280,"transponder":"2000","flight_plan":{{plan}}}],
         "controllers":[]}
        """;

    [Fact]
    public void AReconnectContinuesTheFlight_ANewPlanOrALongBreakStartsAnother()
    {
        using var site = new SiteFactory();
        var feed = site.Get<NetworkFeed>();
        var sessions = site.Get<SessionService>();

        feed.Ingest(Pilot(ToUlli, 55.9));
        feed.Ingest(Pilot(ToUlli, 56.0));
        feed.Ingest(Nobody);                  // the simulator crashed
        Assert.NotNull(sessions.Recent(1000012)[0].EndedAt);
        Assert.Empty(feed.Track("AFL1234"));  // not on the map while offline
        feed.Ingest(Pilot("null", 56.0));     // back where it was, the plan not sent again yet
        feed.Ingest(Pilot(ToUlli, 56.0));

        var flight = Assert.Single(sessions.Recent(1000012));
        Assert.Null(flight.EndedAt);
        Assert.Equal("A20N UUEE→ULLI", flight.Details);
        Assert.Equal(1, sessions.Hours(1000012).PilotSessions);
        Assert.Equal(2, feed.Track("AFL1234").Count); // the track before the crash is still there

        // Landed, left, and later back with the plan home: another flight.
        feed.Ingest(Nobody);
        feed.Ingest(Pilot(BackToUuee, 59.8));
        Assert.Equal(2, sessions.Recent(1000012).Count);
        Assert.Single(feed.Track("AFL1234"));         // a reconnect far away is a new track

        // Back with the same plan, but an hour later: another flight too.
        feed.Ingest(Nobody);
        using (var c = site.Get<Database>().Open())
            c.Execute("UPDATE network_sessions SET started_at = started_at - 7200, ended_at = ended_at - 3600");
        feed.Ingest(Pilot(BackToUuee, 59.8));
        Assert.Equal(3, sessions.Recent(1000012).Count);
    }

    [Fact]
    public void AControllerBackOnTheSameFrequencyKeepsTheSession()
    {
        using var site = new SiteFactory();
        var feed = site.Get<NetworkFeed>();
        string Twr(string frequency) => $$"""
            {"general":{"server":"SkyNetwork","update_timestamp":1790000000},"pilots":[],
             "controllers":[{"cid":1000010,"name":"Ivan Petrov","callsign":"UUEE_TWR","logon_time":1789998000,"latitude":55.97,"longitude":37.41,
                        "rating":"S3","frequency":"{{frequency}}","facility":4,"visual_range":50}]}
            """;
        feed.Ingest(Twr("131.500"));
        feed.Ingest(Nobody);
        feed.Ingest(Twr("131.500"));
        Assert.Single(site.Get<SessionService>().Recent(1000010));
        feed.Ingest(Nobody);
        feed.Ingest(Twr("118.100"));
        Assert.Equal(2, site.Get<SessionService>().Recent(1000010).Count);
    }
}
