using System.Net;
using Dapper;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Tests;

public class LogbookTests
{
    /// <summary>Inserts a fake closed session directly in the database.</summary>
    private static void AddSession(SiteFactory site, long cid, string kind, string callsign,
        string details = "", int durationSeconds = 3600, int agoSeconds = 0)
    {
        var db = site.Get<Database>();
        using var c = db.Open();
        long now = Database.Now();
        long started = now - agoSeconds - durationSeconds;
        long ended = now - agoSeconds;
        c.Execute("INSERT INTO network_sessions (cid, callsign, kind, details, started_at, ended_at) VALUES (@cid,@callsign,@kind,@details,@started,@ended)",
            new { cid, callsign, kind, details, started, ended });
    }

    [Fact]
    public async Task Logbook_UnconfirmedMember_Returns404()
    {
        using var site = new SiteFactory();
        // Register with verified = false so FindConfirmed returns null.
        long cid = site.Get<MemberService>().Register("Unconfirmed User", "unconf@example.com", "", "password1", verified: false);
        var r = await site.Browser().GetAsync($"/members/{cid}/logbook");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Logbook_ConfirmedMember_ReturnsOk()
    {
        using var site = new SiteFactory();
        // site.Member() registers with verified = true by default.
        long cid = site.Member();
        var html = await site.Browser().HtmlAsync($"/members/{cid}/logbook");
        Assert.Contains("Logbook", html);
    }

    [Fact]
    public async Task Logbook_ShowsSessions_InDescendingOrder()
    {
        using var site = new SiteFactory();
        long cid = site.Member();
        // Older session first, newer second.
        AddSession(site, cid, "pilot", "TEST1", "B738 UUEE→UUWW", agoSeconds: 7200);
        AddSession(site, cid, "pilot", "TEST2", "A320 UUWW→URSS", agoSeconds: 100);

        var html = await site.Browser().HtmlAsync($"/members/{cid}/logbook");
        int pos1 = html.IndexOf("TEST2", StringComparison.Ordinal);
        int pos2 = html.IndexOf("TEST1", StringComparison.Ordinal);
        Assert.True(pos1 < pos2, "Newer session (TEST2) should appear before older (TEST1)");
    }

    [Fact]
    public async Task Logbook_PilotTab_ShowsOnlyPilotSessions()
    {
        using var site = new SiteFactory();
        long cid = site.Member();
        AddSession(site, cid, "pilot", "TEST1");
        AddSession(site, cid, "atc", "UUEE_APP");

        var html = await site.Browser().HtmlAsync($"/members/{cid}/logbook?tab=pilot");
        Assert.Contains("TEST1", html);
        Assert.DoesNotContain("UUEE_APP", html);
    }

    [Fact]
    public async Task Logbook_AtcTab_ShowsOnlyAtcSessions()
    {
        using var site = new SiteFactory();
        long cid = site.Member();
        AddSession(site, cid, "pilot", "TEST1");
        AddSession(site, cid, "atc", "UUEE_APP");

        var html = await site.Browser().HtmlAsync($"/members/{cid}/logbook?tab=atc");
        Assert.DoesNotContain("TEST1", html);
        Assert.Contains("UUEE_APP", html);
    }

    [Fact]
    public async Task Logbook_YearFilter_ShowsOnlyThatYear()
    {
        using var site = new SiteFactory();
        long cid = site.Member();
        // Insert one session two years ago and one now.
        var db = site.Get<Database>();
        using (var c = db.Open())
        {
            long oldStart = new DateTimeOffset(2022, 6, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            c.Execute("INSERT INTO network_sessions (cid, callsign, kind, details, started_at, ended_at) VALUES (@cid,'OLD1','pilot','',@s,@e)",
                new { cid, s = oldStart, e = oldStart + 3600 });
        }
        AddSession(site, cid, "pilot", "NEW1");

        var html = await site.Browser().HtmlAsync($"/members/{cid}/logbook?year=2022");
        Assert.Contains("OLD1", html);
        Assert.DoesNotContain("NEW1", html);
    }

    [Fact]
    public async Task Logbook_Pagination_SecondPageExists()
    {
        using var site = new SiteFactory();
        long cid = site.Member();
        // Insert 55 sessions (> 50 per page).
        for (int i = 0; i < 55; i++)
            AddSession(site, cid, "pilot", $"FL{i:000}", agoSeconds: i * 100);

        var html1 = await site.Browser().HtmlAsync($"/members/{cid}/logbook");
        Assert.Contains("Next", html1);

        var html2 = await site.Browser().HtmlAsync($"/members/{cid}/logbook?p=1");
        Assert.Contains("Previous", html2);
    }

    [Fact]
    public async Task Logbook_Totals_PilotAndAtcHoursMatch()
    {
        using var site = new SiteFactory();
        long cid = site.Member();
        AddSession(site, cid, "pilot", "FLT1", durationSeconds: 7200);  // 2 h
        AddSession(site, cid, "atc", "POS1", durationSeconds: 3600);    // 1 h

        var svc = site.Get<SessionService>();
        var hours = svc.Hours(cid);
        Assert.True(hours.PilotHours >= 2.0 - 0.01 && hours.PilotHours < 3.0, $"Expected ~2h pilot, got {hours.PilotHours}");
        Assert.True(hours.AtcHours >= 1.0 - 0.01 && hours.AtcHours < 2.0, $"Expected ~1h atc, got {hours.AtcHours}");
        Assert.Equal(1, hours.PilotSessions);
        Assert.Equal(1, hours.AtcSessions);
    }

    [Fact]
    public async Task Logbook_DetailLink_AppearsOnMemberProfile()
    {
        using var site = new SiteFactory();
        long cid = site.Member();
        var html = await site.Browser().HtmlAsync($"/members/{cid}");
        Assert.Contains($"/members/{cid}/logbook", html);
    }
}
