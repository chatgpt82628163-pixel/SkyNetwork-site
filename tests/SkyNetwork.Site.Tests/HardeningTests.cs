using System.Net;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;

namespace SkyNetwork.Site.Tests;

// Security hardening: headers, security stamp, cookie policy.
public class SecurityHeadersTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/map")]
    [InlineData("/login")]
    [InlineData("/css/site.css")] // static file — headers must be set before UseStaticFiles
    public async Task SecurityHeadersPresent(string url)
    {
        using var site = new SiteFactory();
        var r = await site.Browser().GetAsync(url);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").FirstOrDefault());
        Assert.Equal("SAMEORIGIN", r.Headers.GetValues("X-Frame-Options").FirstOrDefault());
        Assert.Contains("frame-ancestors", r.Headers.GetValues("Content-Security-Policy").FirstOrDefault() ?? "");
        Assert.NotNull(r.Headers.GetValues("Referrer-Policy").FirstOrDefault());
    }
}

public class SecurityStampTests
{
    [Fact]
    public async Task PasswordChangeInvalidatesOtherSessions()
    {
        using var site = new SiteFactory();
        long cid = site.Member();
        // Two separate browser sessions.
        var session1 = site.Browser();
        var session2 = site.Browser();
        await session1.LoginAsync(cid);
        await session2.LoginAsync(cid);

        // Both sessions can access a protected page.
        var r1 = await session1.GetAsync("/account/settings");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var r2 = await session2.GetAsync("/account/settings");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        // Session 1 changes the password.
        await session1.SubmitPageFormAsync("/account/settings", "Password", new Dictionary<string, string>
        {
            ["Current"] = "password1",
            ["NewPassword"] = "newpassword1",
            ["Confirm"] = "newpassword1",
        });

        // Session 2 should now be rejected (stamp mismatch).
        var r2after = await session2.GetAsync("/account/settings");
        Assert.Equal(HttpStatusCode.Redirect, r2after.StatusCode);

        // Session 1 (which re-issued the cookie) stays valid.
        var r1after = await session1.GetAsync("/account/settings");
        Assert.Equal(HttpStatusCode.OK, r1after.StatusCode);
    }

    [Fact]
    public void AddColumnRejectsInvalidNames()
    {
        using var site = new SiteFactory();
        var db = site.Get<Database>();
        // Invoke the private AddColumn via reflection to verify the SQL-injection guard throws on bad input.
        var method = typeof(Database).GetMethod("AddColumn",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.NotNull(method); // sanity: method must exist
        // Open a real connection (same path the site uses) so we can call AddColumn directly.
        using var conn = db.Open();
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method.Invoke(null, [conn, "bad-table", "col", "TEXT"]));
        Assert.IsType<ArgumentException>(ex.InnerException);
        ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method.Invoke(null, [conn, "good_table", "bad-col", "TEXT"]));
        Assert.IsType<ArgumentException>(ex.InnerException);
    }
}
