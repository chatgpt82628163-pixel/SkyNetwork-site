using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Tests;

public class AirportLayoutTests
{
    // Minimal valid Overpass response for one runway.
    private const string OverpassOk = """
        {"elements":[
          {"type":"way","id":1,"tags":{"aeroway":"runway","ref":"09/27"},
           "geometry":[{"lat":55.0,"lon":37.0},{"lat":55.01,"lon":37.01}]}
        ]}
        """;

    private static AirportLayout BuildLayout(string cacheDir, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var options = Options.Create(new SiteOptions { Database = Path.Combine(cacheDir, "sky.db") });
        var factory = new FakeHttpClientFactory(handler);
        var env = new FakeWebHostEnvironment(cacheDir);
        return new AirportLayout(factory, options, env, NullLogger<AirportLayout>.Instance);
    }

    [Fact]
    public async Task FallsBackToNextServerWhenFirstFails()
    {
        string cache = TempDir();
        try
        {
            int calls = 0;
            var layout = BuildLayout(cache, req =>
            {
                calls++;
                // First server returns 504; second succeeds.
                return calls == 1
                    ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OverpassOk) };
            });

            var result = await layout.GetAsync("UUEE", CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(calls >= 2, $"Expected at least 2 server attempts, got {calls}");
            Assert.Contains("runways", result);
        }
        finally { Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task BackoffAfterAllServersFail()
    {
        string cache = TempDir();
        try
        {
            int calls = 0;
            var layout = BuildLayout(cache, _ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });

            // First call: tries all servers, fails, records backoff.
            var r1 = await layout.GetAsync("UUDD", CancellationToken.None);
            Assert.Null(r1);
            int callsAfterFirst = calls;

            // Second call immediately after: backoff prevents any new HTTP requests.
            var r2 = await layout.GetAsync("UUDD", CancellationToken.None);
            Assert.Null(r2);
            Assert.Equal(callsAfterFirst, calls);
        }
        finally { Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task StaleCacheIsServedWhileRefreshing()
    {
        string cache = TempDir();
        try
        {
            // Write a stale cache file (older than MaxAge = 30 days).
            Directory.CreateDirectory(Path.Combine(cache, "airports"));
            string staleJson = """{"icao":"UWWW","runways":[],"taxiways":[],"areas":[],"stands":[]}""";
            string path = Path.Combine(cache, "airports", "UWWW.json");
            await File.WriteAllTextAsync(path, staleJson);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-35));

            bool httpCalled = false;
            var layout = BuildLayout(cache, _ =>
            {
                httpCalled = true;
                // Background refresh fails; stale should still be returned.
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });

            var result = await layout.GetAsync("UWWW", CancellationToken.None);

            // Must return stale data immediately, not null.
            Assert.NotNull(result);
            Assert.Equal(staleJson, result);
            // Background refresh was triggered.
            Assert.True(httpCalled);
        }
        finally { Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task IsCacheFreshReturnsTrueOnlyForRecentFile()
    {
        string cache = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cache, "airports"));
            string path = Path.Combine(cache, "airports", "USSS.json");
            await File.WriteAllTextAsync(path, "{}");

            var layout = BuildLayout(cache, _ => new HttpResponseMessage(HttpStatusCode.OK));

            // File just written: fresh.
            Assert.True(layout.IsCacheFresh("USSS"));
            // No file yet.
            Assert.False(layout.IsCacheFresh("UUEE"));
            // Stale file.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-31));
            Assert.False(layout.IsCacheFresh("USSS"));
        }
        finally { Directory.Delete(cache, true); }
    }

    [Fact]
    public void WarmupCollectsAirportsFromAllSources()
    {
        using var site = new SiteFactory(new Dictionary<string, string>
        {
            ["Site:AirportWarmup"] = "UUEE,UUDD,UUWW",
        });
        // Warmup is a HostedService; get it from DI to test CollectAirports().
        // Since DataFeedUrl is empty, feed is not running but still injectable.
        var warmup = site.Get<AirportLayoutWarmup>();
        var airports = warmup.CollectAirports();

        // Should include the configured list.
        Assert.Contains("UUEE", airports);
        Assert.Contains("UUDD", airports);
        Assert.Contains("UUWW", airports);
        // All entries are valid ICAO codes (3-4 uppercase alphanumeric).
        Assert.All(airports, code => Assert.Matches("^[A-Z0-9]{3,4}$", code));
    }

    [Fact]
    public async Task WarmupSkipsFreshAirports()
    {
        string cache = TempDir();
        try
        {
            // Write a fresh cache file.
            Directory.CreateDirectory(Path.Combine(cache, "airports"));
            string path = Path.Combine(cache, "airports", "UUEE.json");
            await File.WriteAllTextAsync(path, "{}");
            // Fresh = written now.

            int httpCalls = 0;
            var layout = BuildLayout(cache, _ =>
            {
                httpCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OverpassOk) };
            });

            // Manually simulate what warmup does: skip fresh ones.
            string[] toWarm = ["UUEE", "UUDD"];
            foreach (var icao in toWarm)
            {
                if (layout.IsCacheFresh(icao)) continue;
                await layout.GetAsync(icao, CancellationToken.None);
            }

            // UUEE is fresh, so only UUDD should be fetched.
            Assert.Equal(1, httpCalls);
        }
        finally { Directory.Delete(cache, true); }
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"skynet-apt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Minimal IHttpClientFactory that routes all requests through a single handler.</summary>
    private sealed class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FakeHandler(handler));

        private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(handler(request));
        }
    }

    private sealed class FakeWebHostEnvironment(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }
}
