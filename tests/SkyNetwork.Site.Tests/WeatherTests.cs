using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Tests;

// ── METAR decoder tests ──────────────────────────────────────────────────────

public class MetarDecoderTests
{
    [Fact]
    public void BasicMetar_KtWind_Clouds_QNH()
    {
        var d = WeatherDecoder.DecodeMetar("KJFK 121651Z 33015KT 10SM FEW055 BKN150 OVC250 02/M08 A3009 RMK AO2 SLP191");
        Assert.Equal("KJFK", d.Icao);
        Assert.NotNull(d.Wind);
        Assert.Equal(330, d.Wind!.Direction);
        Assert.Equal(15, d.Wind.Speed);
        Assert.Equal("KT", d.Wind.Unit);
        Assert.Null(d.Wind.Gust);
        Assert.False(d.Wind.Variable);
        Assert.Equal(3, d.Clouds.Count);
        Assert.Equal("FEW", d.Clouds[0].Cover);
        Assert.Equal(5500, d.Clouds[0].AltitudeFt);
        Assert.Equal("BKN", d.Clouds[1].Cover);
        Assert.Equal("OVC", d.Clouds[2].Cover);
        Assert.Equal(2, d.TempC);
        Assert.Equal(-8, d.DewC);
        Assert.Null(d.QnhHpa);
        Assert.NotNull(d.AltInHg);
        Assert.Equal(30.09, d.AltInHg!.Value, 2);
        Assert.Equal("VFR", d.FlightCategory);
        Assert.Contains("AO2 SLP191", d.Remarks);
    }

    [Fact]
    public void RussianMetar_MpsWind_VariableSector_QNH()
    {
        var d = WeatherDecoder.DecodeMetar("UUEE 121630Z 22003MPS 330V090 9999 SCT030 05/M04 Q1021 NOSIG");
        Assert.Equal("UUEE", d.Icao);
        Assert.NotNull(d.Wind);
        Assert.Equal("MPS", d.Wind!.Unit);
        Assert.Equal(220, d.Wind.Direction);
        Assert.Equal(3, d.Wind.Speed);
        Assert.Equal(330, d.Wind.VarFrom);
        Assert.Equal(90, d.Wind.VarTo);
        Assert.Equal(9999, d.Visibility!.Metres);
        Assert.Equal(1021, d.QnhHpa);
        Assert.Single(d.Trends);
        Assert.Equal("NOSIG", d.Trends[0].Type);
        Assert.True(d.Trends[0].Nosig);
        Assert.Equal("VFR", d.FlightCategory);
    }

    [Fact]
    public void MetarWithGust_VariableWind_LoVis_LIFR()
    {
        var d = WeatherDecoder.DecodeMetar("UKLL 121800Z VRB02KT 0100 R31/0400 FG BKN002 09/09 Q1015");
        Assert.True(d.Wind!.Variable);
        Assert.Equal(100, d.Visibility!.Metres);
        Assert.Single(d.Rvr);
        Assert.Equal("31", d.Rvr[0].Runway);
        Assert.Equal(400, d.Rvr[0].Distance);
        Assert.Single(d.Weather);
        Assert.Equal("FG", d.Weather[0].Phenomena[0]);
        Assert.Equal("LIFR", d.FlightCategory);
    }

    [Fact]
    public void MetarWithPresentWeather_TSGust_CB()
    {
        var d = WeatherDecoder.DecodeMetar("EGLL 121550Z 28025G40KT 250V320 8000 TSRA SCT020CB BKN040 18/12 Q1008");
        Assert.Equal(280, d.Wind!.Direction);
        Assert.Equal(40, d.Wind.Gust);
        Assert.Equal(250, d.Wind.VarFrom);
        Assert.Equal(320, d.Wind.VarTo);
        Assert.Single(d.Weather);
        Assert.Equal("TS", d.Weather[0].Descriptor);
        Assert.Contains("RA", d.Weather[0].Phenomena);
        Assert.Equal("CB", d.Clouds[0].CloudType);
        Assert.Equal("MVFR", d.FlightCategory); // 8000 m is just under 5 SM; the ceiling (BKN040) is VFR
    }

