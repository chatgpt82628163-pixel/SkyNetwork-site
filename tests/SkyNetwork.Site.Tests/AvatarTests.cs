using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Tests;

public class AvatarTests
{
    // A real 1×1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static async Task<HttpResponseMessage> Post(HttpClient c, string page, string handler, byte[]? file = null, bool remove = false)
    {
        var html = await (await c.GetAsync(page)).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(token.Success);
        var form = new MultipartFormDataContent { { new StringContent(WebUtility.HtmlDecode(token.Groups[1].Value)), "__RequestVerificationToken" } };
        if (remove) form.Add(new StringContent("true"), "remove");
        if (file != null)
        {
            var content = new ByteArrayContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "avatar", "me.png");
        }
        return await c.PostAsync($"{page}?handler={handler}", form);
    }

    [Fact]
    public async Task MemberUploadsReplacesAndRemovesTheirPicture()
    {
        using var site = new SiteFactory();
        long cid = site.Member("Pilot One");
        var c = site.Browser();
        await c.LoginAsync(cid);
        var members = site.Get<MemberService>();
        var uploads = site.Get<UploadStore>();

        Assert.Contains("Profile picture saved", await (await Post(c, "/account/settings", "Avatar", Png)).Content.ReadAsStringAsync());
        string first = members.Find(cid)!.Avatar;
        Assert.Matches("^[a-f0-9]{32}\\.png$", first);
        Assert.Contains($"/uploads/{first}", await site.Browser().HtmlAsync($"/members/{cid}"));
        Assert.Contains($"/uploads/{first}", await c.HtmlAsync("/account"));
        Assert.Contains($"<img class=\"initials\" src=\"/uploads/{first}\"", await c.HtmlAsync("/")); // the header, top right

        // A new picture replaces the old file; not an image is refused.
        await Post(c, "/account/settings", "Avatar", Png);
        string second = members.Find(cid)!.Avatar;
        Assert.NotEqual(first, second);
        Assert.Null(uploads.PathOf(first));
        Assert.Contains("Only JPEG, PNG and WebP", await (await Post(c, "/account/settings", "Avatar", "hello"u8.ToArray())).Content.ReadAsStringAsync());
        Assert.Equal(second, members.Find(cid)!.Avatar);

        await Post(c, "/account/settings", "Avatar", remove: true);
        Assert.Equal("", members.Find(cid)!.Avatar);
        Assert.Null(uploads.PathOf(second));
        Assert.DoesNotContain("/uploads/", await site.Browser().HtmlAsync($"/members/{cid}"));
    }

    [Fact]
    public async Task StaffRemoveAnUnsuitablePicture()
    {
        using var site = new SiteFactory();
        long cid = site.Member("Pilot One");
        long sup = site.Member("Sergey Supervisor", Ratings.SUP);
        var c = site.Browser();
        await c.LoginAsync(cid);
        await Post(c, "/account/settings", "Avatar", Png);
        Assert.NotEqual("", site.Get<MemberService>().Find(cid)!.Avatar);

        var s = site.Browser();
        await s.LoginAsync(sup);
        var r = await s.SubmitPageFormAsync($"/staff/members/{cid}", "RemoveAvatar", new Dictionary<string, string>());
        Assert.Contains("Profile picture removed", await r.Content.ReadAsStringAsync());
        Assert.Equal("", site.Get<MemberService>().Find(cid)!.Avatar);
    }

    [Fact]
    public async Task PublicProfileSaysTheAccountIsSuspended()
    {
        using var site = new SiteFactory();
        long cid = site.Member("Pilot One");
        Assert.DoesNotContain("suspended", await site.Browser().HtmlAsync($"/members/{cid}"));
        site.Get<MemberService>().SetSuspended(5, cid, true, "rule 2.1", days: 7);
        var html = await site.Browser().HtmlAsync($"/members/{cid}");
        Assert.Contains("This account is suspended until", html);
        Assert.DoesNotContain("rule 2.1", html); // the reason stays with staff
        site.Get<MemberService>().SetSuspended(5, cid, true, "rule 3", days: 0);
        Assert.Contains("This account is suspended. It cannot connect", await site.Browser().HtmlAsync($"/members/{cid}"));
    }
}
