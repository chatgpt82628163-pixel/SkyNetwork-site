using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SkyNetwork.Site.Data;
using Database = SkyNetwork.Site.Data.Database;

namespace SkyNetwork.Site.Tests;

public class ReleasesTests
{
    // Minimal fake Windows EXE: MZ header + padding.
    private static byte[] FakeExe(int size = 128)
    {
        var b = new byte[size];
        b[0] = 0x4D; b[1] = 0x5A; // MZ
        return b;
    }

    private static MultipartFormDataContent UploadForm(string product, string version, byte[] content, string fileName = "setup.exe")
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(product), "Product");
        form.Add(new StringContent(version), "Version");
        form.Add(new StringContent("Test notes"), "Notes");
        form.Add(new StringContent("Test notes EN"), "NotesEn");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "Installer", fileName);
        return form;
    }

    private async Task<long> LoginAdminAsync(SiteFactory site, HttpClient browser)
    {
        long cid = site.Member("Admin User", Ratings.ADM);
        await browser.LoginAsync(cid);
        return cid;
    }

    [Fact]
    public async Task AdminCanUploadAndRelease()
    {
        using var site = new SiteFactory();
        var browser = site.Browser();
        await LoginAdminAsync(site, browser);

        // Upload
        var form = UploadForm("skypilot", "0.1.0", FakeExe());
        // First get the antiforgery token
        var html = await (await browser.GetAsync("/staff/releases")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(token.Success, "No antiforgery token on /staff/releases");
        form.Add(new StringContent(WebUtility.HtmlDecode(token.Groups[1].Value)), "__RequestVerificationToken");

        var r = await browser.PostAsync("/staff/releases?handler=Upload", form);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"Upload failed: {(int)r.StatusCode}\n{body[..Math.Min(3000, body.Length)]}");

        // Check DB
        var service = site.Get<ReleaseService>();
        var rel = service.LatestPublished("skypilot");
        Assert.Null(rel); // not published yet

        var all = service.ForProduct("skypilot");
        Assert.Single(all);
        Assert.Equal("0.1.0", all[0].Version);
        Assert.Equal("skypilot", all[0].Product);
        Assert.False(all[0].Published);

        // Check SHA256 and size stored
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(FakeExe())).ToLowerInvariant();
        Assert.Equal(expected, all[0].Sha256);
        Assert.Equal(FakeExe().Length, all[0].Size);

        // Publish
        var publishForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Id"] = all[0].Id.ToString(),
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
        });
        // Refresh token
        html = await (await browser.GetAsync("/staff/releases")).Content.ReadAsStringAsync();
        token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        publishForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Id"] = all[0].Id.ToString(),
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
        });
        var pub = await browser.PostAsync("/staff/releases?handler=Publish", publishForm);
        Assert.True(pub.IsSuccessStatusCode);

        rel = service.LatestPublished("skypilot");
        Assert.NotNull(rel);
        Assert.True(rel!.Published);

        // Unpublish
        html = await (await browser.GetAsync("/staff/releases")).Content.ReadAsStringAsync();
        token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        var unpub = await browser.PostAsync("/staff/releases?handler=Unpublish",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = all[0].Id.ToString(),
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
            }));
        Assert.True(unpub.IsSuccessStatusCode);
        rel = service.LatestPublished("skypilot");
        Assert.Null(rel);
    }

    [Fact]
    public async Task NonAdminCannotAccessReleasesPage()
    {
        using var site = new SiteFactory();
        var browser = site.Browser();
        // Not logged in at all
        var r = await browser.GetAsync("/staff/releases");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        // Unauthenticated POST upload is also refused
        var form = UploadForm("skypilot", "0.1.0", FakeExe());
        var rPost = await browser.PostAsync("/staff/releases?handler=Upload", form);
        Assert.True(rPost.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        // Logged-in staff without Releases permission also cannot access the page
        var browser2 = site.Browser();
        long supCid = site.Member("Supervisor User", Ratings.SUP);
        await browser2.LoginAsync(supCid);
        var r2 = await browser2.GetAsync("/staff/releases");
        Assert.Equal(HttpStatusCode.NotFound, r2.StatusCode);

        // Supervisor POST upload is also refused
        var form2 = UploadForm("skypilot", "0.1.0", FakeExe());
        var rPost2 = await browser2.PostAsync("/staff/releases?handler=Upload", form2);
        Assert.True(rPost2.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UploadRefusesNonMzFile()
    {
        using var site = new SiteFactory();
        var browser = site.Browser();
        await LoginAdminAsync(site, browser);

        var html = await (await browser.GetAsync("/staff/releases")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

        var notExe = Encoding.UTF8.GetBytes("This is not an EXE file at all");
        var form = UploadForm("skypilot", "0.2.0", notExe);
        form.Add(new StringContent(WebUtility.HtmlDecode(token.Groups[1].Value)), "__RequestVerificationToken");

        var r = await browser.PostAsync("/staff/releases?handler=Upload", form);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode);
        Assert.Contains("MZ", body);

        // Nothing stored
        var service = site.Get<ReleaseService>();
        Assert.Empty(service.ForProduct("skypilot"));
    }

    [Fact]
    public async Task DownloadServesLatestPublishedAndCountsDownloads()
    {
        using var site = new SiteFactory();
        var browser = site.Browser();
        await LoginAdminAsync(site, browser);

        // Upload and publish
        var service = site.Get<ReleaseService>();
        var fakeExe = FakeExe(256);
        System.IO.Directory.CreateDirectory(service.Directory);
        var r = new Release
        {
            Product = "skypilot", Version = "1.0.0", FileName = "skypilot-1.0.0.exe",
            Size = fakeExe.Length, Sha256 = "aabbcc",
            UploadedBy = 1, CreatedAt = Database.Now(),
        };
        service.Insert(r);
        var inserted = service.ForProduct("skypilot")[0];
        File.WriteAllBytes(service.FilePath("skypilot-1.0.0.exe"), fakeExe);
        service.SetPublished(inserted.Id, true);

        // Download the latest
        var dl = await browser.GetAsync("/download/skypilot");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Contains("attachment", dl.Content.Headers.ContentDisposition?.ToString() ?? "");
        Assert.Contains("SkyPilot-Setup-1.0.0.exe", dl.Content.Headers.ContentDisposition?.ToString() ?? "");
        var content = await dl.Content.ReadAsByteArrayAsync();
        Assert.Equal(fakeExe, content);

        // Download count incremented
        var updated = service.ForProduct("skypilot")[0];
        Assert.Equal(1, updated.Downloads);

        // Also by version
        var dl2 = await browser.GetAsync("/download/skypilot/1.0.0");
        Assert.Equal(HttpStatusCode.OK, dl2.StatusCode);
    }

    [Fact]
    public async Task DownloadReturns404WhenNotPublished()
    {
        using var site = new SiteFactory();
        var browser = site.Browser();
        var r = await browser.GetAsync("/download/skypilot");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task ApiReturnsGitHubShape()
    {
        using var site = new SiteFactory();
        var browser = site.Browser();
        await LoginAdminAsync(site, browser);

        var service = site.Get<ReleaseService>();
        var fakeExe = FakeExe(512);
        System.IO.Directory.CreateDirectory(service.Directory);
        File.WriteAllBytes(service.FilePath("skypilot-0.3.0.exe"), fakeExe);
        var rel = new Release
        {
            Product = "skypilot", Version = "0.3.0", FileName = "skypilot-0.3.0.exe",
            Size = fakeExe.Length, Sha256 = "deadbeef01",
            Notes = "release notes", NotesEn = "release notes en",
            UploadedBy = 1, CreatedAt = Database.Now(),
        };
        service.Insert(rel);
        var inserted = service.ForProduct("skypilot")[0];
        service.SetPublished(inserted.Id, true);

        var resp = await browser.GetAsync("/api/v1/releases/skypilot/latest");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("0.3.0", json.GetProperty("tag_name").GetString());
        Assert.False(json.GetProperty("draft").GetBoolean());
        Assert.False(json.GetProperty("prerelease").GetBoolean());
        Assert.True(json.TryGetProperty("published_at", out _));
        Assert.Equal("release notes en", json.GetProperty("body").GetString());

        var assets = json.GetProperty("assets");
        Assert.Equal(1, assets.GetArrayLength());
        var asset = assets[0];
        Assert.Equal("SkyPilot-Setup-0.3.0.exe", asset.GetProperty("name").GetString());
        Assert.Contains("/download/skypilot/0.3.0", asset.GetProperty("browser_download_url").GetString());
        Assert.Equal(fakeExe.Length, asset.GetProperty("size").GetInt64());
        Assert.StartsWith("sha256:", asset.GetProperty("digest").GetString());
        Assert.Equal("sha256:deadbeef01", asset.GetProperty("digest").GetString());
    }

    [Fact]
    public async Task UploadRefusesDuplicateVersion()
    {
        using var site = new SiteFactory();
        var browser = site.Browser();
        await LoginAdminAsync(site, browser);

        var html = await (await browser.GetAsync("/staff/releases")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        var afToken = System.Net.WebUtility.HtmlDecode(token.Groups[1].Value);

        // First upload
        var form1 = UploadForm("skypilot", "0.1.0", FakeExe());
        form1.Add(new StringContent(afToken), "__RequestVerificationToken");
        var r1 = await browser.PostAsync("/staff/releases?handler=Upload", form1);
        Assert.True(r1.IsSuccessStatusCode);

        // Refresh token for second request
        html = await (await browser.GetAsync("/staff/releases")).Content.ReadAsStringAsync();
        token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        afToken = System.Net.WebUtility.HtmlDecode(token.Groups[1].Value);

        // Second upload of same version
        var form2 = UploadForm("skypilot", "0.1.0", FakeExe());
        form2.Add(new StringContent(afToken), "__RequestVerificationToken");
        var r2 = await browser.PostAsync("/staff/releases?handler=Upload", form2);
        var body2 = await r2.Content.ReadAsStringAsync();
        Assert.True(r2.IsSuccessStatusCode);
        Assert.Contains("already exists", body2);

        // Only one record in the database
        var service = site.Get<ReleaseService>();
        Assert.Single(service.ForProduct("skypilot"));
    }

    [Fact]
    public async Task ApiReturns404WhenNothingPublished()
    {
        using var site = new SiteFactory();
        var browser = site.Browser();
        var r = await browser.GetAsync("/api/v1/releases/skypilot/latest");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task SoftwarePageShowsVersion()
    {
        using var site = new SiteFactory();
        var browser = site.Browser();

        // Without a release on the site the button leads to the GitHub releases
        var html = await browser.HtmlAsync("/docs/software");
        Assert.Contains("github.com/chatgpt82628163-pixel/SkyPilot/releases/latest", html);

        // Publish one
        await LoginAdminAsync(site, browser);
        var service = site.Get<ReleaseService>();
        System.IO.Directory.CreateDirectory(service.Directory);
        File.WriteAllBytes(service.FilePath("skypilot-2.0.0.exe"), FakeExe());
        var rel = new Release
        {
            Product = "skypilot", Version = "2.0.0", FileName = "skypilot-2.0.0.exe",
            Size = FakeExe().Length, Sha256 = "aabb",
            UploadedBy = 1, CreatedAt = Database.Now(),
        };
        service.Insert(rel);
        var inserted = service.ForProduct("skypilot")[0];
        service.SetPublished(inserted.Id, true);

        html = await browser.HtmlAsync("/docs/software");
        Assert.Contains("2.0.0", html);
        Assert.Contains("/download/skypilot", html);
    }
}