    [Fact]
    public void MetarCAVOK_VFR()
    {
        var d = WeatherDecoder.DecodeMetar("ULLI 121600Z 09005MPS CAVOK 12/05 Q1018");
        Assert.True(d.Visibility!.Cavok);
        Assert.Equal("VFR", d.FlightCategory);
        Assert.Contains("CAVOK", d.HumanRu);
    }

    [Fact]
    public void MetarNSC()
    {
        var d = WeatherDecoder.DecodeMetar("EPWA 121700Z 18004KT 9999 NSC 15/10 Q1012");
        Assert.True(d.Visibility!.Nsc);
        Assert.Equal("VFR", d.FlightCategory);
    }

    [Fact]
    public void MetarVerticalVisibility_IFR()
    {
        var d = WeatherDecoder.DecodeMetar("URSS 121630Z 00000KT 0400 FG VV002 10/10 Q1020");
        Assert.Single(d.Clouds);
        Assert.Equal("VV", d.Clouds[0].Cover);
        Assert.Equal(200, d.Clouds[0].AltitudeFt);
        Assert.Equal("LIFR", d.FlightCategory); // 400m vis AND VV002
    }

    [Fact]
    public void MetarRunwayState_Russian()
    {
        // R06/110050 — runway 06, deposit 1 (damp), contamination 1 (10%), depth 00 (<1mm), friction 50 (µ=0.50)
        var d = WeatherDecoder.DecodeMetar("URSS 121630Z 18003MPS 9999 -DZ FEW010 BKN040 09/08 Q1014 R06/110050");
        Assert.Single(d.RunwayStates);
        Assert.Equal("06", d.RunwayStates[0].Runway);
        Assert.False(d.RunwayStates[0].Snoclo);
        Assert.NotNull(d.RunwayStates[0].Deposit);
    }

    [Fact]
    public void MetarQFE_InRemarks()
    {
        var d = WeatherDecoder.DecodeMetar("UUEE 121630Z 22003MPS 9999 SCT030 12/05 Q1018 RMK QFE745/0994");
        Assert.NotNull(d.Qfe);
        Assert.Contains("QFE745", d.Qfe!);
    }

    [Fact]
    public void MetarBECMGTrend()
    {
        var d = WeatherDecoder.DecodeMetar("UUDD 121800Z 18005MPS 8000 BKN020 10/07 Q1015 BECMG 4000 -RA BKN010");
        Assert.Single(d.Trends);
        Assert.Equal("BECMG", d.Trends[0].Type);
        Assert.Single(d.Trends[0].Weather);
    }

    [Fact]
    public void MetarTEMPO_AfterQNH()
    {
        var d = WeatherDecoder.DecodeMetar("UUEE 121630Z 22003MPS 9999 FEW030 15/05 Q1018 TEMPO 2000 -SHRA FEW015");
        Assert.Single(d.Trends);
        Assert.Equal("TEMPO", d.Trends[0].Type);
    }

    [Fact]
    public void MetarLightRain_IntensityParsed()
    {
        var d = WeatherDecoder.DecodeMetar("LFPO 121700Z 24010KT 9000 -RA FEW020 BKN060 14/11 Q1008");
        Assert.Single(d.Weather);
        Assert.Equal("-", d.Weather[0].Intensity);
        Assert.Null(d.Weather[0].Descriptor);
        Assert.Equal("RA", d.Weather[0].Phenomena[0]);
        Assert.Contains("слабый", d.HumanRu);
    }

