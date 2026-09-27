using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Tests;

public class FriendsTests
{
    /// <summary>The antiforgery token the map page hands to the script (signed-in members only).</summary>
    private static async Task<string> MapToken(HttpClient c)
    {
        var html = await c.HtmlAsync("/map");
        var m = Regex.Match(html, "data-xsrf=\"([^\"]*)\"");
        Assert.True(m.Success, "no data-xsrf on the map page");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod method, long cid, string? token)
    {
        var req = new HttpRequestMessage(method, $"/api/me/friends/{cid}");
        if (token != null) req.Headers.Add("RequestVerificationToken", token);
        return await c.SendAsync(req);
    }

    private static async Task<long[]> Ids(HttpClient c)
    {
        var json = JsonDocument.Parse(await c.GetStringAsync("/api/me/friends")).RootElement;
        return json.GetProperty("friends").EnumerateArray().Select(x => x.GetInt64()).ToArray();
    }

    [Fact]
    public async Task MapApi_AddsAndRemoves_OnlyForSignedInMembersWithTheToken()
    {
        using var site = new SiteFactory();
        long me = site.Member("Pavel Pilot"), friend = site.Member("Olga Controller");

        // A visitor: an empty list, and no writes.
        var anon = site.Browser();
        var list = JsonDocument.Parse(await anon.GetStringAsync("/api/me/friends")).RootElement;
        Assert.False(list.GetProperty("signedIn").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anon, HttpMethod.Post, friend, null)).StatusCode);

        var b = site.Browser();
        await b.LoginAsync(me);
        string token = await MapToken(b);
        // Without the token (a forged request from another site) nothing is added.
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(b, HttpMethod.Post, friend, null)).StatusCode);
        Assert.Empty(await Ids(b));

        Assert.Equal(HttpStatusCode.OK, (await Send(b, HttpMethod.Post, friend, token)).StatusCode);
        Assert.Equal([friend], await Ids(b));
        // Twice is fine; yourself and unknown members are refused.
        Assert.Equal(HttpStatusCode.OK, (await Send(b, HttpMethod.Post, friend, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(b, HttpMethod.Post, me, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(b, HttpMethod.Post, 999999, token)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Send(b, HttpMethod.Delete, friend, token)).StatusCode);
        Assert.Empty(await Ids(b));
    }

    [Fact]
    public void Service_RefusesUnconfirmedAndKeepsTheLimit()
    {
        using var site = new SiteFactory();
        var friends = site.Get<FriendService>();
        var members = site.Get<MemberService>();
        long me = site.Member("Pavel Pilot");
        long waiting = members.Register("Ivan Waiting", "ivan@example.com", "", "password1", verified: false);
        Assert.NotNull(friends.Add(me, waiting));

        for (int i = 0; i < FriendService.Limit; i++) Assert.Null(friends.Add(me, site.Member($"Friend Number{i}")));
        Assert.Equal("Your friends list is full", friends.Add(me, site.Member("One Toomany")));
        Assert.Equal(FriendService.Limit, friends.Ids(me).Count);
    }

    [Fact]
    public void DeletingAMember_RemovesThemFromEveryList_AndTheirOwnList()
    {
        using var site = new SiteFactory();
        var friends = site.Get<FriendService>();
        long a = site.Member("Anna First"), b = site.Member("Boris Second"), c = site.Member("Clara Third");
        friends.Add(a, b);
        friends.Add(b, c);
        friends.Add(c, b);

        site.Get<MemberService>().Delete(0, b, "test");

        Assert.Empty(friends.Ids(a));
        Assert.Empty(friends.Ids(b));
        Assert.Empty(friends.Ids(c));
    }

    [Fact]
    public async Task AccountPage_AddsByCid_Removes_AndTheProfileHasTheButton()
    {
        using var site = new SiteFactory();
        long me = site.Member("Pavel Pilot"), friend = site.Member("Olga Controller");
        var b = site.Browser();
        await b.LoginAsync(me);

        await b.SubmitPageFormAsync("/account/friends", "Add", new Dictionary<string, string> { ["Cid"] = friend.ToString() });
        var page = await b.HtmlAsync("/account/friends");
        Assert.Contains("Olga Controller", page);
        Assert.Equal([friend], site.Get<FriendService>().Ids(me));

        await b.SubmitPageFormAsync("/account/friends", "Remove", new Dictionary<string, string> { ["cid"] = friend.ToString() });
        Assert.Empty(site.Get<FriendService>().Ids(me));

        // The profile: a button on someone else's page, none on your own or for visitors.
        Assert.Contains("handler=Friend", await b.HtmlAsync($"/members/{friend}"));
        Assert.DoesNotContain("handler=Friend", await b.HtmlAsync($"/members/{me}"));
        Assert.DoesNotContain("handler=Friend", await site.Browser().HtmlAsync($"/members/{friend}"));
        await b.SubmitPageFormAsync($"/members/{friend}", "Friend", new Dictionary<string, string>());
        Assert.Equal([friend], site.Get<FriendService>().Ids(me));

        // The account area needs a sign-in.
        Assert.Equal(HttpStatusCode.Redirect, (await site.Browser().GetAsync("/account/friends")).StatusCode);
    }
}
