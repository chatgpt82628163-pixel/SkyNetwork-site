using System.Text;
using System.Text.RegularExpressions;

namespace SkyNetwork.Site.Services;

/// <summary>Parses METAR and TAF strings into structured records. No external dependencies.</summary>
public static partial class WeatherDecoder
{
    // ── Regexes ────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^(VRB|\d{3})(\d{2,3})(G(\d{2,3}))?(KT|MPS|KMH)$")]
    private static partial Regex WindRx();

    [GeneratedRegex(@"^\d{3}V\d{3}$")]
    private static partial Regex VarSectorRx();

    [GeneratedRegex(@"^(\d{4})$")]
    private static partial Regex VisMetresRx();

    [GeneratedRegex(@"^(M?(\d+)(\.\d+)?)SM$")]
    private static partial Regex VisSmRx();

    [GeneratedRegex(@"^R(\d{2}[LCR]?)/([PM]?)(\d{4})(V([PM]?)(\d{4}))?(FT)?([UDN]?)$")]
    private static partial Regex RvrRx();

    [GeneratedRegex(@"^(FEW|SCT|BKN|OVC|VV)(\d{3})(CB|TCU)?$")]
    private static partial Regex CloudRx();

    [GeneratedRegex(@"^(M?)(\d{1,2})/(M?)(\d{1,2}|/{2})$")]
    private static partial Regex TempRx();

    [GeneratedRegex(@"^Q(\d{4})$")]
    private static partial Regex QnhRx();

    [GeneratedRegex(@"^A(\d{4})$")]
    private static partial Regex AltRx();

    [GeneratedRegex(@"^R(\d{2}[LCR]?)/(\d{6})$")]
    private static partial Regex RwyStateRx();

    [GeneratedRegex(@"^R(\d{2}[LCR]?)/CLRD(\d{2})$")]
    private static partial Regex RwyClrdRx();

    [GeneratedRegex(@"\bQFE(\d+)(/(\d+))?\b")]
    private static partial Regex QfeRx();

    [GeneratedRegex(@"^(\d{2})(\d{2})(\d{2})Z$")]
    private static partial Regex ObsTimeRx();

    [GeneratedRegex(@"^TAF\b")]
    private static partial Regex TafHeaderRx();

    [GeneratedRegex(@"^FM(\d{2})(\d{2})(\d{2})$")]
    private static partial Regex FmRx();

    [GeneratedRegex(@"^(\d{4})/(\d{4})$")]
    private static partial Regex ValidityRx();

    // ── Weather code tables ────────────────────────────────────────────────────

    private static readonly HashSet<string> Descriptors = ["MI", "BC", "PR", "DR", "BL", "SH", "TS", "FZ"];

    private static readonly Dictionary<string, (string en, string ru)> WxCodes = new()
    {
        ["DZ"] = ("drizzle", "морось"),
        ["RA"] = ("rain", "дождь"),
        ["SN"] = ("snow", "снег"),
        ["SG"] = ("snow grains", "снежные зёрна"),
        ["IC"] = ("ice crystals", "ледяные кристаллы"),
        ["PL"] = ("ice pellets", "ледяная крупа"),
        ["GR"] = ("hail", "град"),
        ["GS"] = ("small hail", "снежная крупа"),
        ["UP"] = ("unknown precip.", "неизв. осадки"),
        ["BR"] = ("mist", "дымка"),
        ["FG"] = ("fog", "туман"),
        ["FU"] = ("smoke", "дым"),
        ["VA"] = ("volcanic ash", "вулканич. пепел"),
        ["DU"] = ("dust", "пыль"),
        ["SA"] = ("sand", "песок"),
        ["HZ"] = ("haze", "мгла"),
        ["PY"] = ("spray", "водяные брызги"),
        ["PO"] = ("dust whirls", "пыльные вихри"),
        ["SQ"] = ("squalls", "шквалы"),
        ["FC"] = ("funnel cloud", "воронкообразное облако"),
        ["SS"] = ("sandstorm", "пыльная буря"),
        ["DS"] = ("duststorm", "пылевая буря"),
    };

    private static readonly Dictionary<string, (string en, string ru)> DescText = new()
    {
        ["MI"] = ("shallow", "неглубокий"),
        ["BC"] = ("patches of", "клочья"),
        ["PR"] = ("partial", "частичный"),
        ["DR"] = ("drifting", "поземок"),
        ["BL"] = ("blowing", "метель"),
        ["SH"] = ("showers of", "ливни"),
        ["TS"] = ("thunderstorm with", "гроза с"),
        ["FZ"] = ("freezing", "переохлажд."),
    };

