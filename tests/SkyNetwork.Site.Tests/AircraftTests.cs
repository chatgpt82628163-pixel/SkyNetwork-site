using System.Net;
using System.Text.Json;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Tests;

/// <summary>
/// Aircraft performance profile import, fuel flow model, type mapping, and API.
/// No network access: all data comes from the bundled profiles.json.
/// </summary>
public class AircraftTests
{
    // ── profile loading ───────────────────────────────────────────────────────

    [Fact]
    public void BundledProfilesLoadWithoutError()
    {
        // AircraftService is registered in DI; it loads profiles.json from AppContext.BaseDirectory.
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        var all = svc.All();
        Assert.True(all.Count >= 30, $"Expected ≥30 profiles, got {all.Count}");
    }

    [Theory]
    [InlineData("A20N")] [InlineData("A21N")] [InlineData("A318")] [InlineData("A319")]
    [InlineData("A320")] [InlineData("A321")] [InlineData("A332")] [InlineData("A333")]
    [InlineData("A343")] [InlineData("A359")] [InlineData("A388")]
    [InlineData("B737")] [InlineData("B738")] [InlineData("B739")]
    [InlineData("B38M")] [InlineData("B39M")] [InlineData("B744")] [InlineData("B748")]
    [InlineData("B752")] [InlineData("B763")] [InlineData("B772")] [InlineData("B77W")]
    [InlineData("B788")] [InlineData("B789")]
    [InlineData("E170")] [InlineData("E190")] [InlineData("E195")]
    [InlineData("CRJ9")] [InlineData("AT72")] [InlineData("DH8D")] [InlineData("SU95")]
    [InlineData("C172")] [InlineData("PA28")] [InlineData("TBM9")]
    public void RequiredTypeIsPresent(string icao)
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        var p = svc.Get(icao);
        Assert.NotNull(p);
        Assert.Equal(icao, p.Icao);
        Assert.True(p.MtowKg > 0, $"{icao}: MTOW must be positive");
        Assert.True(p.OewKg > 0, $"{icao}: OEW must be positive");
        Assert.True(p.MtowKg > p.OewKg, $"{icao}: MTOW must exceed OEW");
        Assert.True(p.MfcKg > 0, $"{icao}: MFC must be positive");
        Assert.True(p.NomCruiseFfKgH > 0, $"{icao}: nominal cruise FF must be positive");
    }

    [Fact]
    public void MappedTypesSU95IsMarked()
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        var su95 = svc.Get("SU95");
        Assert.NotNull(su95);
        Assert.True(su95.IsMapped, "SU95 should be flagged as mapped");
        Assert.Contains("E190", su95.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GaTypesHaveSimpleModel()
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        foreach (var icao in new[] { "C172", "PA28", "TBM9" })
        {
            var p = svc.Get(icao)!;
            Assert.Equal("ga", p.Category);
            Assert.True(p.MtowKg < 5000, $"{icao}: GA MTOW should be < 5000 kg");
        }
    }

    // ── fuel flow sanity ──────────────────────────────────────────────────────

    [Fact]
    public void A320CruiseFuelFlow_FL350_IsInRange()
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        var p = svc.Get("A320")!;
        // Reference: mass = 0.80 * MTOW at FL350 → should return nominal value ~2.4 t/h
        double ff = AircraftService.FuelFlowKgH(p, massTakeoffKg: 0.80 * p.MtowKg,
            altitudeFt: 35000, machOrTas: 0.78, phase: "cruise");
        Assert.True(ff >= 2200 && ff <= 2800,
            $"A320 cruise FF at FL350 should be 2200–2800 kg/h, got {ff:F0}");
    }

    [Fact]
    public void B738CruiseFuelFlow_FL350_IsInRange()
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        var p = svc.Get("B738")!;
        double ff = AircraftService.FuelFlowKgH(p, massTakeoffKg: 0.80 * p.MtowKg,
            altitudeFt: 35000, machOrTas: 0.785, phase: "cruise");
        Assert.True(ff >= 2200 && ff <= 2800,
            $"B738 cruise FF at FL350 should be 2200–2800 kg/h, got {ff:F0}");
    }

    [Fact]
    public void B77WCruiseFuelFlow_FL350_IsInRange()
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        var p = svc.Get("B77W")!;
        double ff = AircraftService.FuelFlowKgH(p, massTakeoffKg: 0.80 * p.MtowKg,
            altitudeFt: 35000, machOrTas: 0.84, phase: "cruise");
        Assert.True(ff >= 6500 && ff <= 8000,
            $"B77W cruise FF at FL350 should be 6500–8000 kg/h, got {ff:F0}");
    }

    [Fact]
    public void FuelFlowScalesWithMass()
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        var p = svc.Get("A320")!;
        double ffLight = AircraftService.FuelFlowKgH(p, 0.65 * p.MtowKg, 35000, 0.78, "cruise");
        double ffHeavy = AircraftService.FuelFlowKgH(p, 0.95 * p.MtowKg, 35000, 0.78, "cruise");
        Assert.True(ffHeavy > ffLight, "Heavier aircraft should burn more fuel");
    }

    [Fact]
    public void FuelFlowClimbHigherThanCruise()
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        var p = svc.Get("B738")!;
        double ffCruise = AircraftService.FuelFlowKgH(p, 0.80 * p.MtowKg, 35000, 0.785, "cruise");
        double ffClimb = AircraftService.FuelFlowKgH(p, 0.90 * p.MtowKg, 20000, 0.785, "climb");
        Assert.True(ffClimb > ffCruise, "Climb FF should exceed cruise FF");
    }

    [Fact]
    public void FuelFlowDescentLowerThanCruise()
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();
        var p = svc.Get("A320")!;
        double ffCruise = AircraftService.FuelFlowKgH(p, 0.80 * p.MtowKg, 35000, 0.78, "cruise");
        double ffDescent = AircraftService.FuelFlowKgH(p, 0.70 * p.MtowKg, 20000, 0.78, "descent");
        Assert.True(ffDescent < ffCruise, "Descent FF should be lower than cruise FF");
    }

    [Fact]
    public void AtmosphereModel_DeltaAt35000_IsCorrect()
    {
        double delta = AircraftService.DeltaAt(35000);
        // ISA: at FL350, pressure ≈ 23842 Pa → delta ≈ 0.2353
        Assert.True(delta >= 0.22 && delta <= 0.25, $"delta at FL350 should be ≈0.235, got {delta:F4}");
    }

    // ── staff override ────────────────────────────────────────────────────────

    [Fact]
    public void StaffCanOverrideAndResetWeights()
    {
        using var site = new SiteFactory();
        var svc = site.Get<AircraftService>();

        // Override MTOW of A320
        svc.SaveOverride(new AircraftOverride { Icao = "A320", MtowKg = 80000 });
        var p = svc.Get("A320")!;
        Assert.True(p.IsOverridden);
        Assert.Equal(80000, p.MtowKg);

        // Reset
        svc.DeleteOverride("A320");
        p = svc.Get("A320")!;
        Assert.False(p.IsOverridden);
        Assert.Equal(78000, p.MtowKg); // original
    }

    // ── API endpoints ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiListReturnsAllTypes()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        var resp = await c.GetAsync("/api/v1/aircraft");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.True(arr.Count >= 30);
        // Each entry has code, name, category
        var first = arr[0];
        Assert.True(first.TryGetProperty("code", out _));
        Assert.True(first.TryGetProperty("name", out _));
        Assert.True(first.TryGetProperty("category", out _));
    }

    [Fact]
    public async Task ApiGetA320ReturnsFullProfile()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        var resp = await c.GetAsync("/api/v1/aircraft/A320");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("A320", root.GetProperty("code").GetString());
        Assert.True(root.TryGetProperty("weights", out var w));
        Assert.True(w.GetProperty("mtow_kg").GetDouble() > 0);
        Assert.True(root.TryGetProperty("fuel_model", out _));
    }

    [Fact]
    public async Task ApiGet_CaseInsensitive()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        var r1 = await c.GetAsync("/api/v1/aircraft/a320");
        var r2 = await c.GetAsync("/api/v1/aircraft/A320");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    [Fact]
    public async Task ApiGetUnknownType_Returns404()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        var resp = await c.GetAsync("/api/v1/aircraft/ZZZZ");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ApiMappedTypeFlagged()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        var resp = await c.GetAsync("/api/v1/aircraft/SU95");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("is_mapped").GetBoolean());
    }
}
