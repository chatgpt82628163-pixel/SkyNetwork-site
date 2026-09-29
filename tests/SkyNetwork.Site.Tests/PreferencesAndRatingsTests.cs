using System.Net;
using System.Text.Json;
using Dapper;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Tests;

public class LanguageAndThemeTests
{
    [Fact]
    public async Task EnglishByDefault_RussianOnRequest()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        var en = await c.HtmlAsync("/");
        Assert.Contains("<html lang=\"en\"", en);
        Assert.Contains("Fly with live air traffic control", en);

        var r = await c.GetAsync("/lang/ru?r=%2Fonline");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/online", r.Headers.Location!.OriginalString);
        Assert.Contains(r.Headers.GetValues("Set-Cookie"), v => v.StartsWith("lang=ru"));

        var ru = await c.HtmlAsync("/");
        Assert.Contains("<html lang=\"ru\"", ru);
        Assert.Contains("Летайте с живыми диспетчерами", ru);
        Assert.Contains("Такой страницы нет", await (await c.GetAsync("/no-such-page")).Content.ReadAsStringAsync());

        await c.GetAsync("/lang/en");
        Assert.Contains("Fly with live air traffic control", await c.HtmlAsync("/"));
    }

    [Fact]
    public async Task LanguageSwitchOnlyRedirectsWithinTheSite()
    {
        using var site = new SiteFactory();
        var r = await site.Browser().GetAsync("/lang/ru?r=https%3A%2F%2Fevil.example%2F");
        Assert.Equal("/", r.Headers.Location!.OriginalString);
        r = await site.Browser().GetAsync("/lang/ru?r=%2F%2Fevil.example");
        Assert.Equal("/", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ThemeCookieIsRendered()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        Assert.Contains("data-theme-toggle", await c.HtmlAsync("/"));
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Cookie", "theme=dark");
        var html = await (await c.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("data-theme=\"dark\"", html);
    }
}

public class SuspensionTests
{
    [Fact]
    public async Task TemporarySuspensionShowsItsEnd_AndExpires()
    {
        using var site = new SiteFactory();
        long sup = site.Member("Sergey Supervisor", Ratings.SUP);
        long bad = site.Member("Rule Breaker");
        var s = site.Browser();
        await s.LoginAsync(sup);
        await s.SubmitPageFormAsync($"/staff/members/{bad}", "Suspend", new Dictionary<string, string> { ["reason"] = "Spam", ["days"] = "3" });
        var m = site.Get<MemberService>().Find(bad)!;
        Assert.True(m.Suspended);
        Assert.InRange(m.SuspensionEnds!.Value, DateTime.UtcNow.AddDays(3).AddMinutes(-5), DateTime.UtcNow.AddDays(3).AddMinutes(5));

        var login = await site.Browser().SubmitAsync("/login", new Dictionary<string, string> { ["Cid"] = bad.ToString(), ["Password"] = "password1" });
        Assert.Contains("suspended until", await login.Content.ReadAsStringAsync());
        Assert.Contains("Suspended until", await s.HtmlAsync($"/staff/members/{bad}"));
        Assert.Contains(bad.ToString(), await s.HtmlAsync("/staff/members?suspended=1"));

        // Lifting it with the same form's button, then suspending again.
        var lifted = await s.SubmitPageFormAsync($"/staff/members/{bad}", "Suspend", new Dictionary<string, string>());
        Assert.Contains("Suspension lifted", await lifted.Content.ReadAsStringAsync());
        Assert.False(site.Get<MemberService>().IsSuspended(bad));
        await s.SubmitPageFormAsync($"/staff/members/{bad}", "Suspend", new Dictionary<string, string> { ["reason"] = "Spam", ["days"] = "3" });
        Assert.True(site.Get<MemberService>().IsSuspended(bad));

        // Time is up: the next check lifts it.
        using (var c = site.Get<Database>().Open())
            c.Execute("UPDATE member_profiles SET suspended_until = 1 WHERE cid = @bad", new { bad });
        Assert.Equal([bad], site.Get<MemberService>().LiftExpiredSuspensions());
        Assert.False(site.Get<MemberService>().IsSuspended(bad));
        await site.Browser().LoginAsync(bad);
        Assert.Contains(site.Get<AuditService>().Recent(), a => a.Action == "unsuspend" && a.ActorCid == 0);
    }

    [Fact]
    public async Task SupervisorsCannotSuspendOtherSupervisors()
    {
        using var site = new SiteFactory();
        long sup = site.Member("Sergey Supervisor", Ratings.SUP);
        long other = site.Member("Olga Supervisor", Ratings.SUP);
        long admin = site.Member("Anna Admin", Ratings.ADM);
        var s = site.Browser();
        await s.LoginAsync(sup);
        Assert.DoesNotContain("handler=Suspend", await s.HtmlAsync($"/staff/members/{other}"));
        // No form for them, and a hand-made request is refused too.
        var r = await s.SubmitAsync($"/staff/members/{other}", new Dictionary<string, string> { ["suspend"] = "true", ["reason"] = "x", ["days"] = "1" },
            $"/staff/members/{other}?handler=Suspend");
        Assert.Contains("Only an administrator", await r.Content.ReadAsStringAsync());
        Assert.False(site.Get<MemberService>().IsSuspended(other));

        var a = site.Browser();
        await a.LoginAsync(admin);
        await a.SubmitPageFormAsync($"/staff/members/{other}", "Suspend", new Dictionary<string, string> { ["reason"] = "x", ["days"] = "1" });
        Assert.True(site.Get<MemberService>().IsSuspended(other));
    }
}

public class PilotRatingTests
{
    [Fact]
    public async Task StaffSetPilotAndMilitaryRatings_ShownOnProfileAndApi()
    {
        using var site = new SiteFactory();
        long sup = site.Member("Sergey Supervisor", Ratings.SUP);
        long pilot = site.Member("Pavel Pilot");
        var s = site.Browser();
        await s.LoginAsync(sup);
        await s.SubmitAsync($"/staff/members/{pilot}", new Dictionary<string, string> { ["pilot"] = "2", ["military"] = "1" },
            $"/staff/members/{pilot}?handler=PilotRatings");
        var m = site.Get<MemberService>().Find(pilot)!;
        Assert.Equal((2, 1), (m.PilotRating, m.MilitaryRating));

        var profile = await site.Browser().HtmlAsync($"/members/{pilot}");
        Assert.Contains("Instrument Rating", profile);
        Assert.Contains("Military Pilot License", profile);
        var json = JsonDocument.Parse(await site.Browser().GetStringAsync($"/api/v1/members/{pilot}")).RootElement;
        Assert.Equal("IR", json.GetProperty("pilotRating").GetString());
        Assert.Equal("M1", json.GetProperty("militaryRating").GetString());
        Assert.Contains(site.Get<AuditService>().Recent(), a => a.Action == "pilot-rating" && a.Details == "P0 → IR");
    }

}