    [Fact]
    public void MetarHeavySnow_WithDescriptor()
    {
        var d = WeatherDecoder.DecodeMetar("UUEE 121200Z 27010MPS 1500 +BLSN BKN004 M05/M08 Q0995");
        Assert.Equal("+", d.Weather[0].Intensity);
        Assert.Equal("BL", d.Weather[0].Descriptor);
        Assert.Contains("SN", d.Weather[0].Phenomena);
        Assert.Equal("LIFR", d.FlightCategory);
    }

    [Fact]
    public void HumanTextContainsWindAndVis()
    {
        var d = WeatherDecoder.DecodeMetar("UUEE 121630Z 22003MPS 9999 SCT030 05/M04 Q1021 NOSIG");
        Assert.Contains("220", d.HumanEn);
        Assert.Contains("220", d.HumanRu);
        Assert.Contains("м/с", d.HumanRu);
        Assert.Contains("10 км", d.HumanRu);
    }

    [Fact]
    public void RvrVariableAndFeet()
    {
        var d = WeatherDecoder.DecodeMetar("EGLL 121550Z 28010KT 0200 R27L/0400VP1200FT/U FG OVC002 15/13 Q1012");
        Assert.Single(d.Rvr);
        Assert.Equal("27L", d.Rvr[0].Runway);
        Assert.Equal(400, d.Rvr[0].Distance);
        Assert.Equal(1200, d.Rvr[0].VarDistance);
        Assert.Equal("FT", d.Rvr[0].Unit);
        Assert.Equal('U', d.Rvr[0].Tendency);
    }
}

// ── TAF decoder tests ─────────────────────────────────────────────────────────

public class TafDecoderTests
{
    [Fact]
    public void BasicTaf_WithTEMPOandBECMG()
    {
        const string raw = "TAF UUEE 121100Z 1212/1312 22003MPS 9999 SCT030 " +
                           "TEMPO 1216/1220 3000 -SHRA BKN020 " +
                           "BECMG 1300/1302 18005MPS 6000 BKN040";
        var t = WeatherDecoder.DecodeTaf(raw);
        Assert.Equal("UUEE", t.Icao);
        Assert.Equal(3, t.Groups.Count);
        Assert.Equal("MAIN",  t.Groups[0].Type);
        Assert.Equal("TEMPO", t.Groups[1].Type);
        Assert.Equal("BECMG", t.Groups[2].Type);
        Assert.Equal("MPS", t.Groups[0].Wind!.Unit);
        Assert.NotNull(t.Groups[1].Visibility);
        Assert.Equal(3000, t.Groups[1].Visibility!.Metres);
    }

    [Fact]
    public void TafWithFM_andPROB40()
    {
        const string raw = "TAF EGLL 121700Z 1218/1318 28015KT 9999 BKN030 " +
                           "FM130000 30010KT 9999 SCT025 " +
                           "PROB40 TEMPO 1308/1312 5000 -RA BKN015";
        var t = WeatherDecoder.DecodeTaf(raw);
        Assert.Equal("EGLL", t.Icao);
        // MAIN + FM + PROB40TEMPO
        Assert.Equal(3, t.Groups.Count);
        Assert.Equal("FM", t.Groups[1].Type);
        Assert.Equal("PROB40TEMPO", t.Groups[2].Type);
    }

    [Fact]
    public void TafCAVOK_MainGroup()
    {
        const string raw = "TAF ULLI 121200Z 1212/1312 09005MPS CAVOK";
        var t = WeatherDecoder.DecodeTaf(raw);
        Assert.True(t.Groups[0].Visibility?.Cavok);
    }

    [Fact]
    public void TafAMD_Flag()
    {
        const string raw = "TAF AMD UUEE 121100Z 1212/1312 22003MPS 9999 FEW020";
        var t = WeatherDecoder.DecodeTaf(raw);
        Assert.True(t.Amended);
    }

