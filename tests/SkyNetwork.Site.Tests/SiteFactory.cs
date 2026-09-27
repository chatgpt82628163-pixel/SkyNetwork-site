using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Tests;

/// <summary>The site on a fresh temporary database, without the FSD data feed.</summary>
public sealed class SiteFactory(Dictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"skynet-site-{Guid.NewGuid():N}.db");
    public string UploadsPath { get; } = Path.Combine(Path.GetTempPath(), $"skynet-uploads-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Site:Database", DatabasePath);
        builder.UseSetting("Site:Uploads", UploadsPath);
        builder.UseSetting("Site:DataFeedUrl", "");
        builder.UseSetting("Site:AuthAttemptsPerMinute", "10000");
        // Tests fill in the registration form at once and register many members; SignupProtectionTests switch these on.
        builder.UseSetting("Site:SignupMinSeconds", "0");
        builder.UseSetting("Site:RegistrationsPerDayPerAddress", "10000");
        // The status page checks only when a test asks (StatusPageTests), and never GitHub.
        builder.UseSetting("Site:HealthCheckSeconds", "0");
        builder.UseSetting("Site:ReleaseRepos", "");
        if (settings != null)
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.UseEnvironment(Environment.GetEnvironmentVariable("SITE_TEST_ENV") ?? "Production");
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A member created directly in the database (as skynet-admin would).</summary>
    public long Member(string name = "Test Pilot", int rating = Ratings.OBS, string password = "password1")
    {
        var members = Get<MemberService>();
        long cid = members.Register(name, $"{Guid.NewGuid():N}@example.com", "", password);
        // SUP and ADM are staff ranks (the controller rating stays OBS).
        if (rating is Ratings.SUP or Ratings.ADM) members.SetStaffRank(0, cid, rating);
        else if (rating != Ratings.OBS) members.SetRating(0, cid, rating);
        return cid;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var f in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
        try { Directory.Delete(UploadsPath, true); } catch (IOException) { }
    }
}

public static class BrowserExtensions
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    /// <summary>GETs the page, then posts the form fields with the page's antiforgery token.</summary>
    public static async Task<HttpResponseMessage> SubmitAsync(this HttpClient c, string page, IDictionary<string, string> fields, string? action = null)
    {
        var html = await (await c.GetAsync(page)).Content.ReadAsStringAsync();
        var m = Token.Match(html);
        Assert.True(m.Success, $"no form on {page}");
        var data = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(m.Groups[1].Value) };
        return await c.PostAsync(action ?? page, new FormUrlEncodedContent(data));
    }

    /// <summary>
    /// Submits the form of a page handler the way a browser would: its own hidden fields (and
    /// antiforgery token) as rendered, plus the fields a user fills in.
    /// </summary>
    public static async Task<HttpResponseMessage> SubmitPageFormAsync(this HttpClient c, string page, string handler, IDictionary<string, string> filled)
    {
        var html = await (await c.GetAsync(page)).Content.ReadAsStringAsync();
        var form = Regex.Match(html, $@"<form[^>]*action=""[^""]*handler={handler}""[^>]*>(.*?)</form>", RegexOptions.Singleline);
        Assert.True(form.Success, $"no {handler} form on {page}");
        var data = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(form.Groups[1].Value, @"<input[^>]*type=""hidden""[^>]*>"))
        {
            var name = Regex.Match(input.Value, @"name=""([^""]+)""");
            var value = Regex.Match(input.Value, @"value=""([^""]*)""");
            if (name.Success) data[name.Groups[1].Value] = WebUtility.HtmlDecode(value.Success ? value.Groups[1].Value : "");
        }
        foreach (var (k, v) in filled) data[k] = v;
        var action = WebUtility.HtmlDecode(Regex.Match(form.Value, @"action=""([^""]+)""").Groups[1].Value);
        return await c.PostAsync(action, new FormUrlEncodedContent(data));
    }

    public static async Task LoginAsync(this HttpClient c, long cid, string password = "password1")
    {
        var r = await c.SubmitAsync("/login", new Dictionary<string, string> { ["Cid"] = cid.ToString(), ["Password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
    }

    public static async Task<string> HtmlAsync(this HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode}\n{body[..Math.Min(body.Length, 3000)]}");
        return body;
    }
}
