using System.Net;
using System.Text.Json;
using Dapper;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Tests;

public class AccountDeletionTests
{
    /// <summary>Everything the site keeps about a member, one row per table.</summary>
    private static void FillIn(SiteFactory site, long cid)
    {
        using var c = site.Get<Database>().Open();
        long now = Database.Now();
        c.Execute("""
            INSERT INTO flight_plans (cid, callsign, rules, aircraft, cruise_speed, departure, destination, departure_time, cruise_altitude,
                                      enroute_minutes, fuel_minutes, route, created_at)
                VALUES (@cid, 'SKY1', 'I', 'B738', 450, 'UUEE', 'ULLI', '1200', 'FL350', 80, 150, 'DCT', @now);
            INSERT INTO network_sessions (cid, callsign, kind, started_at, ended_at) VALUES (@cid, 'SKY1', 'pilot', @now - 3600, @now);
            INSERT INTO bookings (cid, callsign, starts_at, ends_at, created_at) VALUES (@cid, 'UUEE_TWR', @now + 3600, @now + 7200, @now);
            INSERT INTO tickets (cid, subject, created_at, updated_at) VALUES (@cid, 'Help', @now, @now);
            INSERT INTO ticket_messages (ticket_id, cid, body, created_at) VALUES (last_insert_rowid(), @cid, 'Hello', @now);
            INSERT INTO oauth_consents (cid, client_id, scope, created_at) VALUES (@cid, 'skyrus', 'profile', @now);
            INSERT INTO oauth_tokens (token_hash, client_id, cid, scope, expires_at) VALUES ('t' || @cid, 'skyrus', @cid, 'profile', @now + 3600);
            INSERT INTO staff_notes (cid, author_cid, body, created_at) VALUES (@cid, 1, 'note', @now);
            """, new { cid, now });
    }

    private static long Rows(SiteFactory site, long cid)
    {
        using var c = site.Get<Database>().Open();
        return new[] { "members", "member_profiles", "flight_plans", "network_sessions", "bookings", "tickets", "ticket_messages",
                       "oauth_consents", "oauth_tokens", "staff_notes" }
            .Sum(t => c.ExecuteScalar<long>($"SELECT COUNT(*) FROM {t} WHERE cid = @cid", new { cid }));
    }

