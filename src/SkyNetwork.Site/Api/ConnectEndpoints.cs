using System.Text;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Api;

/// <summary>
/// SkyNetwork Connect endpoints for other sites: POST /oauth/token (code for an access token) and
/// GET /oauth/userinfo (the member behind a token). The sign-in page itself is /oauth/authorize.
/// Documented on /developers.
/// </summary>
public static class ConnectEndpoints
{
    public static void MapConnect(this WebApplication app)
    {
        app.MapPost("/oauth/token", async (HttpContext ctx, ConnectService connect) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!ctx.Request.HasFormContentType) return Error(400, "invalid_request", "Send the parameters as application/x-www-form-urlencoded");
            var form = await ctx.Request.ReadFormAsync();
            if (form["grant_type"] != "authorization_code") return Error(400, "unsupported_grant_type", "Only authorization_code is supported");
            // Client credentials: HTTP Basic or the form fields client_id / client_secret.
            string clientId = form["client_id"].ToString(), secret = form["client_secret"].ToString();
            string? auth = ctx.Request.Headers.Authorization;
            if (auth != null && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var pair = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim())).Split(':', 2);
                    if (pair.Length == 2) (clientId, secret) = (Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]));
                }
                catch (FormatException) { return Error(401, "invalid_client", "Malformed Basic credentials"); }
            }
            string code = form["code"].ToString(), redirect = form["redirect_uri"].ToString();
            if (clientId.Length == 0 || secret.Length == 0 || code.Length == 0 || redirect.Length == 0)
                return Error(400, "invalid_request", "client_id, client_secret, code and redirect_uri are required");
            string? verifier = form["code_verifier"].ToString() is { Length: > 0 } v ? v : null;
            var result = connect.Exchange(clientId, secret, code, redirect, verifier);
            if (result.Error != null) return Error(result.Error == "invalid_client" ? 401 : 400, result.Error, result.Description!);
            return Results.Json(new
            {
                access_token = result.AccessToken, token_type = "Bearer", expires_in = (int)ConnectService.TokenLifetime.TotalSeconds,
                scope = result.Scope,
            });
        }).RequireRateLimiting("connect");

        app.MapGet("/oauth/userinfo", (HttpContext ctx, ConnectService connect, MemberService members) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            string? auth = ctx.Request.Headers.Authorization;
            var token = auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : null;
            if (connect.Token(token) is not { } t || members.Find(t.Cid) is not { Suspended: false } m)
            {
                ctx.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
                return Error(401, "invalid_token", "The access token is invalid or expired");
            }
            return Results.Json(new
            {
                cid = m.Cid, name = m.Name, email = t.Scopes.Contains("email") ? m.Email : null,
                rating = m.RatingShort, ratingName = m.RatingLong,
                staffRank = m.IsStaff ? Ratings.Short(m.StaffRank) : null,
                pilotRating = PilotRatings.Pilot.Short(m.PilotRating), militaryRating = PilotRatings.Military.Short(m.MilitaryRating),
                country = m.Country,
                // Absolute address of the profile picture, or null.
                avatar = m.AvatarUrl is { } a ? $"{ctx.Request.Scheme}://{ctx.Request.Host}{a}" : null,
            });
        }).RequireRateLimiting("connect");
    }

    private static IResult Error(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: status);
}
