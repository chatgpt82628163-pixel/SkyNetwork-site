using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Microsoft.Extensions.WebEncoders;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using SkyNetwork.Site;
using SkyNetwork.Site.Api;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Localization;
using SkyNetwork.Site.Security;
using SkyNetwork.Site.Services;

var builder = WebApplication.CreateBuilder(args);

// The site's own warnings and errors of the last day, for the status page.
var recentLog = new SkyNetwork.Site.Services.Health.RecentLog();
builder.Services.AddSingleton(recentLog);
builder.Logging.AddProvider(recentLog);

builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection("Site"));
builder.Services.AddSingleton<Database>();
builder.Services.Configure<MailOptions>(builder.Configuration.GetSection("Mail"));
builder.Services.AddSingleton<IMailSender, SmtpMailSender>();
builder.Services.AddSingleton<Mailer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Mailer>());
builder.Services.AddSingleton<Notifications>();
builder.Services.AddSingleton<EmailTokenService>();
builder.Services.AddSingleton<AccountMail>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<MemberService>();
builder.Services.AddSingleton<ContentService>();
builder.Services.AddSingleton<FlightPlanService>();
builder.Services.AddSingleton<SupportService>();
builder.Services.AddSingleton<DivisionService>();
builder.Services.AddSingleton<ConnectService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<FriendService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<Lang>();
builder.Services.AddHostedService<SuspensionExpiry>();
builder.Services.AddHostedService<UnconfirmedAccounts>();
builder.Services.AddHttpClient("feed", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("discord-bot", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<NetworkFeed>();
builder.Services.AddHttpClient("tiles", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    // Tile providers require an identifying User-Agent.
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SkyNetworkSite/1.0 (+https://github.com/Anntixs/Skynetwork-site)");
});
builder.Services.AddSingleton<TileProxy>();
builder.Services.AddSingleton<NavData>();
builder.Services.AddHttpClient("simbrief", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Simbrief>();
builder.Services.AddHttpClient("metar", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<MetarService>();
builder.Services.AddHttpClient("overpass", c =>
{
    c.Timeout = TimeSpan.FromSeconds(45);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SkyNetworkSite/1.0 (+https://github.com/Anntixs/Skynetwork-site)");
});
builder.Services.AddSingleton<AirportLayout>();
builder.Services.AddSingleton<UploadStore>();
builder.Services.AddSingleton<SkyNetwork.Site.Security.SignupGuard>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkFeed>());
builder.Services.AddHttpClient("health", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SkyNetworkSite/1.0 (+https://github.com/Anntixs/Skynetwork-site)");
});
builder.Services.AddSingleton<SkyNetwork.Site.Services.Health.HealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SkyNetwork.Site.Services.Health.HealthMonitor>());

// Cyrillic stays as text in the HTML instead of &#x...; entities.
builder.Services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/logout";
        o.AccessDeniedPath = "/login";
        o.Cookie.Name = "skynetwork";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Account");
    o.Conventions.AuthorizePage("/FlightPlan");
}).AddMvcOptions(o => o.ModelMetadataDetailsProviders.Add(new KeepEmptyStrings()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Login and registration: a few attempts per minute per address.
    int limit = builder.Configuration.GetValue("Site:AuthAttemptsPerMinute", 10);
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1) }));
    // SkyNetwork Connect token and profile requests: per address.
    int connectLimit = builder.Configuration.GetValue("Site:ConnectRequestsPerMinute", 120);
    o.AddPolicy("connect", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = connectLimit, Window = TimeSpan.FromMinutes(1) }));
    // Division API: per key (or address when there is none).
    int divisionLimit = builder.Configuration.GetValue("Site:DivisionApiRequestsPerMinute", 120);
    o.AddPolicy("division-api", ctx => RateLimitPartition.GetFixedWindowLimiter(
        DivisionApi.KeyOf(ctx) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = divisionLimit, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddCors(o => o.AddPolicy("api", p => p.AllowAnyOrigin().AllowAnyHeader().WithMethods("GET")));

var app = builder.Build();
app.Services.GetRequiredService<Database>().Migrate();

if (app.Configuration.GetValue<bool>("Site:BehindProxy"))
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error/500");
app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseCors();
app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    // Language from the cookie (English by default); dates and units follow it.
    var lang = ctx.RequestServices.GetRequiredService<Lang>();
    lang.Set(ctx.Request.Cookies[Lang.Cookie]);
    System.Globalization.CultureInfo.CurrentUICulture = lang.Culture;
    var user = ctx.RequestServices.GetRequiredService<CurrentUser>();
    await user.LoadAsync(ctx);
    // The staff area does not exist for anyone else: plain 404, no login redirect, not indexed.
    if (ctx.Request.Path.StartsWithSegments("/staff"))
    {
        if (!user.IsStaff)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        ctx.Response.Headers.CacheControl = "no-store";
    }
    await next();
});
app.UseAuthorization();

app.MapRazorPages();
app.MapSiteApi();
app.MapDivisionApi();
app.MapConnect();
app.MapDiscordBot();
app.MapFriends();

// Language switch: remembered for a year in a cookie, then back to the page.
app.MapGet("/lang/{code}", (string code, string? r, HttpContext ctx) =>
{
    ctx.Response.Cookies.Append(Lang.Cookie, code == "ru" ? "ru" : "en", Preference(ctx));
    return Results.LocalRedirect(r is { Length: > 0 } && r.StartsWith('/') && !r.StartsWith("//") ? r : "/");
});

static CookieOptions Preference(HttpContext ctx) => new()
{
    MaxAge = TimeSpan.FromDays(365), HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, IsEssential = true,
};

app.Run();

/// <summary>Form fields left empty bind as "" rather than null (string properties are never null).</summary>
sealed class KeepEmptyStrings : Microsoft.AspNetCore.Mvc.ModelBinding.Metadata.IDisplayMetadataProvider
{
    public void CreateDisplayMetadata(Microsoft.AspNetCore.Mvc.ModelBinding.Metadata.DisplayMetadataProviderContext context)
    {
        if (context.Key.ModelType == typeof(string)) context.DisplayMetadata.ConvertEmptyStringToNull = false;
    }
}

/// <summary>Entry point, visible to integration tests.</summary>
public partial class Program;
