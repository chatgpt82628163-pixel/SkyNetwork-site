using Microsoft.Extensions.DependencyInjection;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Tests;

/// <summary>Routes typed by hand become points: bundled fixes and airways, plus what the site learned from SimBrief.</summary>
public class NavDataTests
{
    [Fact]
    public void ExpandsAirwaysAndSkipsWhatIsNotAFix()
    {
        using var site = new SiteFactory();
        var nav = site.Get<NavData>();
        // BINOL and ERSAR sit on G487 with GUBOR between them (bundled data, AIRAC 2609); N0450F350 is a speed/level
        // group, 5530N03730E a coordinate, ZZZZZ nothing at all.
        var (points, unresolved) = nav.Decode("URSS", "UUEE", "N0450F350 BINOL G487 ERSAR DCT 5530N03730E ZZZZZ");
        Assert.Equal(["URSS", "BINOL", "GUBOR", "ERSAR", "5530N03730E", "UUEE"], points.Select(p => p.Ident));
        Assert.Equal(["", "", "G487", "G487", "", ""], points.Select(p => p.Airway));
        Assert.Equal((55.5, 37.5), (points[4].Lat, points[4].Lon));
        Assert.Equal(["ZZZZZ"], unresolved);
        Assert.True(nav.Counts.Fixes > 100000 && nav.Counts.Airways > 1000, "bundled nav data not loaded");
    }

    [Fact]
    public void LearnsFixesAndAirwaysFromSimbriefRoutes_AndKeepsThem()
    {
        using var site = new SiteFactory();
        var nav = site.Get<NavData>();
        // Nothing known about these yet: the line goes straight from the airport to the airport.
        Assert.Equal(["UUEE", "EDDF"], nav.Decode("UUEE", "EDDF", "TESTA UL999 TESTC").Points.Select(p => p.Ident));

        nav.Learn(
        [
            new RoutePoint("UUEE", 55.97, 37.41, "", 0),
            new RoutePoint("TESTA", 55.9, 36.9, "", 5000),
            new RoutePoint("TESTB", 55.5, 33.6, "UL999", 35000),
            new RoutePoint("TESTC", 54.9, 29.5, "UL999", 35000),
            new RoutePoint("EDDF", 50.03, 8.57, "", 0),
        ], "UUEE", "EDDF");

        var (points, unresolved) = nav.Decode("UUEE", "EDDF", "TESTA UL999 TESTC");
        Assert.Equal(["UUEE", "TESTA", "TESTB", "TESTC", "EDDF"], points.Select(p => p.Ident));
        Assert.Equal("UL999", points[2].Airway);
        Assert.Empty(unresolved);
        // A SID name is skipped, its fix taken; the airway can also be flown the other way.
        Assert.Equal(["EDDF", "TESTC", "TESTB", "TESTA", "UUEE"],
            nav.Decode("EDDF", "UUEE", "TEST1A TESTC UL999 TESTA").Points.Select(p => p.Ident));

        // Another instance (a restart) reads what was learned from the database.
        var restarted = ActivatorUtilities.CreateInstance<NavData>(site.Services);
        Assert.Equal(["UUEE", "TESTA", "TESTB", "TESTC", "EDDF"], restarted.Decode("UUEE", "EDDF", "TESTA UL999 TESTC").Points.Select(p => p.Ident));
        Assert.Equal((3, 1), (restarted.Counts.LearnedFixes, restarted.Counts.LearnedAirways));
    }

    [Fact]
    public void TheSameNameFarAwayIsAnotherFix()
    {
        using var site = new SiteFactory();
        var nav = site.Get<NavData>();
        nav.Learn(
        [
            new RoutePoint("SAMEX", 55.9, 36.9, "", 0),       // near Moscow
            new RoutePoint("SAMEX", -33.9, 151.2, "", 0),     // near Sydney
        ], "XXXX", "YYYY");
        var moscow = nav.Decode("UUEE", "ULLI", "SAMEX").Points;
        Assert.Equal(55.9, moscow[1].Lat);
        var sydney = nav.Decode("YSSY", "YMML", "SAMEX").Points;
        Assert.Equal(-33.9, sydney[1].Lat);
    }
}