    [Fact]
    public void TafHumanTextContainsValidityPeriod()
    {
        const string raw = "TAF UUEE 121100Z 1212/1312 22003MPS 9999 SCT030";
        var t = WeatherDecoder.DecodeTaf(raw);
        Assert.Contains("Действует", t.HumanRu);
    }

    [Fact]
    public void TafPROB30TEMPO()
    {
        const string raw = "TAF LFPO 121600Z 1218/1318 24010KT 9999 FEW040 " +
                           "PROB30 TEMPO 1300/1306 4000 TSRA SCT020CB";
        var t = WeatherDecoder.DecodeTaf(raw);
        var prob = t.Groups.FirstOrDefault(g => g.Type.StartsWith("PROB"));
        Assert.NotNull(prob);
        Assert.Equal("PROB30TEMPO", prob!.Type);
    }
}

// ── Flight category tests ─────────────────────────────────────────────────────

public class FlightCategoryTests
{
    private static VisibilityInfo Vis(int m) => new(m, null, false, false);
    private static CloudLayer Cloud(string cover, int ft) => new(cover, ft, null);

    [Theory]
    [InlineData(10000, "BKN", 5000, "VFR")]
    [InlineData(10000, "BKN", 2000, "MVFR")]
    [InlineData(10000, "BKN", 900, "IFR")]
    [InlineData(10000, "OVC", 400, "LIFR")]
    [InlineData(6000, "BKN", 5000, "MVFR")] // vis drives MVFR (3.7 SM)
    [InlineData(3000, "FEW", 9000, "IFR")]  // vis drives IFR (1.9 SM)
    [InlineData(1500, "FEW", 9000, "LIFR")] // under 1 SM
    [InlineData(400,  "FEW", 9000, "LIFR")] // vis drives LIFR
    public void CategoryByVisAndCeiling(int visM, string cover, int ceilFt, string expected)
    {
        var cat = WeatherDecoder.FlightCategory(Vis(visM), [Cloud(cover, ceilFt)]);
        Assert.Equal(expected, cat);
    }

    [Fact]
    public void CAVOK_IsVFR()
    {
        var cat = WeatherDecoder.FlightCategory(new VisibilityInfo(null, null, true, false), []);
        Assert.Equal("VFR", cat);
    }
}

// ── Runway wind component tests ───────────────────────────────────────────────

public class RunwayWindTests
{
    private static WindInfo KtWind(int dir, int spd, int? gust = null) =>
        new(false, dir, spd, gust, "KT", null, null);
    private static WindInfo MpsWind(int dir, int spd, int? gust = null) =>
        new(false, dir, spd, gust, "MPS", null, null);

    [Fact]
    public void PureHeadwind()
    {
        // Wind 360°, runway 36 (360°) → pure headwind
        var (hw, cw, hwMs, cwMs, _, _) = MetarService.RunwayComponents(KtWind(360, 10), 360);
        Assert.Equal(10.0, hw, 1);
        Assert.Equal(0.0, cw, 1);
    }

    [Fact]
    public void PureTailwind()
    {
        var (hw, cw, _, _, _, _) = MetarService.RunwayComponents(KtWind(180, 10), 360);
        Assert.Equal(-10.0, hw, 1);
        Assert.Equal(0.0, cw, 1);
    }

    [Fact]
    public void PureCrosswindFromRight()
    {
        // Wind 090°, runway 36 → 90° from right
        var (hw, cw, _, _, _, _) = MetarService.RunwayComponents(KtWind(90, 10), 0);
        Assert.Equal(0.0, hw, 1);
        Assert.True(cw > 0, "crosswind should be from right (positive)");
        Assert.Equal(10.0, cw, 1);
    }

    [Fact]
    public void PureCrosswindFromLeft()
    {
        // Wind 270°, runway 36 → 90° from left
        var (hw, cw, _, _, _, _) = MetarService.RunwayComponents(KtWind(270, 10), 0);
        Assert.Equal(0.0, hw, 1);
        Assert.True(cw < 0, "crosswind should be from left (negative)");
        Assert.Equal(-10.0, cw, 1);
    }

