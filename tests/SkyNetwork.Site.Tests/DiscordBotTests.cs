using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;

namespace SkyNetwork.Site.Tests;

public class DiscordBotTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task DiscordPagesArePassedToTheBot()
    {
        int port = FreePort();
        using var bot = new HttpListener();
        bot.Prefixes.Add($"http://127.0.0.1:{port}/");
        bot.Start();
        string? asked = null;
        _ = Task.Run(async () =>
        {
            var ctx = await bot.GetContextAsync();
            asked = ctx.Request.Url!.PathAndQuery;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var body = "<h2>Готово</h2>"u8.ToArray();
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });

        using var site = new SiteFactory();
        using var app = site.WithWebHostBuilder(b => b.UseSetting("Site:DiscordBotUrl", $"http://127.0.0.1:{port}"));
        var r = await app.CreateClient().GetAsync("/discord/callback?code=abc&state=xyz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/html", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Готово", await r.Content.ReadAsStringAsync());
        Assert.Equal("/callback?code=abc&state=xyz", asked);
    }

    [Fact]
    public async Task BotNotRunningIsSaidPlainly()
    {
        using var site = new SiteFactory();
        using var app = site.WithWebHostBuilder(b => b.UseSetting("Site:DiscordBotUrl", $"http://127.0.0.1:{FreePort()}"));
        var r = await app.CreateClient().GetAsync("/discord/callback?code=abc");
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Contains("not running", await r.Content.ReadAsStringAsync());
    }
}
