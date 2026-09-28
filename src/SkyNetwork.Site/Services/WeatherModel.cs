namespace SkyNetwork.Site.Services;

// ── Wind ──────────────────────────────────────────────────────────────────────

/// <param name="Variable">True when no prevailing direction (VRB).</param>
/// <param name="Direction">Magnetic degrees, 0 when Variable.</param>
/// <param name="Speed">Speed in the original unit.</param>
/// <param name="Gust">Gusting speed, null if no gusts.</param>
/// <param name="Unit">KT, MPS, or KMH.</param>
/// <param name="VarFrom">Variable sector low bound (dddVddd group).</param>
/// <param name="VarTo">Variable sector high bound.</param>
public record WindInfo(bool Variable, int Direction, int Speed, int? Gust, string Unit,
    int? VarFrom, int? VarTo)
{
    public double SpeedKt => Unit switch { "KT" => Speed, "MPS" => Speed / 0.514444, "KMH" => Speed / 1.852, _ => Speed };
    public double SpeedMs => Unit switch { "MPS" => Speed, "KT" => Speed * 0.514444, "KMH" => Speed / 3.6, _ => Speed };
    public double? GustKt => Gust is null ? null : Unit switch { "KT" => Gust, "MPS" => Gust / 0.514444, "KMH" => Gust / 1.852, _ => Gust };
    public double? GustMs => Gust is null ? null : Unit switch { "MPS" => Gust, "KT" => Gust * 0.514444, "KMH" => Gust / 3.6, _ => Gust };
}

// ── Visibility ────────────────────────────────────────────────────────────────

public record VisibilityInfo(int? Metres, double? StatuteMiles, bool Cavok, bool Nsc)
{
    /// Effective visibility in metres for flight-category calculation.
    public int EffectiveMetres => Cavok ? 10000 : Metres ?? (StatuteMiles is { } sm ? (int)(sm * 1609.34) : 0);
    public double EffectiveSm => EffectiveMetres / 1609.34;
}

// ── RVR ───────────────────────────────────────────────────────────────────────

/// <param name="Runway">Designator, e.g. "28" or "10L".</param>
/// <param name="Qualifier">P (more than), M (less than), or "".</param>
/// <param name="Distance">RVR value.</param>
/// <param name="VarQualifier">Qualifier for variable high end.</param>
/// <param name="VarDistance">Variable high end, null if fixed.</param>
/// <param name="Unit">FT or M.</param>
/// <param name="Tendency">U (increasing), D (decreasing), N (no change), null (not reported).</param>
public record RvrInfo(string Runway, string Qualifier, int Distance, string? VarQualifier, int? VarDistance, string Unit, char? Tendency);

// ── Present weather ───────────────────────────────────────────────────────────

/// <param name="Intensity">-, +, VC, or "" (moderate).</param>
/// <param name="Descriptor">MI BC PR DR BL SH TS FZ or null.</param>
/// <param name="Phenomena">One or more ICAO weather codes.</param>
public record WeatherInfo(string Intensity, string? Descriptor, IReadOnlyList<string> Phenomena);

// ── Clouds ────────────────────────────────────────────────────────────────────

/// <param name="Cover">FEW SCT BKN OVC VV SKC CLR NSC NCD.</param>
/// <param name="AltitudeFt">In hundreds of feet, null for SKC/CLR/NSC/NCD.</param>
/// <param name="CloudType">CB, TCU, or null.</param>
public record CloudLayer(string Cover, int? AltitudeFt, string? CloudType);

// ── Runway state (Russian METARs) ─────────────────────────────────────────────

public record RunwayStateInfo(string Runway, string? Deposit, string? Contamination, string? Depth, string? Friction, bool Snoclo, bool Clrd);

// ── Decoded METAR ─────────────────────────────────────────────────────────────

public record DecodedMetar(
    string Raw,
    string Icao,
    DateTime ObsTime,          // UTC
    bool Auto, bool Cor,
    WindInfo? Wind,
    VisibilityInfo? Visibility,
    IReadOnlyList<RvrInfo> Rvr,
    IReadOnlyList<WeatherInfo> Weather,
    IReadOnlyList<CloudLayer> Clouds,
    int? TempC, int? DewC,
    int? QnhHpa, double? AltInHg,
    string? Qfe,
    IReadOnlyList<RunwayStateInfo> RunwayStates,
    IReadOnlyList<TafChange> Trends,   // NOSIG / BECMG / TEMPO at the end
    string? Remarks,
    string FlightCategory,             // VFR MVFR IFR LIFR
    string HumanEn,
    string HumanRu);

// ── TAF change group ──────────────────────────────────────────────────────────

/// <param name="Type">MAIN FM BECMG TEMPO PROB30 PROB40 PROB30TEMPO PROB40TEMPO NOSIG.</param>
public record TafChange(
    string Type,
    DateTime? From, DateTime? To,
    WindInfo? Wind,
    VisibilityInfo? Visibility,
    IReadOnlyList<WeatherInfo> Weather,
    IReadOnlyList<CloudLayer> Clouds,
    bool Nosig,
    string HumanEn,
    string HumanRu);

// ── Decoded TAF ───────────────────────────────────────────────────────────────

public record DecodedTaf(
    string Raw,
    string Icao,
    DateTime IssueTime,
    DateTime ValidFrom, DateTime ValidTo,
    bool Amended, bool Corrected,
    IReadOnlyList<TafChange> Groups,
    string HumanEn,
    string HumanRu);

// ── Winds aloft ───────────────────────────────────────────────────────────────

public record AloftPoint(
    double Latitude, double Longitude,
    int FlightLevel,           // e.g. 350
    DateTime ValidTime,
    int WindDirection,         // degrees true
    double WindSpeedKt,
    double TemperatureC,
    double IsaDeviationC);
