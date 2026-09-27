using System.Text.RegularExpressions;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;

namespace SkyNetwork.Site.Tests;

public class PasswordResetTests
{
    [Fact]
    public void OnlyAdministratorsSetSomeoneElsesPassword()
    {
        Assert.True(Permissions.For(Ratings.OBS, Ratings.ADM, []).HasFlag(Perm.ResetPasswords));
        Assert.False(Permissions.For(Ratings.OBS, Ratings.SUP, []).HasFlag(Perm.ResetPasswords));
        Assert.False(Permissions.For(Ratings.I3, 0, ["support", "events", "news"]).HasFlag(Perm.ResetPasswords));
    }

    [Fact]
    public async Task SupportCannotResetAPassword()
    {
        using var site = new SiteFactory();
        long pilot = site.Member("Pilot One");
        long helper = site.Member("Help Desk");
        site.Get<MemberService>().SetRoles(0, helper, ["support"]);
        var c = site.Browser();
        await c.LoginAsync(helper);
        var html = await c.HtmlAsync($"/staff/members/{pilot}");
        Assert.DoesNotContain("handler=ResetPassword", html);
    }

    [Fact]
    public async Task LettersAreInEnglish()
    {
        using var f = new MailSiteFactory();
        long cid = f.Site.Member("Pilot One");
        var members = f.Get<MemberService>();
        string email = members.Find(cid)!.Email!;
        members.SetSuspended(5, cid, true, "rule 2.1", days: 7);
        var letter = await f.Mail.WaitFor(email, "account suspended");
        Assert.Equal("SkyNetwork: account suspended", letter.Subject);
        Assert.DoesNotMatch(new Regex("[А-Яа-яЁё]"), letter.Subject + letter.Body);
    }
}
