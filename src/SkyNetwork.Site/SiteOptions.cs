namespace SkyNetwork.Site;

/// <summary>Settings from the "Site" section of appsettings.json.</summary>
public sealed class SiteOptions
{
    public string Name { get; set; } = "SkyNetwork";
    /// <summary>SQLite file shared with the FSD server (its --db): members registered here can log in to the network.</summary>
    public string Database { get; set; } = "skynetwork.db";
    /// <summary>FSD server data feed; empty disables the live data.</summary>
    public string DataFeedUrl { get; set; } = "http://127.0.0.1:8080/data.json";
    public int FeedPollSeconds { get; set; } = 5;
    /// <summary>Shown on the "how to connect" pages.</summary>
    public string FsdHost { get; set; } = "127.0.0.1";
    public int FsdPort { get; set; } = 6809;
    /// <summary>CID given to the first registered member.</summary>
    public int FirstCid { get; set; } = 1;
    /// <summary>
    /// Map tile layers by name, each a list of sources ({z}/{x}/{y}) tried in order; a layer missing here uses
    /// <see cref="DefaultTileLayers"/>. The site downloads and caches the tiles itself, so visitors' browsers only talk to this site.
    /// </summary>
    public Dictionary<string, string[]> TileLayers { get; set; } = [];
    private const string Esri = "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/";
    // CARTO now answers every request without an API key with a watermarked "API KEY REQUIRED" tile.
    public static readonly IReadOnlyDictionary<string, string[]> DefaultTileLayers = new Dictionary<string, string[]>
    {
        ["light"] = [Esri + "World_Light_Gray_Base/MapServer/tile/{z}/{y}/{x}", "https://tile.openstreetmap.org/{z}/{x}/{y}.png"],
        // Transparent city and country labels drawn over the base layer.
        ["light-labels"] = [Esri + "World_Light_Gray_Reference/MapServer/tile/{z}/{y}/{x}"],
        ["dark"] = [Esri + "World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}", "https://tile.openstreetmap.org/{z}/{x}/{y}.png"],
        ["dark-labels"] = [Esri + "World_Dark_Gray_Reference/MapServer/tile/{z}/{y}/{x}"],
    };
    /// <summary>Tile cache directory; empty means "tiles" next to the database.</summary>
    public string TileCache { get; set; } = "";
    /// <summary>Directory for uploaded banners; empty means "uploads" next to the database.</summary>
    public string Uploads { get; set; } = "";
    /// <summary>Behind nginx or another reverse proxy: trust its X-Forwarded-For / X-Forwarded-Proto headers.</summary>
    public bool BehindProxy { get; set; }
    /// <summary>Login/registration attempts per minute from one address.</summary>
    public int AuthAttemptsPerMinute { get; set; } = 10;
    /// <summary>Accounts that can be registered from one address per day (against junk registrations).</summary>
    public int RegistrationsPerDayPerAddress { get; set; } = 3;
    /// <summary>Seconds the registration form must be open before it is sent (bots send it at once); 0 turns the check off.</summary>
    public int SignupMinSeconds { get; set; } = 3;
    /// <summary>
    /// The Discord bot's own web page (github.com/chatgpt82628163-pixel/Skynetwork-bot): /discord/… is passed on to it, so members come
    /// back to it after signing in with SkyNetwork Connect. Empty turns this off.
    /// </summary>
    public string DiscordBotUrl { get; set; } = "http://127.0.0.1:8090";

    /// <summary>
    /// Hours after which a registration whose email is still not confirmed (and whose last link has expired) is
    /// deleted; 0 keeps them. Only while mail is set up: without it nobody is asked to confirm.
    /// </summary>
    public int UnconfirmedHours { get; set; } = 48;

    // ---- the status page (/staff/status) ----

    /// <summary>Seconds between the infrastructure checks of the status page; 0 turns the background checks off.</summary>
    public int HealthCheckSeconds { get; set; } = 30;
    /// <summary>The site's public address for the outside and HTTPS check; empty: the Mail SiteUrl, else the FSD host name.</summary>
    public string PublicUrl { get; set; } = "";
    /// <summary>systemd services of the network server, the voice server, the Discord bot and the web server; empty skips one.</summary>
    public string FsdService { get; set; } = "skynet-fsd";
    public string VoiceService { get; set; } = "skynet-voice";
    public string BotService { get; set; } = "skynetwork-bot";
    public string WebService { get; set; } = "caddy";
    /// <summary>UDP port of the voice server.</summary>
    public int VoicePort { get; set; } = 3782;
    /// <summary>GitHub repositories of the programs whose latest release the status page checks: "owner/repo=Name", comma-separated.</summary>
    public string ReleaseRepos { get; set; } = "chatgpt82628163-pixel/SkyPilot=SkyPilot,chatgpt82628163-pixel/Network-ATC=Network-ATC";

    /// <summary>
    /// Comma-separated ICAO codes to warm up in the background at startup and daily, in addition to airports seen
    /// in the current network feed and recent flight activity. Default: main Russian and nearby airports.
    /// </summary>
    public string AirportWarmup { get; set; } = "UUEE,UUDD,UUWW,UUBW,ULLI,URSS,URKK,UWWW,USSS,UNNT,UHWW,UMMS";
}
