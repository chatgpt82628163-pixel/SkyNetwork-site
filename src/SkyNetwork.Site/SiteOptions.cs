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
    /// The Discord bot's own web page (github.com/Anntixs/skynetwork_bot): /discord/… is passed on to it, so members come
    /// back to it after signing in with SkyNetwork Connect. Empty turns this off.
    /// </summary>
    public string DiscordBotUrl { get; set; } = "http://127.0.0.1:8090";
}
