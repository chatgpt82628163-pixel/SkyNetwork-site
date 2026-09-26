using Microsoft.Extensions.Options;

namespace SkyNetwork.Site.Api;

/// <summary>
/// /discord/… goes to the Discord bot's web page on this server (<see cref="SiteOptions.DiscordBotUrl"/>): SkyNetwork Connect
/// sends members back to /discord/callback after they sign in, and this way no separate nginx rule is needed for it.
/// </summary>
public static class DiscordBotEndpoints
{
    public static void MapDiscordBot(this WebApplication app)
    {
        app.MapGet("/discord/{**path}", async (string? path, HttpContext ctx, IHttpClientFactory http, IOptions<SiteOptions> options) =>
        {
            string bot = options.Value.DiscordBotUrl.TrimEnd('/');
            if (bot.Length == 0) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            try
            {
                using var response = await http.CreateClient("discord-bot")
                    .GetAsync($"{bot}/{path}{ctx.Request.QueryString}", ctx.RequestAborted);
                string body = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
                string type = response.Content.Headers.ContentType?.ToString() ?? "text/plain; charset=utf-8";
                return Results.Content(body, type, statusCode: (int)response.StatusCode);
            }
            catch (HttpRequestException)
            {
                return Results.Content("The Discord bot is not running. Try again later or tell the network staff.",
                    "text/plain; charset=utf-8", statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
