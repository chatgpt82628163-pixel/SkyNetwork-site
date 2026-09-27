using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Reflection;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Options;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Services.Health;

/// <summary>
/// Checks the whole infrastructure every half minute for the staff status page: the network server and its data feed,
/// the voice server, the Discord bot, the website from outside with its certificate, the database, mail, the server's
/// processor, memory and disk, and the programs' releases. Each problem comes with what it means and what to do.
/// Results of the last day are kept in half-hour steps for the timelines.
/// </summary>
public sealed class HealthMonitor(
    IOptions<SiteOptions> options, IOptions<MailOptions> mail, Database db, NetworkFeed feed, Mailer mailer, RecentLog recent,
    IHttpClientFactory http, ILogger<HealthMonitor> log) : BackgroundService
{
    public const string Network = "network", Website = "website", Server = "server", Programs = "programs";
    public static readonly IReadOnlyList<(string Id, string Title)> Groups =
        [(Network, "Network"), (Website, "Website"), (Server, "Server"), (Programs, "Programs")];

    /// <summary>A timeline covers a day in half hours.</summary>
    public const int Buckets = 48;
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(30);

    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Dictionary<string, Dictionary<long, HealthLevel>> _history = [];
    private readonly Dictionary<string, (HealthCheck Result, DateTime At)> _slow = [];
    private volatile IReadOnlyList<HealthCheck> _results = [];
    private IReadOnlyList<LogEntry> _journal = [];
    private DateTime _lastRun;

    public IReadOnlyList<HealthCheck> Results => _results;
    public DateTime LastRun => _lastRun;

    /// <summary>The worst level among the checks (Unknown counts as fine: it is not a problem, only not visible here).</summary>
    public HealthLevel Overall => _results.Select(r => r.Level == HealthLevel.Unknown ? HealthLevel.Ok : r.Level).DefaultIfEmpty().Max();

    /// <summary>The worst level of each half hour of the last day, oldest first; null where nothing was checked.</summary>
    public HealthLevel?[] Timeline(string id)
    {
        long now = Slot(DateTime.UtcNow);
        var line = new HealthLevel?[Buckets];
        lock (_history)
        {
            if (!_history.TryGetValue(id, out var h)) return line;
            for (int i = 0; i < Buckets; i++)
                if (h.TryGetValue(now - (Buckets - 1 - i), out var level)) line[i] = level;
        }
        return line;
    }

    private static long Slot(DateTime t) => t.Ticks / Bucket.Ticks;

    /// <summary>Warnings and errors of the last day from the site and the services, repeats counted together, newest first.</summary>
    public IReadOnlyList<LogGroup> Problems(int max = 200)
    {
        var since = DateTime.UtcNow - TimeSpan.FromHours(24);
        return recent.Since(since).Concat(_journal.Where(e => e.Time >= since))
            .GroupBy(e => (e.Source, e.IsError, e.Template))
            .Select(g =>
            {
                var last = g.MaxBy(e => e.Time)!;
                return new LogGroup(g.Key.Source, g.Key.IsError, last.Message, last.Details, g.Count(), g.Min(e => e.Time), last.Time);
            })
            .OrderByDescending(g => g.Last).Take(max).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        int seconds = options.Value.HealthCheckSeconds;
        if (seconds <= 0) return;
        try { await Task.Delay(TimeSpan.FromSeconds(5), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await RunChecksAsync(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Status checks failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, seconds)), stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>"Check now" on the page: at most every 10 seconds.</summary>
    public async Task RunNowAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastRun < TimeSpan.FromSeconds(10)) return;
        await RunChecksAsync(ct);
    }

    /// <summary>Runs every check once (also used by the tests).</summary>
    public async Task RunChecksAsync(CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            var o = options.Value;
            var checks = new List<Task<HealthCheck>>
            {
                Guarded("fsd", Network, "Network server", () => ServiceCheck("fsd", Network, "Network server", o.FsdService, o.FsdPort, tcp: true, ct)),
                Guarded("feed", Network, "Live data", () => Task.FromResult(FeedCheck())),
                Guarded("voice", Network, "Voice server", () => ServiceCheck("voice", Network, "Voice server", o.VoiceService, o.VoicePort, tcp: false, ct)),
                Guarded("bot", Network, "Discord bot", () => BotCheck(ct)),
                Guarded("site", Website, "Website", () => Task.FromResult(SiteCheck())),
                Guarded("public", Website, "Address and HTTPS", () => Slow("public", TimeSpan.FromMinutes(5), () => PublicCheck(ct))),
                Guarded("database", Website, "Database", () => Task.FromResult(DatabaseCheck())),
                Guarded("mail", Website, "Email", () => Task.FromResult(MailCheck())),
                Guarded("errors", Website, "Website errors", () => Task.FromResult(ErrorsCheck())),
                Guarded("cpu", Server, "Processor", () => Task.FromResult(CpuCheck())),
                Guarded("memory", Server, "Memory", () => Task.FromResult(MemoryCheck())),
                Guarded("disk", Server, "Disk", () => Task.FromResult(DiskCheck())),
                Guarded("uptime", Server, "Server running", () => Task.FromResult(UptimeCheck())),
                Guarded("web", Server, "Web server", () => ServiceCheck("web", Server, "Web server", o.WebService, null, tcp: true, ct)),
                Guarded("logs", Server, "Service logs", () => Slow("logs", TimeSpan.FromMinutes(2), () => JournalCheck(ct))),
            };
            // "owner/repo=Name" or just "owner/repo".
            foreach (var entry in o.ReleaseRepos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = entry.IndexOf('=');
                string repo = eq > 0 ? entry[..eq].Trim() : entry;
                string id = "release:" + repo, name = eq > 0 ? entry[(eq + 1)..].Trim() : repo[(repo.IndexOf('/') + 1)..];
                checks.Add(Guarded(id, Programs, name, () => Slow(id, TimeSpan.FromMinutes(60), () => ReleaseCheck(id, repo, name, ct))));
            }
            var results = await Task.WhenAll(checks);
            _results = results;
            _lastRun = DateTime.UtcNow;
            lock (_history)
            {
                long slot = Slot(_lastRun);
                foreach (var r in results)
                {
                    if (!_history.TryGetValue(r.Id, out var h)) _history[r.Id] = h = [];
                    h[slot] = h.TryGetValue(slot, out var was) && was > r.Level ? was : r.Level;
                    foreach (var old in h.Keys.Where(k => k <= slot - Buckets).ToList()) h.Remove(old);
                }
            }
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>A check that throws is a problem of its own, shown as such, not a gap on the page.</summary>
    private async Task<HealthCheck> Guarded(string id, string group, string title, Func<Task<HealthCheck>> run)
    {
        try
        {
            return await run();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Status check {Id} failed", id);
            return new HealthCheck(id, group, new Say(title), HealthLevel.Unknown, new Say("Not checked"),
                new Say("The check itself failed: {0}", e.Message));
        }
    }

    /// <summary>Checks that call other servers or take longer run less often; the last result is shown in between.</summary>
    private async Task<HealthCheck> Slow(string id, TimeSpan every, Func<Task<HealthCheck>> run)
    {
        lock (_slow)
            if (_slow.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.At < every) return cached.Result;
        var result = await run();
        lock (_slow) _slow[id] = (result, DateTime.UtcNow);
        return result;
    }

    // ---- network --------------------------------------------------------------------------------

    private static readonly Dictionary<string, (Say Down, string Service)> Consequences = new()
    {
        ["fsd"] = (new Say("Pilots and controllers cannot connect to the network."), "skynet-fsd"),
        ["voice"] = (new Say("Voice on frequencies does not work: pilots and controllers cannot hear each other."), "skynet-voice"),
        ["web"] = (new Say("The website and the map do not open from outside."), "caddy"),
    };

    /// <summary>A systemd service and, when it has one, its port: listening (TCP) or bound (UDP) on this server.</summary>
    private async Task<HealthCheck> ServiceCheck(string id, string group, string title, string unit, int? port, bool tcp, CancellationToken ct)
    {
        var what = Consequences[id];
        string? state = unit.Length > 0 ? await HostProbe.ServiceState(unit, ct) : null;
        bool? open = port is { } p ? HostProbe.PortOpen(p, tcp) : null;
        string proto = tcp ? "TCP" : "UDP";
        if (state == null && open == null)
            return new HealthCheck(id, group, new Say(title), HealthLevel.Unknown, new Say("Visible only on the server"));
        if (state is not (null or "active"))
        {
            return new HealthCheck(id, group, new Say(title), HealthLevel.Error, new Say("Service {0}: {1}", unit, state),
                new Say("The service {0} is not running ({1}). {2}", unit, state, what.Down),
                new Say("Look at why it stopped and start it again on the server:"))
            { Command = $"ssh SW \"systemctl status {unit} --no-pager; systemctl restart {unit}\"" };
        }
        if (open == false)
        {
            return new HealthCheck(id, group, new Say(title), HealthLevel.Error, new Say("Port {0} {1} closed", port, proto),
                new Say("The service is running but does not accept connections on port {0} ({1}). {2}", port, proto, what.Down),
                new Say("Restart the service and look at its log on the server:"))
            { Command = $"ssh SW \"systemctl restart {unit}; journalctl -u {unit} -n 50 --no-pager\"" };
        }
        return port is { } q
            ? new HealthCheck(id, group, new Say(title), HealthLevel.Ok, new Say("Running · port {0} {1} open", q, proto))
            : new HealthCheck(id, group, new Say(title), HealthLevel.Ok, new Say("Running"));
    }

    private HealthCheck FeedCheck()
    {
        var title = new Say("Live data");
        if (string.IsNullOrWhiteSpace(options.Value.DataFeedUrl))
            return new HealthCheck("feed", Network, title, HealthLevel.Unknown, new Say("Not set up"),
                new Say("The website is not set up to read the network server's data feed (Site:DataFeedUrl)."));
        var s = feed.Current;
        if (!s.Available)
            return new HealthCheck("feed", Network, title, HealthLevel.Error, new Say("No data"),
                new Say("The website gets no data from the network server. The map and the online list are empty, and flights and hours are not recorded."),
                new Say("Make sure the network server is running and its data feed answers on the server:"))
            { Command = $"ssh SW \"curl -s -m 5 {options.Value.DataFeedUrl} | head -c 300\"" };
        var age = DateTime.UtcNow - s.Updated;
        var value = new Say("{0} pilots · {1} controllers", s.Pilots.Count, s.Controllers.Count);
        int stale = Math.Max(90, options.Value.FeedPollSeconds * 6);
        if (s.Updated != default && age.TotalSeconds > stale)
            return new HealthCheck("feed", Network, title, HealthLevel.Warning, value,
                new Say("The data from the network server is {0:0} minutes old: the server answers but its data does not change. It may be stuck.", age.TotalMinutes),
                new Say("Restart the network server (everyone online is disconnected):"))
            { Command = $"ssh SW \"systemctl restart {options.Value.FsdService}\"" };
        return new HealthCheck("feed", Network, title, HealthLevel.Ok, value);
    }

    private async Task<HealthCheck> BotCheck(CancellationToken ct)
    {
        var title = new Say("Discord bot");
        string unit = options.Value.BotService, url = options.Value.DiscordBotUrl;
        string? state = unit.Length > 0 ? await HostProbe.ServiceState(unit, ct) : null;
        if (state is not (null or "active"))
            return new HealthCheck("bot", Network, title, HealthLevel.Error, new Say("Service {0}: {1}", unit, state),
                new Say("The Discord bot is not running ({0}): roles and nicknames in Discord are not updated, and signing in to Discord through the website fails.", state),
                new Say("Look at why it stopped and start it again on the server:"))
            { Command = $"ssh SW \"systemctl status {unit} --no-pager; systemctl restart {unit}\"" };
        if (url.Length > 0)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var r = await http.CreateClient("health").GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return new HealthCheck("bot", Network, title, HealthLevel.Ok, new Say("Running · answers"));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new HealthCheck("bot", Network, title, state == null ? HealthLevel.Unknown : HealthLevel.Warning, new Say("Does not answer"),
                    new Say("The bot's web page ({0}) does not answer: signing in to Discord through the website fails.", url),
                    new Say("Restart the bot on the server:"))
                { Command = $"ssh SW \"systemctl restart {unit}\"" };
            }
        }
        return state == null
            ? new HealthCheck("bot", Network, title, HealthLevel.Unknown, new Say("Visible only on the server"))
            : new HealthCheck("bot", Network, title, HealthLevel.Ok, new Say("Running"));
    }

    // ---- website --------------------------------------------------------------------------------

    public static string Version
    {
        get
        {
            var v = typeof(HealthMonitor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            int plus = v.IndexOf('+');
            // "1.0.0+de7e9b3…": the commit the site was built from says more than the number.
            return plus >= 0 && v.Length > plus + 7 ? v[(plus + 1)..(plus + 8)] : v;
        }
    }

    private static HealthCheck SiteCheck()
    {
        var up = DateTime.UtcNow - Started;
        long mb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        return new HealthCheck("site", Website, new Say("Website"), HealthLevel.Ok,
            new Say("Version {0} · running {1} · {2} MB", Version, up, mb));
    }

    /// <summary>The address the world reaches the site at: the setting, the one letters use, or the network host name.</summary>
    public string? PublicAddress()
    {
        string u = options.Value.PublicUrl.Trim();
        if (u.Length == 0) u = mail.Value.SiteUrl.Trim();
        if (u.Length == 0)
        {
            string host = options.Value.FsdHost.Trim();
            if (host.Length == 0 || host == "localhost" || IPAddress.TryParse(host, out _)) return null;
            u = "https://" + host;
        }
        return Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.GetLeftPart(UriPartial.Authority) + "/" : null;
    }

    private async Task<HealthCheck> PublicCheck(CancellationToken ct)
    {
        var title = new Say("Address and HTTPS");
        if (PublicAddress() is not { } url)
            return new HealthCheck("public", Website, title, HealthLevel.Unknown, new Say("Address not known"),
                new Say("The site does not know its public address, so it cannot check itself from outside (set Site:PublicUrl)."));
        DateTime? expires = null;
        SslPolicyErrors certErrors = SslPolicyErrors.None;
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert != null) expires = cert.NotAfter.ToUniversalTime();
                certErrors = errors;
                return errors == SslPolicyErrors.None;
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SkyNetworkSite-status/1.0");
        var sw = Stopwatch.StartNew();
        HttpStatusCode code;
        try
        {
            using var r = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            code = r.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (certErrors != SslPolicyErrors.None)
                return new HealthCheck("public", Website, title, HealthLevel.Error, new Say("Certificate not valid"),
                    new Say("Browsers do not trust the site's HTTPS certificate ({0}): visitors see a security warning instead of the site.", certErrors),
                    new Say("The web server (Caddy) gets certificates by itself; look at its log on the server:"))
                { Command = $"ssh SW \"journalctl -u {options.Value.WebService} -n 50 --no-pager\"" };
            return new HealthCheck("public", Website, title, HealthLevel.Error, new Say("Does not open"),
                new Say("The site does not open from outside at {0} ({1}). Visitors see an error.", url, e.Message),
                new Say("Check the web server (Caddy) and the website service on the server:"))
            { Command = $"ssh SW \"systemctl status {options.Value.WebService} skynetwork-site --no-pager\"" };
        }
        long ms = sw.ElapsedMilliseconds;
        if ((int)code >= 500)
            return new HealthCheck("public", Website, title, HealthLevel.Error, new Say("Answers {0}", (int)code),
                new Say("The site answers from outside with error {0}: the web server works but cannot reach the website.", (int)code),
                new Say("Check the website service on the server:"))
            { Command = "ssh SW \"systemctl status skynetwork-site --no-pager\"" };
        if (expires is { } until)
        {
            double days = (until - DateTime.UtcNow).TotalDays;
            var value = new Say("Opens in {0} ms · certificate for {1:0} more days", ms, Math.Max(0, days));
            if (days < 14)
                return new HealthCheck("public", Website, title, days < 3 ? HealthLevel.Error : HealthLevel.Warning, value,
                    new Say("The HTTPS certificate runs out in {0:0} days and has not been renewed. When it runs out, browsers will not open the site.", Math.Max(0, days)),
                    new Say("The web server (Caddy) renews it by itself; look at its log on the server:"))
                { Command = $"ssh SW \"journalctl -u {options.Value.WebService} -n 50 --no-pager | grep -i -E 'certificate|acme|error'\"" };
            return new HealthCheck("public", Website, title, ms > 3000 ? HealthLevel.Warning : HealthLevel.Ok, value,
                ms > 3000 ? new Say("The site opens slowly from outside ({0} ms).", ms) : null);
        }
        return new HealthCheck("public", Website, title, HealthLevel.Ok, new Say("Opens in {0} ms", ms));
    }

    private DateTime _lastIntegrity;
    private string _integrity = "ok";

    private HealthCheck DatabaseCheck()
    {
        var title = new Say("Database");
        var sw = Stopwatch.StartNew();
        long members;
        try
        {
            using var c = db.Open();
            members = c.ExecuteScalar<long>("SELECT COUNT(*) FROM members");
            // A full integrity check now and then (the database is small; it only reads).
            if (DateTime.UtcNow - _lastIntegrity > TimeSpan.FromHours(6))
            {
                _integrity = c.ExecuteScalar<string>("PRAGMA quick_check") ?? "ok";
                _lastIntegrity = DateTime.UtcNow;
            }
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new HealthCheck("database", Website, title, HealthLevel.Error, new Say("Cannot be read"),
                new Say("The database cannot be read ({0}). Signing in to the website and to the network fails.", e.Message),
                new Say("Check the file and its owner on the server:"))
            { Command = $"ssh SW \"ls -la {Path.GetDirectoryName(Path.GetFullPath(options.Value.Database))}\"" };
        }
        long ms = sw.ElapsedMilliseconds;
        string file = Path.GetFullPath(options.Value.Database);
        double sizeMb = SizeMb(file), walMb = SizeMb(file + "-wal");
        var value = new Say("{0} members · {1:0.0} MB · {2} ms", members, sizeMb + walMb, ms);
        if (!string.Equals(_integrity, "ok", StringComparison.OrdinalIgnoreCase))
            return new HealthCheck("database", Website, title, HealthLevel.Error, value,
                new Say("The database is damaged: {0}. Restore it from the latest backup.", _integrity),
                new Say("Stop the website and the network server before restoring; ask for help if unsure."));
        if (walMb > 64)
            return new HealthCheck("database", Website, title, HealthLevel.Warning, value,
                new Say("The database's change journal has grown to {0:0} MB: something keeps it from being written back into the database.", walMb),
                new Say("Restarting the website and the network server writes it back:"))
            { Command = "ssh SW \"systemctl restart skynetwork-site skynet-fsd\"" };
        if (ms > 1000)
            return new HealthCheck("database", Website, title, HealthLevel.Warning, value,
                new Say("The database answers slowly ({0} ms): pages open slowly.", ms));
        return new HealthCheck("database", Website, title, HealthLevel.Ok, value);
    }

    private static double SizeMb(string path) => File.Exists(path) ? new FileInfo(path).Length / 1048576.0 : 0;

    private HealthCheck MailCheck()
    {
        var title = new Say("Email");
        if (!mailer.Enabled)
            return new HealthCheck("mail", Website, title, HealthLevel.Warning, new Say("Not set up"),
                new Say("Email is not set up: address confirmation, password reset and notifications are not sent. New members are let in without confirming their address."),
                new Say("Enter the mailbox and its app password on the server (the command asks for them and checks them):"))
            { Command = "ssh -t SW \"bash ~/set-mail.sh\"" };
        var value = mailer.LastSent is { } sent ? new Say("Set up · last letter {0}", sent) : new Say("Set up · no letters yet");
        if (mailer.LastFailure is { } f && (mailer.LastSent == null || f.At > mailer.LastSent) && DateTime.UtcNow - f.At < TimeSpan.FromHours(24))
            return new HealthCheck("mail", Website, title, HealthLevel.Error, value,
                new Say("Letters are not sent: the mail server refused the last one ({0}). Members do not get confirmation and password reset letters.", f.Error),
                new Say("Check the mailbox and its app password again on the server:"))
            { Command = "ssh -t SW \"bash ~/set-mail.sh\"" };
        if (mailer.Waiting > 20)
            return new HealthCheck("mail", Website, title, HealthLevel.Warning, value,
                new Say("{0} letters are waiting to be sent: the mail server is slow or refuses them.", mailer.Waiting));
        return new HealthCheck("mail", Website, title, HealthLevel.Ok, value);
    }

    private HealthCheck ErrorsCheck()
    {
        var title = new Say("Website errors");
        var day = recent.Since(DateTime.UtcNow - TimeSpan.FromHours(24));
        int errors = day.Count(e => e.IsError), warnings = day.Count - errors;
        var hour = day.Where(e => e.Time >= DateTime.UtcNow - TimeSpan.FromHours(1)).ToList();
        var value = new Say("{0} errors · {1} warnings in 24 h", errors, warnings);
        if (hour.Any(e => e.IsError))
            return new HealthCheck("errors", Website, title, HealthLevel.Error, value,
                new Say("The website had errors in the last hour: some pages or actions fail. See the list of problems below."));
        if (hour.Count > 0)
            return new HealthCheck("errors", Website, title, HealthLevel.Warning, value,
                new Say("The website had warnings in the last hour (usually an outside service not answering). See the list of problems below."));
        return new HealthCheck("errors", Website, title, HealthLevel.Ok, value);
    }

    // ---- server ---------------------------------------------------------------------------------

    private static HealthCheck CpuCheck()
    {
        var title = new Say("Processor");
        if (HostProbe.LoadAverage(HostProbe.Read("/proc/loadavg")) is not { } load)
            return new HealthCheck("cpu", Server, title, HealthLevel.Unknown, new Say("Visible only on the server"));
        int cores = Environment.ProcessorCount;
        double perCore = load.Five / cores;
        var value = new Say("Load {0:0.00} on {1} cores", load.Five, cores);
        if (perCore > 2)
            return new HealthCheck("cpu", Server, title, HealthLevel.Error, value,
                new Say("The server is overloaded: everything answers late, pilots may see jumps and lost connections."),
                new Say("See what uses the processor on the server:"))
            { Command = "ssh SW \"top -b -n 1 | head -20\"" };
        if (perCore > 1)
            return new HealthCheck("cpu", Server, title, HealthLevel.Warning, value,
                new Say("The server is busy (more work than processor cores): pages and the network may answer late."),
                new Say("See what uses the processor on the server:"))
            { Command = "ssh SW \"top -b -n 1 | head -20\"" };
        return new HealthCheck("cpu", Server, title, HealthLevel.Ok, value);
    }

    private static HealthCheck MemoryCheck()
    {
        var title = new Say("Memory");
        if (HostProbe.Memory(HostProbe.Read("/proc/meminfo")) is not { } m)
            return new HealthCheck("memory", Server, title, HealthLevel.Unknown, new Say("Visible only on the server"));
        double freeGb = m.AvailableKb / 1048576.0, totalGb = m.TotalKb / 1048576.0, share = (double)m.AvailableKb / m.TotalKb;
        var value = new Say("{0:0.0} GB free of {1:0.0} GB", freeGb, totalGb);
        if (share < 0.05)
            return new HealthCheck("memory", Server, title, HealthLevel.Error, value,
                new Say("The server has almost no free memory: the system will soon stop services by force."),
                new Say("See what uses the memory on the server:"))
            { Command = "ssh SW \"ps aux --sort=-rss | head -10\"" };
        if (share < 0.15)
            return new HealthCheck("memory", Server, title, HealthLevel.Warning, value,
                new Say("Free memory is running low."),
                new Say("See what uses the memory on the server:"))
            { Command = "ssh SW \"ps aux --sort=-rss | head -10\"" };
        return new HealthCheck("memory", Server, title, HealthLevel.Ok, value);
    }

    private HealthCheck DiskCheck()
    {
        var title = new Say("Disk");
        string dir = Path.GetDirectoryName(Path.GetFullPath(options.Value.Database)) ?? "/";
        // The mounted disk the database lives on: the longest mount point that holds its folder.
        var drive = DriveInfo.GetDrives().Where(d => d.IsReady && dir.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
            .MaxBy(d => d.RootDirectory.FullName.Length);
        if (drive == null || drive.TotalSize <= 0)
            return new HealthCheck("disk", Server, title, HealthLevel.Unknown, new Say("Not checked"));
        double freeGb = drive.AvailableFreeSpace / 1073741824.0, totalGb = drive.TotalSize / 1073741824.0, share = freeGb / totalGb;
        var value = new Say("{0:0.0} GB free of {1:0} GB", freeGb, totalGb);
        if (share < 0.05 || freeGb < 1)
            return new HealthCheck("disk", Server, title, HealthLevel.Error, value,
                new Say("The disk is almost full: the database cannot save new members, flights and changes."),
                new Say("See what takes the space on the server (old backups and logs can go):"))
            { Command = "ssh SW \"du -sh /root/* /var/lib/skynetwork/* 2>/dev/null | sort -h | tail -15\"" };
        if (share < 0.15)
            return new HealthCheck("disk", Server, title, HealthLevel.Warning, value,
                new Say("Free disk space is running low."),
                new Say("See what takes the space on the server (old backups and logs can go):"))
            { Command = "ssh SW \"du -sh /root/* /var/lib/skynetwork/* 2>/dev/null | sort -h | tail -15\"" };
        return new HealthCheck("disk", Server, title, HealthLevel.Ok, value);
    }

    private static HealthCheck UptimeCheck()
    {
        var title = new Say("Server running");
        if (HostProbe.Uptime(HostProbe.Read("/proc/uptime")) is not { } up)
            return new HealthCheck("uptime", Server, title, HealthLevel.Unknown, new Say("Visible only on the server"));
        var value = new Say("For {0}", up);
        if (up < TimeSpan.FromMinutes(15))
            return new HealthCheck("uptime", Server, title, HealthLevel.Warning, value,
                new Say("The server started again a few minutes ago: everyone online was disconnected."));
        return new HealthCheck("uptime", Server, title, HealthLevel.Ok, value);
    }

    /// <summary>Whether the site may read the services' journals; when it may, their warnings join the list of problems.</summary>
    private async Task<HealthCheck> JournalCheck(CancellationToken ct)
    {
        var title = new Say("Service logs");
        var o = options.Value;
        var units = new[] { o.FsdService, o.VoiceService, o.BotService, o.WebService }.Where(u => u.Length > 0).ToList();
        var args = new List<string> { "-p", "warning", "--since", "-24h", "-o", "json", "--no-pager", "-n", "300" };
        foreach (var u in units) { args.Add("-u"); args.Add(u); }
        var run = await HostProbe.RunAsync("journalctl", args, TimeSpan.FromSeconds(10), ct);
        if (run is not { } r)
            return new HealthCheck("logs", Server, title, HealthLevel.Unknown, new Say("Visible only on the server"));
        if (HostProbe.JournalDenied(r.Output))
        {
            _journal = [];
            return new HealthCheck("logs", Server, title, HealthLevel.Unknown, new Say("No access"),
                new Say("The website may not read the logs of the network server, the voice server, the bot and the web server, so their errors are not in the list below."),
                new Say("Allow it once on the server (the website restarts in a second):"))
            { Command = "ssh SW \"usermod -aG systemd-journal skynetwork && systemctl restart skynetwork-site\"" };
        }
        _journal = HostProbe.ParseJournal(r.Output);
        int errors = _journal.Count(e => e.IsError);
        return new HealthCheck("logs", Server, title, HealthLevel.Ok, new Say("{0} errors · {1} warnings in 24 h", errors, _journal.Count - errors));
    }

    // ---- programs -------------------------------------------------------------------------------

    private async Task<HealthCheck> ReleaseCheck(string id, string repo, string name, CancellationToken ct)
    {
        var title = new Say("{0}: latest release", name);
        ReleaseDto? release;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var r = await http.CreateClient("health").SendAsync(request, ct);
            if (r.StatusCode == HttpStatusCode.NotFound)
                return new HealthCheck(id, Programs, title, HealthLevel.Warning, new Say("No releases"),
                    new Say("{0} has no published release: nobody can download it and installed copies cannot update.", name));
            if (!r.IsSuccessStatusCode)
                return new HealthCheck(id, Programs, title, HealthLevel.Unknown, new Say("GitHub answers {0}", (int)r.StatusCode));
            release = await r.Content.ReadFromJsonAsync<ReleaseDto>(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            return new HealthCheck(id, Programs, title, HealthLevel.Unknown, new Say("GitHub does not answer"));
        }
        if (release?.Tag == null) return new HealthCheck(id, Programs, title, HealthLevel.Unknown, new Say("GitHub does not answer"));
        var value = new Say("{0} · {1}", release.Tag, release.Published ?? DateTime.UtcNow);
        bool hasSetup = release.Assets?.Any(a => a.Name?.Contains("-Setup-", StringComparison.OrdinalIgnoreCase) == true &&
                                                 a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasSetup)
            return new HealthCheck(id, Programs, title, HealthLevel.Warning, value,
                new Say("The latest release {0} has no installer (.exe): installed copies cannot update themselves.", release.Tag),
                new Say("Publish releases with a version tag (for example 0.2.0) from main: GitHub then builds and attaches the installer by itself."));
        return new HealthCheck(id, Programs, title, HealthLevel.Ok, value);
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? Tag { get; set; }
        [JsonPropertyName("published_at")] public DateTime? Published { get; set; }
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