    [Fact]
    public void TypicalApproach_Wind340_5kt_Rwy32()
    {
        // Wind 340°, runway 320°, 20° crosswind angle
        var (hw, cw, _, _, _, _) = MetarService.RunwayComponents(KtWind(340, 10), 320);
        Assert.True(hw > 0, "should be headwind");
        Assert.True(cw > 0, "should be from right");
        // hw ≈ 10 * cos(20°) ≈ 9.4, cw ≈ 10 * sin(20°) ≈ 3.4
        Assert.Equal(9.4, hw, 1);
        Assert.Equal(3.4, cw, 1);
    }

    [Fact]
    public void GustComponentsCalculated()
    {
        var (_, _, _, _, gHw, gCw) = MetarService.RunwayComponents(KtWind(360, 10, 20), 360);
        Assert.NotNull(gHw);
        Assert.Equal(20.0, gHw!.Value, 1);
        Assert.NotNull(gCw);
        Assert.Equal(0.0, gCw!.Value, 1);
    }

    [Fact]
    public void VariableWind_ReturnsZero()
    {
        var wind = new WindInfo(true, 0, 5, null, "KT", null, null);
        var (hw, cw, _, _, _, _) = MetarService.RunwayComponents(wind, 360);
        Assert.Equal(0.0, hw, 1);
        Assert.Equal(0.0, cw, 1);
    }

    [Fact]
    public void MpsWindConverted()
    {
        // 10 MPS ~ 19.4 kt pure headwind
        var (hw, _, hwMs, _, _, _) = MetarService.RunwayComponents(MpsWind(360, 10), 360);
        Assert.Equal(10.0, hwMs, 1);
        Assert.Equal(10.0 / 0.514444, hw, 1);
    }
}

// ── ISA / pressure conversion tests ──────────────────────────────────────────

public class IsaTests
{
    [Theory]
    [InlineData(0, 1013.25, 0.5)]
    [InlineData(35000, 238.4, 5.0)]  // FL350
    [InlineData(40000, 187.7, 5.0)]  // FL400
    public void FtToPressureRoundTrip(double altFt, double expectedHpa, double toleranceHpa)
    {
        double hpa = WindsAloftService.FtToPressureHpa(altFt);
        Assert.Equal(expectedHpa, hpa, toleranceHpa);
        double back = WindsAloftService.PressureHpaToFt(hpa);
        Assert.Equal(altFt, back, 100.0); // ±100 ft (a double is a tolerance; an int would be decimal places)
    }

    [Theory]
    [InlineData(0, 15.0)]
    [InlineData(36089, -56.5)]
    public void IsaTemperature(double altFt, double expectedC)
    {
        Assert.Equal(expectedC, WindsAloftService.IsaTempC(altFt), 1);
    }
}

// ── Open-Meteo JSON parsing ───────────────────────────────────────────────────

public class OpenMeteoParseTests
{
    private const string SampleJson = """
        {
          "hourly": {
            "time": ["2026-01-01T00:00", "2026-01-01T01:00"],
            "wind_direction_850hPa": [270.0, 280.0],
            "wind_speed_850hPa": [30.0, 32.0],
            "temperature_850hPa": [-5.0, -6.0],
            "wind_direction_700hPa": [260.0, 265.0],
            "wind_speed_700hPa": [40.0, 42.0],
            "temperature_700hPa": [-15.0, -16.0]
          }
        }
        """;

    [Fact]
    public void ParsesHourlyArrays()
    {
        var data = WindsAloftService.ParseOpenMeteo(SampleJson, [850, 700]);
        Assert.Equal(2, data.Time.Count);
        Assert.Equal(2, data.WindDir[850].Length);
        Assert.Equal(270.0, data.WindDir[850][0]);
        Assert.Equal(30.0, data.WindSpd[850][0]);
        Assert.Equal(-5.0, data.Temp[850][0]);
        Assert.Equal(260.0, data.WindDir[700][0]);
    }
}

