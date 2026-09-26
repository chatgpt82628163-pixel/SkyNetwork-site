using System.Net;
using System.Text.RegularExpressions;

namespace SkyNetwork.Site.Tests;

public class SignOutTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/account")]
    [InlineData("/account/settings")]
    [InlineData("/members/1")]
    public async Task SignOutButtonSignsOut(string page)
    {
        using var site = new SiteFactory();
        long cid = site.Member("Pilot One");
        var c = site.Browser();
        await c.LoginAsync(cid);
        var html = await c.HtmlAsync(page);
        // The sign-out form in the header, posted as a browser would: its hidden fields as rendered.
        var form = Regex.Match(html, @"<form[^>]*action=""/logout""[^>]*>(.*?)</form>", RegexOptions.Singleline);
        Assert.True(form.Success);
        var data = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(form.Groups[1].Value, @"<input[^>]*type=""hidden""[^>]*>"))
            data[Regex.Match(input.Value, @"name=""([^""]+)""").Groups[1].Value] =
                WebUtility.HtmlDecode(Regex.Match(input.Value, @"value=""([^""]*)""").Groups[1].Value);
        var r = await c.PostAsync("/logout", new FormUrlEncodedContent(data));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/account")).StatusCode);
    }
}
