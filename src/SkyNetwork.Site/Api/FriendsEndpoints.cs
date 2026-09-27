using Microsoft.AspNetCore.Antiforgery;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;

namespace SkyNetwork.Site.Api;

/// <summary>
/// The signed-in member's friends for the map. Reading is open (an empty list for visitors); adding and removing
/// need the session cookie and the antiforgery token the map page carries, sent in the RequestVerificationToken header.
/// </summary>
public static class FriendsEndpoints
{
    public static void MapFriends(this WebApplication app)
    {
        app.MapGet("/api/me/friends", (CurrentUser me, FriendService friends, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { signedIn = me.IsSignedIn, friends = me.IsSignedIn ? friends.Ids(me.Cid) : Array.Empty<long>() });
        });

        app.MapPost("/api/me/friends/{cid:long}", async (long cid, CurrentUser me, FriendService friends, IAntiforgery xsrf, HttpContext ctx) =>
        {
            if (await Refused(me, xsrf, ctx) is { } refused) return refused;
            return friends.Add(me.Cid, cid) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { friends = friends.Ids(me.Cid) });
        });

        app.MapDelete("/api/me/friends/{cid:long}", async (long cid, CurrentUser me, FriendService friends, IAntiforgery xsrf, HttpContext ctx) =>
        {
            if (await Refused(me, xsrf, ctx) is { } refused) return refused;
            friends.Remove(me.Cid, cid);
            return Results.Ok(new { friends = friends.Ids(me.Cid) });
        });
    }

    private static async Task<IResult?> Refused(CurrentUser me, IAntiforgery xsrf, HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (!me.IsSignedIn) return Results.Unauthorized();
        return await xsrf.IsRequestValidAsync(ctx) ? null : Results.BadRequest(new { error = "Reload the page and try again" });
    }
}