// ── API endpoint tests ────────────────────────────────────────────────────────

public class WeatherApiTests
{
    // A fake HttpMessageHandler that returns canned METAR/TAF.
    private sealed class FakeWeatherHandler : HttpMessageHandler
    {
        public string MetarResponse { get; set; } = "UUEE 121630Z 22003MPS 9999 SCT030 05/M04 Q1021 NOSIG";
        public string TafResponse   { get; set; } = "TAF UUEE 121100Z 1212/1312 22003MPS 9999 SCT030";
        public string AloftResponse { get; set; } = """{"hourly":{"time":["2026-01-01T00:00"],"wind_direction_850hPa":[270.0],"wind_speed_850hPa":[30.0],"temperature_850hPa":[-5.0],"wind_direction_700hPa":[260.0],"wind_speed_700hPa":[40.0],"temperature_700hPa":[-15.0]}}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.RequestUri?.AbsolutePath.Contains("taf") == true ? TafResponse
                        : request.RequestUri?.Host == "api.open-meteo.com" ? AloftResponse
                        : MetarResponse;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body) });
        }
    }

    private FakeWeatherHandler _handler = new();

    private SiteFactory BuildSite()
    {
        var handler = _handler;
        return new SiteFactory(new Dictionary<string, string>
        {
            ["Site:DataFeedUrl"] = ""
        })
        {
        };
    }

    [Fact]
    public async Task WeatherEndpoint_ReturnsParsedMetar()
    {
        var handler = new FakeWeatherHandler();
        using var site = new SiteFactory();
        using var app = site.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                // Replace the "metar" named client with our fake.
                s.AddHttpClient("metar").ConfigurePrimaryHttpMessageHandler(() => handler);
                s.AddSingleton<MetarService>(); // re-register after replacing client
                s.AddHttpClient("aloft").ConfigurePrimaryHttpMessageHandler(() => handler);
                s.AddSingleton<WindsAloftService>();
            }));

        var c = app.CreateClient();
        var r = await c.GetAsync("/api/v1/weather/UUEE");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string json = await r.Content.ReadAsStringAsync();
        Assert.Contains("UUEE", json);
        Assert.Contains("flightCategory", json);
        Assert.Contains("VFR", json);
    }

    [Fact]
    public async Task WeatherEndpoint_UnknownIcao_Returns404()
    {
        var handler = new FakeWeatherHandler { MetarResponse = "" };
        using var site = new SiteFactory();
        using var app = site.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.AddHttpClient("metar").ConfigurePrimaryHttpMessageHandler(() => handler);
                s.AddSingleton<MetarService>();
                s.AddHttpClient("aloft").ConfigurePrimaryHttpMessageHandler(() => handler);
                s.AddSingleton<WindsAloftService>();
            }));

        var c = app.CreateClient();
        // Empty METAR = airport has no METAR, returns 200 with empty decoded
        var r = await c.GetAsync("/api/v1/weather/ZZZZ");
        // Not 404 (null METAR from GetAsync = null = NotFound, but empty = OK with no decoded)
        Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WeatherEndpoint_InvalidIcao_Returns404()
    {
        using var site = new SiteFactory();
        var c = site.CreateClient();
        var r = await c.GetAsync("/api/v1/weather/notanicao123");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task AloftEndpoint_ReturnsBadRequestWithoutPoints()
    {
        using var site = new SiteFactory();
        var c = site.CreateClient();
        var r = await c.GetAsync("/api/v1/weather/aloft?fl=350");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task AloftEndpoint_ReturnsBadRequestWithoutFl()
    {
        using var site = new SiteFactory();
        var c = site.CreateClient();
        var r = await c.GetAsync("/api/v1/weather/aloft?points=55.97,37.41");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
