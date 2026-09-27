using System.Net;
using Microsoft.Extensions.Logging;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Localization;
using SkyNetwork.Site.Services.Health;

namespace SkyNetwork.Site.Tests;

/// <summary>The status page: who sees it, how problems are explained, and how the server's own files are read.</summary>
public class StatusPageTests
{
    // Nothing on this machine's services is asked: the tests run on the real server.
    private static Dictionary<string, string> Quiet(Dictionary<string, string>? more = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["Site:FsdService"] = "", ["Site:VoiceService"] = "", ["Site:BotService"] = "", ["Site:WebService"] = "",
            ["Site:DiscordBotUrl"] = "",
        };
        foreach (var (k, v) in more ?? []) settings[k] = v;
        return settings;
    }

    [Fact]
    public async Task OnlySupervisorsAndAdministratorsSeeIt()
    {
        using var site = new SiteFactory(Quiet());
        long admin = site.Member("Anna Admin", Ratings.ADM), sup = site.Member("Sergey Supervisor", Ratings.SUP);
        long instructor = site.Member("Ivan Instructor", Ratings.I1), pilot = site.Member("Petr Pilot");

        var a = site.Browser();
        await a.LoginAsync(admin);
        string html = await a.HtmlAsync("/staff/status");
        Assert.Matches("Everything is working|Working, with warnings|Something is not working", html);
        foreach (var section in new[] { "Network", "Website", "Server", "Problems in the last 24 hours" }) Assert.Contains(section, html);
        Assert.Contains("href=\"/staff/status\"", html);

        var s = site.Browser();
        await s.LoginAsync(sup);
        Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/staff/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/staff/status?handler=Panel")).StatusCode);

        foreach (long cid in new[] { instructor, pilot })
        {
            var c = site.Browser();
            await c.LoginAsync(cid);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/staff/status")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await site.Browser().GetAsync("/staff/status")).StatusCode);
    }

    [Fact]
    public async Task ProblemsAreExplained_WithWhatToDo()
    {
        // A data feed nobody answers and no mail server.
        using var site = new SiteFactory(Quiet(new() { ["Site:DataFeedUrl"] = "http://127.0.0.1:9/data.json", ["Site:FeedPollSeconds"] = "3600" }));
        var monitor = site.Get<HealthMonitor>();
        await monitor.RunChecksAsync();

        var feed = monitor.Results.Single(r => r.Id == "feed");
        Assert.Equal(HealthLevel.Error, feed.Level);
        Assert.StartsWith("The website gets no data from the network server", feed.Problem!.Key);
        Assert.Contains("curl", feed.Command);

        var mail = monitor.Results.Single(r => r.Id == "mail");
        Assert.Equal((HealthLevel.Warning, "ssh -t SW \"bash ~/set-mail.sh\""), (mail.Level, mail.Command));
        Assert.Equal(HealthLevel.Ok, monitor.Results.Single(r => r.Id == "database").Level);
        Assert.Equal(HealthLevel.Error, monitor.Overall);
        Assert.Equal(HealthLevel.Error, monitor.Timeline("feed")[^1]);
        Assert.Null(monitor.Timeline("feed")[0]);

        var admin = site.Browser();
        await admin.LoginAsync(site.Member("Anna Admin", Ratings.ADM));
        string html = await admin.HtmlAsync("/staff/status");
        Assert.Contains("Something is not working", html);
        Assert.Contains("The website gets no data from the network server", html);
        Assert.Contains("bash ~/set-mail.sh", html);
        Assert.Contains("class=\"count danger\"", html); // the problems next to the menu link
    }

    [Fact]
    public async Task WarningsOfTheSite_AreListed_RepeatsCountedOnce()
    {
        using var site = new SiteFactory(Quiet());
        var logs = site.Get<ILoggerFactory>();
        var metar = logs.CreateLogger("SkyNetwork.Site.Services.MetarService");
        metar.LogWarning("METAR {Icao}: {Error}", "UUEE", "timeout");
        metar.LogWarning("METAR {Icao}: {Error}", "URSS", "timeout");
        logs.CreateLogger("SkyNetwork.Site.Services.Mailer").LogError(new InvalidOperationException("SMTP refused"), "Mail {Subject} not sent", "Welcome");
        // ASP.NET's warning on every page with a form is not a problem.
        logs.CreateLogger("Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery").LogWarning(new EventId(8), "The 'Cache-Control' header was overridden");

        var monitor = site.Get<HealthMonitor>();
        await monitor.RunChecksAsync();
        // Only what this test wrote (the site may add its own start-up notes).
        var problems = monitor.Problems().Where(p => p.Message.StartsWith("MetarService") || p.Message.StartsWith("Mailer")).ToList();
        Assert.Equal(2, problems.Count);
        var weather = problems.Single(p => !p.IsError);
        Assert.Equal((2, "site", "MetarService: METAR URSS: timeout"), (weather.Count, weather.Source, weather.Message));
        var mail = problems.Single(p => p.IsError);
        Assert.Contains("SMTP refused", mail.Details);
        Assert.Equal(HealthLevel.Error, monitor.Results.Single(r => r.Id == "errors").Level);

        var admin = site.Browser();
        await admin.LoginAsync(site.Member("Anna Admin", Ratings.ADM));
        string html = await admin.HtmlAsync("/staff/status?handler=Panel");
        Assert.Contains("MetarService: METAR URSS: timeout", html);
        Assert.Contains("×2", html);
        Assert.DoesNotContain("Cache-Control", html);
    }

    [Fact]
    public async Task EveryTextOfTheChecks_HasARussianTranslation()
    {
        using var site = new SiteFactory(Quiet(new() { ["Site:DataFeedUrl"] = "http://127.0.0.1:9/data.json", ["Site:FeedPollSeconds"] = "3600" }));
        var monitor = site.Get<HealthMonitor>();
        await monitor.RunChecksAsync();
        var keys = monitor.Results.SelectMany(r => new[] { r.Title, r.Value, r.Problem, r.Fix }).OfType<Say>().Select(s => s.Key)
            .Concat(HealthMonitor.Groups.Select(g => g.Title)).Distinct();
        Assert.Empty(keys.Where(k => !Ru.Texts.ContainsKey(k)));

        var admin = site.Browser();
        await admin.LoginAsync(site.Member("Anna Admin", Ratings.ADM));
        await admin.GetAsync("/lang/ru");
        string html = await admin.HtmlAsync("/staff/status");
        Assert.Contains("Что-то не работает", html);
        Assert.Contains("Сайт не получает данные от сервера сети", html);
    }

    [Fact]
    public void ServerFiles_AreRead()
    {
        Assert.Equal((0.08, 0.02, 0.04), HostProbe.LoadAverage("0.08 0.02 0.04 1/390 86796\n"));
        Assert.Null(HostProbe.LoadAverage("garbage"));
        Assert.Equal((8148052L, 6517792L), HostProbe.Memory("MemTotal:        8148052 kB\nMemFree:         1234 kB\nMemAvailable:    6517792 kB\n"));
        Assert.Equal(TimeSpan.FromSeconds(60432.13), HostProbe.Uptime("60432.13 240112.20"));

        const string tcp = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000:1A99 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 1
               1: 0100007F:1F40 0100007F:D2F0 01 00000000:00000000 00:00000000 00000000     0        0 2
            """;
        Assert.True(HostProbe.HasPort(tcp, 6809, tcp: true));
        Assert.False(HostProbe.HasPort(tcp, 8000, tcp: true));   // only a connection, not listening
        const string udp = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
             1: 00000000:0EC6 00000000:0000 07 00000000:00000000 00:00000000 00000000     0        0 3 2 0 0
            """;
        Assert.True(HostProbe.HasPort(udp, 3782, tcp: false));
        Assert.False(HostProbe.HasPort(null, 3782, tcp: false));
    }

    [Fact]
    public void ServiceJournals_AreRead()
    {
        string output = string.Join('\n',
            """{"__REALTIME_TIMESTAMP":"1790000000000000","_SYSTEMD_UNIT":"skynet-fsd.service","PRIORITY":"3","MESSAGE":"client 10.0.0.5 dropped after 31 s"}""",
            """{"__REALTIME_TIMESTAMP":"1790000060000000","_SYSTEMD_UNIT":"skynet-voice.service","PRIORITY":"4","MESSAGE":"late packet"}""",
            // systemd's own note about a service names the service in UNIT.
            """{"__REALTIME_TIMESTAMP":"1790000120000000","_SYSTEMD_UNIT":"init.scope","UNIT":"caddy.service","PRIORITY":"4","MESSAGE":"caddy.service: Failed with result 'timeout'."}""",
            """{"__REALTIME_TIMESTAMP":"17900""", // cut off
            """{"_SYSTEMD_UNIT":"caddy.service","MESSAGE":[104,105]}""");
        var entries = HostProbe.ParseJournal(output);
        Assert.Equal(3, entries.Count);
        Assert.Equal(("caddy", "Failed with result 'timeout'."), (entries[2].Source, entries[2].Message));
        Assert.Equal(("skynet-fsd", true, "client 10.0.0.5 dropped after 31 s"), (entries[0].Source, entries[0].IsError, entries[0].Message));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1790000000), entries[0].Time);
        Assert.Equal(("skynet-voice", false), (entries[1].Source, entries[1].IsError));
        Assert.Equal(HostProbe.TemplateOf("client 10.0.0.9 dropped after 2 s"), entries[0].Template);
        Assert.True(HostProbe.JournalDenied("No journal files were opened due to insufficient permissions."));
    }
}