    [Fact]
    public async Task UnconfirmedRegistrationDoesNotCount()
    {
        using var site = new SiteFactory();
        var members = site.Get<MemberService>();
        long confirmed = site.Member("Vera Confirmed");
        long waiting = members.Register("Ivan Waiting", "ivan@example.com", "", "password1", verified: false);

        Assert.Equal(1, members.Count());
        Assert.Equal(1, members.CountUnconfirmed());
        var stats = JsonDocument.Parse(await site.Browser().GetStringAsync("/api/v1/stats")).RootElement;
        Assert.Equal(1, stats.GetProperty("members").GetInt32());

        // No public profile, nothing in the API, until the email is confirmed.
        Assert.Equal(HttpStatusCode.NotFound, (await site.Browser().GetAsync($"/members/{waiting}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await site.Browser().GetAsync($"/api/v1/members/{waiting}")).StatusCode);
        await site.Browser().HtmlAsync($"/members/{confirmed}");

        // The team sees them apart.
        long admin = site.Member("Anna Admin", Ratings.ADM);
        var a = site.Browser();
        await a.LoginAsync(admin);
        var list = await a.HtmlAsync("/staff/members?show=unconfirmed");
        Assert.Contains("Ivan Waiting", list);
        Assert.DoesNotContain("Vera Confirmed", list);
        Assert.Contains("email not confirmed", list);

        members.ConfirmEmail(waiting, "ivan@example.com");
        Assert.Equal(3, members.Count());
        await site.Browser().HtmlAsync($"/members/{waiting}");
    }

    [Fact]
    public void UnconfirmedRegistrationIsDeletedOnceTheLinkExpires()
    {
        using var site = new SiteFactory();
        var members = site.Get<MemberService>();
        long old = members.Register("Old Waiting", "old@example.com", "", "password1", verified: false);
        long fresh = members.Register("Fresh Waiting", "fresh@example.com", "", "password1", verified: false);
        long resent = members.Register("Resent Waiting", "resent@example.com", "", "password1", verified: false);
        long confirmed = site.Member("Vera Confirmed");
        using (var c = site.Get<Database>().Open())
        {
            long twoDaysAgo = Database.Now() - 49 * 3600;
            c.Execute("UPDATE member_profiles SET registered_at = @twoDaysAgo WHERE cid IN (@old, @resent, @confirmed)",
                new { twoDaysAgo, old, resent, confirmed });
        }
        // A letter sent again yesterday still works: that registration waits for it.
        site.Get<EmailTokenService>().Create(resent, EmailTokenService.Verify, "resent@example.com");

        var deleted = members.DeleteUnconfirmed(TimeSpan.FromHours(48));

        Assert.Equal([old], deleted.Select(d => d.Cid));
        Assert.Null(members.Find(old));
        Assert.NotNull(members.Find(fresh));
        Assert.NotNull(members.Find(resent));
        Assert.NotNull(members.Find(confirmed));
        Assert.Contains(site.Get<AuditService>().Recent(), e => e.Action == "account-expired" && e.Target == old.ToString());
    }

    [Fact]
    public async Task MemberDeletesOwnAccount_AndTheCidIsNeverGivenAgain()
    {
        using var site = new SiteFactory();
        var members = site.Get<MemberService>();
        site.Member("Someone Before");
        long cid = site.Member("Pavel Leaving");
        FillIn(site, cid);
        var b = site.Browser();
        await b.LoginAsync(cid);

        // Wrong password or no tick: nothing happens.
        await b.SubmitPageFormAsync("/account/settings", "Delete", new Dictionary<string, string> { ["password"] = "wrong-one", ["understood"] = "true" });
        await b.SubmitPageFormAsync("/account/settings", "Delete", new Dictionary<string, string> { ["password"] = "password1" });
        Assert.NotNull(members.Find(cid));

        var r = await b.SubmitPageFormAsync("/account/settings", "Delete", new Dictionary<string, string> { ["password"] = "password1", ["understood"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/account-deleted", r.Headers.Location?.OriginalString);
        await b.HtmlAsync("/account-deleted");

        Assert.Null(members.Find(cid));
        Assert.Equal(0, Rows(site, cid));
        Assert.Contains(site.Get<AuditService>().Recent(), e => e.Action == "account-delete" && e.Target == cid.ToString() && e.ActorCid == cid);
        // Signed out; the old password no longer works.
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/account")).StatusCode);
        Assert.Null(members.Authenticate(cid, "password1"));

        // It was the newest CID, yet the next member gets a new one.
        long next = site.Member("Next Member");
        Assert.True(next > cid, $"{next} after {cid}");
    }

    [Fact]
    public async Task TeamMemberWithARankCannotDeleteThemselves()
    {
        using var site = new SiteFactory();
        long sup = site.Member("Sergey Supervisor", Ratings.SUP);
        var s = site.Browser();
        await s.LoginAsync(sup);
        var html = await s.HtmlAsync("/account/settings");
        Assert.DoesNotContain("handler=Delete", html);

        await s.SubmitAsync("/account/settings", new Dictionary<string, string> { ["password"] = "password1", ["understood"] = "true" },
            "/account/settings?handler=Delete");
        Assert.NotNull(site.Get<MemberService>().Find(sup));
    }

    [Fact]
    public async Task AdministratorDeletesAccounts_OthersCannot()
    {
        using var site = new SiteFactory();
        var members = site.Get<MemberService>();
        long admin = site.Member("Anna Admin", Ratings.ADM);
        long sup = site.Member("Sergey Supervisor", Ratings.SUP);
        long junk = site.Member("Junk One");
        FillIn(site, junk);

        // A supervisor has no delete form and the handler is not there for them.
        var s = site.Browser();
        await s.LoginAsync(sup);
        Assert.DoesNotContain("handler=Delete", await s.HtmlAsync($"/staff/members/{junk}"));
        var denied = await s.SubmitAsync($"/staff/members/{junk}", new Dictionary<string, string> { ["reason"] = "x", ["confirmCid"] = junk.ToString() },
            $"/staff/members/{junk}?handler=Delete");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        var a = site.Browser();
        await a.LoginAsync(admin);
        // Not for the team with a rank, not for oneself.
        Assert.DoesNotContain("handler=Delete", await a.HtmlAsync($"/staff/members/{sup}"));
        Assert.DoesNotContain("handler=Delete", await a.HtmlAsync($"/staff/members/{admin}"));

        // The CID has to be typed to confirm.
        await a.SubmitPageFormAsync($"/staff/members/{junk}", "Delete", new Dictionary<string, string> { ["reason"] = "Junk", ["confirmCid"] = "1" });
        Assert.NotNull(members.Find(junk));
        var r = await a.SubmitPageFormAsync($"/staff/members/{junk}", "Delete",
            new Dictionary<string, string> { ["reason"] = "Junk registration", ["confirmCid"] = junk.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains($"Account {junk} deleted", await a.HtmlAsync(r.Headers.Location!.OriginalString));
        Assert.Null(members.Find(junk));
        Assert.Equal(0, Rows(site, junk));
        Assert.Contains(site.Get<AuditService>().Recent(),
            e => e.Action == "account-delete" && e.Target == junk.ToString() && e.ActorCid == admin && e.Details.Contains("Junk registration"));

        // Several at once from the list: the supervisor is skipped.
        long b1 = site.Member("Bot One"), b2 = site.Member("Bot Two");
        var form = new List<KeyValuePair<string, string>>
        {
            new("cids", b1.ToString()), new("cids", b2.ToString()), new("cids", sup.ToString()), new("reason", "Junk registration"),
        };
        var page = await a.HtmlAsync("/staff/members");
        Assert.Contains("handler=Delete", page);
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        form.Add(new("__RequestVerificationToken", WebUtility.HtmlDecode(token)));
        var bulk = await (await a.PostAsync("/staff/members?handler=Delete", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        Assert.Contains("Deleted: 2. Skipped: 1", bulk);
        Assert.Null(members.Find(b1));
        Assert.Null(members.Find(b2));
        Assert.NotNull(members.Find(sup));
    }
}