    private static readonly Dictionary<string, (string en, string ru)> CloudText = new()
    {
        ["FEW"] = ("few", "незначит."),
        ["SCT"] = ("scattered", "рассеянная"),
        ["BKN"] = ("broken", "значит."),
        ["OVC"] = ("overcast", "сплошная"),
        ["VV"] = ("vertical visibility", "вертикальная видимость"),
    };

    // ── METAR ─────────────────────────────────────────────────────────────────

    public static DecodedMetar DecodeMetar(string raw)
    {
        // Normalise: collapse spaces, strip leading/trailing.
        var tokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = 0;

        // Optional "METAR" or "SPECI" header.
        if (i < tokens.Length && tokens[i] is "METAR" or "SPECI") i++;

        string icao = i < tokens.Length ? tokens[i++].ToUpperInvariant() : "";
        bool cor = false, auto = false;
        DateTime obsTime = DateTime.UtcNow;

        // Date/time group ddhhmmZ.
        if (i < tokens.Length && ObsTimeRx().Match(tokens[i]) is { Success: true } tm)
        {
            var now = DateTime.UtcNow;
            int day = int.Parse(tm.Groups[1].Value), hour = int.Parse(tm.Groups[2].Value), min = int.Parse(tm.Groups[3].Value);
            obsTime = new DateTime(now.Year, now.Month, day, hour, min, 0, DateTimeKind.Utc);
            if (day > now.Day) obsTime = obsTime.AddMonths(-1);
            i++;
        }

        // AUTO / COR flags.
        while (i < tokens.Length && tokens[i] is "AUTO" or "COR" or "NIL")
        {
            if (tokens[i] == "COR") cor = true;
            if (tokens[i] == "AUTO") auto = true;
            if (tokens[i] == "NIL") return EmptyMetar(raw, icao, obsTime, auto, cor);
            i++;
        }

        WindInfo? wind = null;
        VisibilityInfo? vis = null;
        List<RvrInfo> rvr = [];
        List<WeatherInfo> wx = [];
        List<CloudLayer> clouds = [];
        int? tempC = null, dewC = null;
        int? qnh = null;
        double? alt = null;
        string? qfe = null;
        List<RunwayStateInfo> rwyStates = [];
        List<TafChange> trends = [];
        string? remarks = null;

        while (i < tokens.Length)
        {
            string tok = tokens[i];

            // Wind.
            if (wind == null && WindRx().Match(tok) is { Success: true } wm)
            {
                bool vrb = wm.Groups[1].Value == "VRB";
                int dir = vrb ? 0 : int.Parse(wm.Groups[1].Value);
                int spd = int.Parse(wm.Groups[2].Value);
                int? gust = wm.Groups[4].Length > 0 ? int.Parse(wm.Groups[4].Value) : null;
                string unit = wm.Groups[5].Value;
                int? vFrom = null, vTo = null;
                if (i + 1 < tokens.Length && VarSectorRx().IsMatch(tokens[i + 1]))
                {
                    var v = tokens[i + 1].Split('V');
                    vFrom = int.Parse(v[0]); vTo = int.Parse(v[1]);
                    i++;
                }
                wind = new WindInfo(vrb, dir, spd, gust, unit, vFrom, vTo);
                i++; continue;
            }

            // Visibility — metres.
            if (vis == null && VisMetresRx().IsMatch(tok))
            {
                int m = int.Parse(tok);
                // Ignore 9999 wind group match: must come after wind group
                vis = new VisibilityInfo(m, null, false, false);
                i++; continue;
            }

            // Visibility — statute miles.
            if (vis == null && VisSmRx().Match(tok) is { Success: true } sm)
            {
                bool less = sm.Groups[1].Value.StartsWith('M');
                double val = double.Parse(sm.Groups[2].Value + (sm.Groups[3].Length > 0 ? sm.Groups[3].Value : ""),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (less) val = -val; // less than
                vis = new VisibilityInfo(null, val, false, false);
                i++; continue;
            }

            // SM fractions like "1/2SM", "3/4SM", "1 1/2SM".
            if (vis == null && tok.EndsWith("SM") && tok.Contains('/'))
            {
                var parts = tok[..^2].Split('/');
                if (parts.Length == 2 && int.TryParse(parts[0], out int num) && int.TryParse(parts[1], out int den))
                {
                    // Check if previous token was a whole number (e.g. "1 3/4SM").
                    double whole = 0;
                    if (i > 0 && int.TryParse(tokens[i - 1], out int w)) { whole = w; }
                    vis = new VisibilityInfo(null, whole + (double)num / den, false, false);
                    i++; continue;
                }
            }

            // CAVOK / NSC / NCD / CLR / SKC.
            if (vis == null && tok is "CAVOK")
            {
                vis = new VisibilityInfo(null, null, true, false);
                i++; continue;
            }
            if (tok is "NSC" or "NCD" or "CLR" or "SKC")
            {
                if (vis == null) vis = new VisibilityInfo(10000, null, false, true);
                if (tok is "NSC" or "NCD" or "CLR" or "SKC") clouds.Add(new CloudLayer(tok, null, null));
                i++; continue;
            }

            // CAVOK already sets vis; now handle case where CAVOK after vis set (shouldn't happen).

            // RVR.
            if (RvrRx().Match(tok) is { Success: true } rm)
            {
                string rwy = rm.Groups[1].Value;
                string q1 = rm.Groups[2].Value;
                int d1 = int.Parse(rm.Groups[3].Value);
                string? q2 = rm.Groups[5].Length > 0 ? rm.Groups[5].Value : null;
                int? d2 = rm.Groups[6].Length > 0 ? int.Parse(rm.Groups[6].Value) : null;
                string unit = rm.Groups[7].Length > 0 ? "FT" : "M";
                char? tend = rm.Groups[8].Length > 0 ? rm.Groups[8].Value[0] : null;
                rvr.Add(new RvrInfo(rwy, q1, d1, q2, d2, unit, tend));
                i++; continue;
            }

            // Weather.
            if (ParseWeather(tok) is { } wxi) { wx.Add(wxi); i++; continue; }

            // Cloud layers.
            if (CloudRx().Match(tok) is { Success: true } cm)
            {
                int? ft = int.TryParse(cm.Groups[2].Value, out int fv) ? fv * 100 : null;
                string? ct = cm.Groups[3].Length > 0 ? cm.Groups[3].Value : null;
                clouds.Add(new CloudLayer(cm.Groups[1].Value, ft, ct));
                i++; continue;
            }

            // Temp/dew.
            if (tempC == null && TempRx().Match(tok) is { Success: true } ttm)
            {
                int t = int.Parse(ttm.Groups[2].Value) * (ttm.Groups[1].Value == "M" ? -1 : 1);
                int? d = ttm.Groups[4].Value == "//" ? null :
                    int.Parse(ttm.Groups[4].Value) * (ttm.Groups[3].Value == "M" ? -1 : 1);
                tempC = t; dewC = d;
                i++; continue;
            }

            // QNH.
            if (QnhRx().Match(tok) is { Success: true } qm) { qnh = int.Parse(qm.Groups[1].Value); i++; continue; }
            if (AltRx().Match(tok) is { Success: true } am) { alt = int.Parse(am.Groups[1].Value) / 100.0; i++; continue; }

            // Runway state groups (Russian).
            if (RwyStateRx().Match(tok) is { Success: true } rsm)
            {
                rwyStates.Add(ParseRunwayState(rsm.Groups[1].Value, rsm.Groups[2].Value));
                i++; continue;
            }
            if (RwyClrdRx().Match(tok) is { Success: true } rcm)
            {
                rwyStates.Add(new RunwayStateInfo(rcm.Groups[1].Value, null, null, null, rcm.Groups[2].Value, false, true));
                i++; continue;
            }
            if (tok == "SNOCLO") { rwyStates.Add(new RunwayStateInfo("00", null, null, null, null, true, false)); i++; continue; }

            // NOSIG / BECMG / TEMPO trends.
            if (tok is "NOSIG") { trends.Add(new TafChange("NOSIG", null, null, null, null, [], [], true, "No significant change", "Без существенных изменений")); i++; continue; }
            if (tok is "BECMG" or "TEMPO")
            {
                string type = tok; i++;
                var (change, adv) = ParseTafChangeBody(tokens, ref i, type, DateTime.MinValue);
                trends.Add(change);
                continue;
            }

            // Remarks: everything after "RMK".
            if (tok == "RMK")
            {
                i++;
                var sb = new StringBuilder();
                while (i < tokens.Length) { sb.Append(tokens[i++]); sb.Append(' '); }
                remarks = sb.ToString().Trim();
                // Extract QFE from remarks.
                if (QfeRx().Match(remarks) is { Success: true } qfem) qfe = qfem.Value;
                break;
            }

            // QFE outside RMK (Russian METARs sometimes put it before RMK).
            if (tok.StartsWith("QFE") && QfeRx().Match(tok) is { Success: true } qfet)
            {
                qfe = qfet.Value;
                i++; continue;
            }

            i++; // unknown token, skip
        }

        string cat = FlightCategory(vis, clouds);
        string en = HumanMetarEn(wind, vis, wx, clouds, tempC, dewC, qnh, alt, cat);
        string ru = HumanMetarRu(wind, vis, wx, clouds, tempC, dewC, qnh, alt, cat);

        return new DecodedMetar(raw, icao, obsTime, auto, cor, wind, vis, rvr, wx, clouds,
            tempC, dewC, qnh, alt, qfe, rwyStates, trends, remarks, cat, en, ru);
    }

    // ── TAF ───────────────────────────────────────────────────────────────────

    public static DecodedTaf DecodeTaf(string raw)
    {
        var tokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        if (i < tokens.Length && tokens[i] == "TAF") i++;
        bool amd = false, cor = false;
        if (i < tokens.Length && tokens[i] is "AMD") { amd = true; i++; }
        if (i < tokens.Length && tokens[i] is "COR") { cor = true; i++; }

        string icao = i < tokens.Length ? tokens[i++].ToUpperInvariant() : "";
        DateTime issueTime = DateTime.UtcNow;
        if (i < tokens.Length && ObsTimeRx().Match(tokens[i]) is { Success: true } it)
        {
            var now = DateTime.UtcNow;
            int day = int.Parse(it.Groups[1].Value), hour = int.Parse(it.Groups[2].Value), min = int.Parse(it.Groups[3].Value);
            issueTime = new DateTime(now.Year, now.Month, day, hour, min, 0, DateTimeKind.Utc);
            if (day > now.Day) issueTime = issueTime.AddMonths(-1);
            i++;
        }

        // Validity period: DDHH/DDHH.
        DateTime validFrom = DateTime.MinValue, validTo = DateTime.MinValue;
        if (i < tokens.Length && ValidityRx().Match(tokens[i]) is { Success: true } vp)
        {
            validFrom = ParseDdHh(vp.Groups[1].Value, issueTime);
            validTo   = ParseDdHh(vp.Groups[2].Value, issueTime);
            i++;
        }

        // Parse the main body and change groups.
        List<TafChange> groups = [];
        var (main, _) = ParseTafChangeBody(tokens, ref i, "MAIN", issueTime);
        groups.Add(main with { From = validFrom, To = validTo });

        while (i < tokens.Length)
        {
            string tok = tokens[i];
            string type;
            if (FmRx().IsMatch(tok)) { type = "FM"; }
            else if (tok is "BECMG" or "TEMPO") { type = tok; i++; }
            else if (tok == "PROB30") { i++; type = i < tokens.Length && tokens[i] is "TEMPO" or "BECMG" ? $"PROB30{tokens[i++]}" : "PROB30"; }
            else if (tok == "PROB40") { i++; type = i < tokens.Length && tokens[i] is "TEMPO" or "BECMG" ? $"PROB40{tokens[i++]}" : "PROB40"; }
            else { i++; continue; }

            var (change, _) = ParseTafChangeBody(tokens, ref i, type, issueTime);
            groups.Add(change);
        }

        string en = HumanTafEn(groups, validFrom, validTo);
        string ru = HumanTafRu(groups, validFrom, validTo);
        return new DecodedTaf(raw, icao, issueTime, validFrom, validTo, amd, cor, groups, en, ru);
    }

    // ── TAF change body ───────────────────────────────────────────────────────

    private static readonly string[] ChangeKeywords = ["FM", "BECMG", "TEMPO", "PROB30", "PROB40", "RMK"];

    private static (TafChange change, int advance) ParseTafChangeBody(string[] tokens, ref int i, string type, DateTime anchor)
    {
        DateTime? from = null, to = null;

        // FM ddhhnn
        if (type == "FM" && i < tokens.Length && FmRx().Match(tokens[i]) is { Success: true } fm)
        {
            from = ParseFm(fm, anchor);
            i++;
        }

        // BECMG/TEMPO validity ddhh/ddhh
        if (i < tokens.Length && ValidityRx().Match(tokens[i]) is { Success: true } vp)
        {
            from = ParseDdHh(vp.Groups[1].Value, anchor);
            to   = ParseDdHh(vp.Groups[2].Value, anchor);
            i++;
        }

        WindInfo? wind = null;
        VisibilityInfo? vis = null;
        List<WeatherInfo> wx = [];
        List<CloudLayer> clouds = [];
        bool nosig = false;

        while (i < tokens.Length)
        {
            string tok = tokens[i];
            if (tok is "NOSIG") { nosig = true; i++; continue; }
            // Stop at next change group keyword.
            if (IsChangeKeyword(tok)) break;

            if (wind == null && WindRx().Match(tok) is { Success: true } wm)
            {
                bool vrb = wm.Groups[1].Value == "VRB";
                int dir = vrb ? 0 : int.Parse(wm.Groups[1].Value);
                int spd = int.Parse(wm.Groups[2].Value);
                int? gust = wm.Groups[4].Length > 0 ? int.Parse(wm.Groups[4].Value) : null;
                wind = new WindInfo(vrb, dir, spd, gust, wm.Groups[5].Value, null, null);
                if (i + 1 < tokens.Length && VarSectorRx().IsMatch(tokens[i + 1])) i++;
                i++; continue;
            }
            if (vis == null && VisMetresRx().IsMatch(tok)) { vis = new VisibilityInfo(int.Parse(tok), null, false, false); i++; continue; }
            if (vis == null && VisSmRx().Match(tok) is { Success: true } sm)
            {
                double val = double.Parse(sm.Groups[2].Value + sm.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                vis = new VisibilityInfo(null, val, false, false); i++; continue;
            }
            if (tok == "CAVOK") { vis = new VisibilityInfo(null, null, true, false); i++; continue; }
            if (tok is "NSC" or "NCD" or "CLR" or "SKC") { clouds.Add(new CloudLayer(tok, null, null)); i++; continue; }
            if (ParseWeather(tok) is { } wxi) { wx.Add(wxi); i++; continue; }
            if (CloudRx().Match(tok) is { Success: true } cm)
            {
                int? ft = int.TryParse(cm.Groups[2].Value, out int fv) ? fv * 100 : null;
                clouds.Add(new CloudLayer(cm.Groups[1].Value, ft, cm.Groups[3].Length > 0 ? cm.Groups[3].Value : null));
                i++; continue;
            }
            i++;
        }

        string en = HumanGroupEn(type, from, to, wind, vis, wx, clouds, nosig);
        string ru = HumanGroupRu(type, from, to, wind, vis, wx, clouds, nosig);
        return (new TafChange(type, from, to, wind, vis, wx, clouds, nosig, en, ru), 0);
    }

    private static bool IsChangeKeyword(string tok)
    {
        if (tok is "BECMG" or "TEMPO" or "NOSIG" or "RMK") return true;
        if (FmRx().IsMatch(tok)) return true;
        if (tok.StartsWith("PROB")) return true;
        return false;
    }

    // ── Helper parsers ────────────────────────────────────────────────────────

    private static WeatherInfo? ParseWeather(string tok)
    {
        // Pattern: [intensity] [descriptor] phenomenon...
        string rest = tok;
        string intensity = "";
        if (rest.StartsWith("VC")) { intensity = "VC"; rest = rest[2..]; }
        else if (rest.StartsWith('+')) { intensity = "+"; rest = rest[1..]; }
        else if (rest.StartsWith('-')) { intensity = "-"; rest = rest[1..]; }

        string? desc = null;
        if (rest.Length >= 2 && Descriptors.Contains(rest[..2]))
        {
            desc = rest[..2]; rest = rest[2..];
        }

        List<string> phenomena = [];
        while (rest.Length >= 2)
        {
            string code = rest[..2];
            if (WxCodes.ContainsKey(code)) { phenomena.Add(code); rest = rest[2..]; }
            else break;
        }

        if (phenomena.Count == 0 && desc == null) return null;
        if (phenomena.Count == 0 && desc != null) return null; // descriptor alone is not valid
        return new WeatherInfo(intensity, desc, phenomena);
    }

    private static RunwayStateInfo ParseRunwayState(string rwy, string code)
    {
        // code is 6 chars: deposit(1) contamination(1) depth(2) friction(2)
        if (code.Length < 6) return new RunwayStateInfo(rwy, null, null, null, null, false, false);
        string deposit = DepositCode(code[0]);
        string contamination = ContaminationCode(code[1]);
        string depth = DepthCode(code[2..4]);
        string friction = FrictionCode(code[4..6]);
        return new RunwayStateInfo(rwy, deposit, contamination, depth, friction, false, false);
    }

    private static string DepositCode(char c) => c switch
    {
        '0' => "Clear and dry", '1' => "Damp", '2' => "Wet/water patches",
        '3' => "Rime/frost", '4' => "Dry snow", '5' => "Wet snow",
        '6' => "Slush", '7' => "Ice", '8' => "Compacted snow",
        '9' => "Frozen ruts", _ => ""
    };

    private static string ContaminationCode(char c) => c switch
    {
        '1' => "10%", '2' => "11-25%", '5' => "26-50%", '9' => "51-100%", _ => ""
    };

    private static string DepthCode(string s)
    {
        if (!int.TryParse(s, out int v)) return "";
        if (v == 99) return "runway not operational";
        if (v == 98) return ">40 cm";
        if (v == 92) return "10 cm";
        if (v == 91) return "<1 mm";
        return $"{v} mm";
    }

    private static string FrictionCode(string s)
    {
        if (!int.TryParse(s, out int v)) return "";
        if (v == 99) return "not measured";
        if (v == 95) return "poor";
        if (v == 94) return "medium/poor";
        if (v == 93) return "medium";
        if (v == 92) return "medium/good";
        if (v == 91) return "good";
        return $"µ={v / 100.0:F2}";
    }

    private static DateTime ParseDdHh(string ddhh, DateTime anchor)
    {
        int day = int.Parse(ddhh[..2]), hour = int.Parse(ddhh[2..]);
        if (hour == 24) { day++; hour = 0; }
        var dt = new DateTime(anchor.Year, anchor.Month, day, hour, 0, 0, DateTimeKind.Utc);
        if (day < anchor.Day) dt = dt.AddMonths(1);
        return dt;
    }

    private static DateTime ParseFm(Match fm, DateTime anchor)
    {
        int day = int.Parse(fm.Groups[1].Value), hour = int.Parse(fm.Groups[2].Value), min = int.Parse(fm.Groups[3].Value);
        var dt = new DateTime(anchor.Year, anchor.Month, day, hour, min, 0, DateTimeKind.Utc);
        if (day < anchor.Day) dt = dt.AddMonths(1);
        return dt;
    }

    // ── Flight category ───────────────────────────────────────────────────────

    public static string FlightCategory(VisibilityInfo? vis, IReadOnlyList<CloudLayer> clouds)
    {
        int visM = vis?.EffectiveMetres ?? 0;
        double visSm = visM / 1609.34;

        // Lowest ceiling (BKN or OVC).
        int? ceiling = null;
        foreach (var c in clouds)
            if (c.Cover is "BKN" or "OVC" && c.AltitudeFt is { } ft)
                if (ceiling == null || ft < ceiling) ceiling = ft;

        // VV counts as ceiling.
        foreach (var c in clouds)
            if (c.Cover == "VV" && c.AltitudeFt is { } ft)
                if (ceiling == null || ft < ceiling) ceiling = ft;

        bool cavok = vis?.Cavok == true || vis?.Nsc == true;

        // Check ceiling.
        bool cLifr  = ceiling is < 500;
        bool cIfr   = ceiling is >= 500 and < 1000;
        bool cMvfr  = ceiling is >= 1000 and < 3000;
        // Check visibility.
        bool vLifr  = !cavok && visSm < 1.0;
        bool vIfr   = !cavok && visSm is >= 1.0 and < 3.0;
        bool vMvfr  = !cavok && visSm is >= 3.0 and < 5.0;

        if (cLifr || vLifr) return "LIFR";
        if (cIfr  || vIfr)  return "IFR";
        if (cMvfr || vMvfr) return "MVFR";
        return "VFR";
    }

    // ── Human-readable text ───────────────────────────────────────────────────

    private static string HumanMetarEn(WindInfo? wind, VisibilityInfo? vis, IReadOnlyList<WeatherInfo> wx,
        IReadOnlyList<CloudLayer> clouds, int? tempC, int? dewC, int? qnh, double? alt, string cat)
    {
        var sb = new StringBuilder();
        if (wind != null) sb.Append(WindEn(wind)).Append(", ");
        if (vis != null) sb.Append(VisEn(vis)).Append(", ");
        foreach (var w in wx) sb.Append(WxEn(w)).Append(", ");
        foreach (var c in clouds) if (c.AltitudeFt != null) sb.Append(CloudEn(c)).Append(", ");
        if (tempC != null) sb.Append($"{tempC}°C/{dewC}°C, ");
        if (qnh != null) sb.Append($"QNH {qnh} hPa, ");
        else if (alt != null) sb.Append($"Altimeter {alt:F2} inHg, ");
        sb.Append($"Flight category: {cat}");
        return sb.ToString();
    }

    private static string HumanMetarRu(WindInfo? wind, VisibilityInfo? vis, IReadOnlyList<WeatherInfo> wx,
        IReadOnlyList<CloudLayer> clouds, int? tempC, int? dewC, int? qnh, double? alt, string cat)
    {
        var sb = new StringBuilder();
        if (wind != null) sb.Append(WindRu(wind)).Append(", ");
        if (vis != null) sb.Append(VisRu(vis)).Append(", ");
        foreach (var w in wx) sb.Append(WxRu(w)).Append(", ");
        foreach (var c in clouds) if (c.AltitudeFt != null) sb.Append(CloudRu(c)).Append(", ");
        if (tempC != null) sb.Append($"{tempC}°C/{dewC}°C, ");
        if (qnh != null) sb.Append($"QNH {qnh} гПа, ");
        else if (alt != null) sb.Append($"Высотомер {alt:F2} дюйм.рт.ст., ");
        sb.Append($"Категория: {cat}");
        return sb.ToString();
    }

    private static string HumanTafEn(IReadOnlyList<TafChange> groups, DateTime from, DateTime to)
    {
        var sb = new StringBuilder();
        sb.Append($"Valid {from:ddHHmm}Z–{to:ddHHmm}Z. ");
        foreach (var g in groups) { sb.Append(g.HumanEn); sb.Append(' '); }
        return sb.ToString().Trim();
    }

    private static string HumanTafRu(IReadOnlyList<TafChange> groups, DateTime from, DateTime to)
    {
        var sb = new StringBuilder();
        sb.Append($"Действует {from:ddHHmm}Z–{to:ddHHmm}Z. ");
        foreach (var g in groups) { sb.Append(g.HumanRu); sb.Append(' '); }
        return sb.ToString().Trim();
    }

    private static string HumanGroupEn(string type, DateTime? from, DateTime? to, WindInfo? wind, VisibilityInfo? vis,
        IReadOnlyList<WeatherInfo> wx, IReadOnlyList<CloudLayer> clouds, bool nosig)
    {
        if (nosig) return "No significant change.";
        var sb = new StringBuilder();
        string prefix = type switch
        {
            "MAIN"        => "",
            "FM"          => from != null ? $"From {from:HHmm}Z: " : "From: ",
            "BECMG"       => from != null && to != null ? $"Becoming {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Becoming: ",
            "TEMPO"       => from != null && to != null ? $"Temporary {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Temporary: ",
            "PROB30"      => from != null && to != null ? $"Prob 30% {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Prob 30%: ",
            "PROB40"      => from != null && to != null ? $"Prob 40% {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Prob 40%: ",
            "PROB30TEMPO" => from != null && to != null ? $"Prob 30% tempo {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Prob 30% tempo: ",
            "PROB40TEMPO" => from != null && to != null ? $"Prob 40% tempo {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Prob 40% tempo: ",
            _             => $"{type}: "
        };
        sb.Append(prefix);
        if (wind != null) sb.Append(WindEn(wind)).Append(", ");
        if (vis != null) sb.Append(VisEn(vis)).Append(", ");
        foreach (var w in wx) sb.Append(WxEn(w)).Append(", ");
        foreach (var c in clouds) sb.Append(CloudEn(c)).Append(", ");
        return sb.ToString().TrimEnd(' ', ',') + ".";
    }

    private static string HumanGroupRu(string type, DateTime? from, DateTime? to, WindInfo? wind, VisibilityInfo? vis,
        IReadOnlyList<WeatherInfo> wx, IReadOnlyList<CloudLayer> clouds, bool nosig)
    {
        if (nosig) return "Без существенных изменений.";
        var sb = new StringBuilder();
        string prefix = type switch
        {
            "MAIN"        => "",
            "FM"          => from != null ? $"С {from:HHmm}Z: " : "С: ",
            "BECMG"       => from != null && to != null ? $"Улучш/ухудш {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Улучш/ухудш: ",
            "TEMPO"       => from != null && to != null ? $"Временно {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Временно: ",
            "PROB30"      => from != null && to != null ? $"Веро 30% {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Вероятность 30%: ",
            "PROB40"      => from != null && to != null ? $"Веро 40% {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Вероятность 40%: ",
            "PROB30TEMPO" => from != null && to != null ? $"Вер.30% вр {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Вероятность 30% временно: ",
            "PROB40TEMPO" => from != null && to != null ? $"Вер.40% вр {from:ddHHmm}Z–{to:ddHHmm}Z: " : "Вероятность 40% временно: ",
            _             => $"{type}: "
        };
        sb.Append(prefix);
        if (wind != null) sb.Append(WindRu(wind)).Append(", ");
        if (vis != null) sb.Append(VisRu(vis)).Append(", ");
        foreach (var w in wx) sb.Append(WxRu(w)).Append(", ");
        foreach (var c in clouds) sb.Append(CloudRu(c)).Append(", ");
        return sb.ToString().TrimEnd(' ', ',') + ".";
    }

    // ── Text builders ─────────────────────────────────────────────────────────

    private static string WindEn(WindInfo w)
    {
        string spd = w.Unit == "MPS"
            ? $"{w.Speed} m/s"
            : $"{w.Speed} kt";
        string gust = w.Gust != null ? $" gusting {w.Gust} {(w.Unit == "MPS" ? "m/s" : "kt")}" : "";
        string dir = w.Variable ? "variable" : $"{w.Direction:D3}°";
        string var = w.VarFrom != null ? $" varying {w.VarFrom}°–{w.VarTo}°" : "";
        return $"Wind {dir} {spd}{gust}{var}";
    }

    private static string WindRu(WindInfo w)
    {
        string spd = w.Unit == "MPS" ? $"{w.Speed} м/с" : $"{w.Speed} уз";
        string gust = w.Gust != null ? $" порывы {w.Gust} {(w.Unit == "MPS" ? "м/с" : "уз")}" : "";
        string dir = w.Variable ? "переменный" : $"{w.Direction:D3}°";
        string var = w.VarFrom != null ? $" изменение {w.VarFrom}°–{w.VarTo}°" : "";
        return $"Ветер {dir} {spd}{gust}{var}";
    }

    private static string VisEn(VisibilityInfo v)
    {
        if (v.Cavok) return "CAVOK";
        if (v.Nsc) return "No significant cloud";
        if (v.Metres is { } m) return m >= 9999 ? "visibility 10 km or more" : $"visibility {m} m";
        if (v.StatuteMiles is { } sm) return $"visibility {sm:F1} SM";
        return "visibility not reported";
    }

    private static string VisRu(VisibilityInfo v)
    {
        if (v.Cavok) return "CAVOK";
        if (v.Nsc) return "Нет существенной облачности";
        if (v.Metres is { } m) return m >= 9999 ? "видимость 10 км и более" : $"видимость {m} м";
        if (v.StatuteMiles is { } sm) return $"видимость {sm:F1} СМ";
        return "видимость не сообщается";
    }

    private static string WxEn(WeatherInfo w)
    {
        string intens = w.Intensity switch { "-" => "light ", "+" => "heavy ", "VC" => "in vicinity: ", _ => "" };
        string desc = w.Descriptor != null && DescText.TryGetValue(w.Descriptor, out var dt) ? dt.en + " " : "";
        string phen = string.Join(", ", w.Phenomena.Select(p => WxCodes.TryGetValue(p, out var wt) ? wt.en : p));
        return $"{intens}{desc}{phen}";
    }

    private static string WxRu(WeatherInfo w)
    {
        string intens = w.Intensity switch { "-" => "слабый ", "+" => "сильный ", "VC" => "вблизи: ", _ => "" };
        string desc = w.Descriptor != null && DescText.TryGetValue(w.Descriptor, out var dt) ? dt.ru + " " : "";
        string phen = string.Join(", ", w.Phenomena.Select(p => WxCodes.TryGetValue(p, out var wt) ? wt.ru : p));
        return $"{intens}{desc}{phen}";
    }

    private static string CloudEn(CloudLayer c)
    {
        if (c.Cover is "SKC" or "CLR") return "sky clear";
        if (c.Cover is "NSC" or "NCD") return "no significant cloud";
        string cover = CloudText.TryGetValue(c.Cover, out var ct) ? ct.en : c.Cover;
        string alt = c.AltitudeFt != null ? $" {c.AltitudeFt / 100:D3}" : "";
        string kind = c.CloudType != null ? $" ({c.CloudType})" : "";
        return $"{cover}{alt}{kind}";
    }

    private static string CloudRu(CloudLayer c)
    {
        if (c.Cover is "SKC" or "CLR") return "небо ясно";
        if (c.Cover is "NSC" or "NCD") return "нет существ. облачности";
        string cover = CloudText.TryGetValue(c.Cover, out var ct) ? ct.ru : c.Cover;
        string alt = c.AltitudeFt != null ? $" {c.AltitudeFt / 100:D3}" : "";
        string kind = c.CloudType != null ? $" ({c.CloudType})" : "";
        return $"{cover}{alt}{kind}";
    }

    private static DecodedMetar EmptyMetar(string raw, string icao, DateTime obsTime, bool auto, bool cor) =>
        new(raw, icao, obsTime, auto, cor, null, null, [], [], [], null, null, null, null, null, [], [], null, "VFR", "NIL", "NIL");
}
