using System.Text.Json;
using Dapper;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Aircraft performance profiles derived from OpenAP (TU Delft, LGPL-3.0).
/// Fuel flow model: simplified BADA-style using nominal cruise flow scaled by mass,
/// altitude (pressure ratio), Mach, and flight phase. Coefficients from OpenAP
/// engine and WRAP kinematic data; see LICENCES/openap.txt.
/// </summary>
public sealed class AircraftService
{
    private readonly Database _db;

    /// <summary>ISA sea-level pressure (Pa).</summary>
    private const double P0 = 101325.0;
    /// <summary>Reference altitude for nominal cruise fuel flow (FL350, 35 000 ft).</summary>
    private const double AltRefFt = 35_000.0;
    /// <summary>Reference pressure ratio at FL350.</summary>
    private static readonly double DeltaRef = DeltaAt(AltRefFt);

    private static readonly Dictionary<string, AircraftProfile> _profiles;
    private static readonly string _profilesPath;

    static AircraftService()
    {
        _profilesPath = Path.Combine(AppContext.BaseDirectory, "Aircraft", "profiles.json");
        _profiles = LoadProfiles(_profilesPath);
    }

    public AircraftService(Database db) => _db = db;

    // ── public query ──────────────────────────────────────────────────────────

    /// <summary>Returns all profiles with staff overrides applied.</summary>
    public IReadOnlyList<AircraftProfile> All()
    {
        var overrides = LoadOverrides();
        return _profiles.Values
            .Select(p => ApplyOverride(p, overrides.GetValueOrDefault(p.Icao)))
            .OrderBy(p => p.Icao)
            .ToList();
    }

    /// <summary>Returns a single profile with overrides, or null if unknown.</summary>
    public AircraftProfile? Get(string icao)
    {
        icao = icao.ToUpperInvariant();
        if (!_profiles.TryGetValue(icao, out var p)) return null;
        var overrides = LoadOverrides();
        return ApplyOverride(p, overrides.GetValueOrDefault(icao));
    }

    // ── fuel flow ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Estimates total fuel flow (all engines combined) in kg/h.
    /// <para>
    /// Formula (cruise): <c>ff = nom_cruise_ff * (mass/ref_mass)^mass_exp *
    /// (delta/delta_ref)^alt_exp * phase_factor * (1 + 0.006 * isa_dev)</c>
    /// where <c>ref_mass = 0.80 * MTOW</c> and <c>delta_ref</c> is the ISA pressure
    /// ratio at FL350.
    /// </para>
    /// </summary>
    /// <param name="profile">Aircraft profile (with overrides applied).</param>
    /// <param name="massTakeoffKg">Current aircraft mass (kg).</param>
    /// <param name="altitudeFt">Pressure altitude (ft).</param>
    /// <param name="machOrTas">Mach number if ≤ 2, or TAS in knots if > 2.</param>
    /// <param name="phase">Flight phase: "climb", "cruise", "descent", "taxi".</param>
    /// <param name="isaDevK">ISA temperature deviation in Kelvin (default 0).</param>
    public static double FuelFlowKgH(
        AircraftProfile profile,
        double massTakeoffKg,
        double altitudeFt,
        double machOrTas,
        string phase = "cruise",
        double isaDevK = 0.0)
    {
        double refMass = 0.80 * profile.MtowKg;
        double massRatio = refMass > 0 ? massTakeoffKg / refMass : 1.0;

        double delta = DeltaAt(altitudeFt);
        double altFactor = DeltaRef > 0 && delta > 0
            ? Math.Pow(delta / DeltaRef, profile.AltExp)
            : 1.0;

        // Mach correction: deviation from typical cruise Mach (±5% changes flow by ±3%)
        double mach = machOrTas > 2.0
            ? MachFromTas(machOrTas, altitudeFt, isaDevK)
            : machOrTas;
        double machTyp = profile.CruiseMachTyp;
        double machFactor = machTyp > 0 ? 1.0 + 0.6 * (mach - machTyp) / machTyp : 1.0;

        double phaseFactor = profile.PhaseFf.TryGetValue(phase, out var pf) ? pf : 1.0;
        double isaFactor = 1.0 + 0.006 * isaDevK;

        double ff = profile.NomCruiseFfKgH
            * Math.Pow(Math.Max(massRatio, 0.3), profile.MassExp)
            * Math.Max(altFactor, 0.1)
            * Math.Max(machFactor, 0.3)
            * Math.Max(phaseFactor, 0.01)
            * Math.Max(isaFactor, 0.5);

        return Math.Max(ff, 0.0);
    }

    // ── staff editing ─────────────────────────────────────────────────────────

    public void SaveOverride(AircraftOverride ov)
    {
        using var c = _db.Open();
        c.Execute("""
            INSERT INTO aircraft_overrides (icao, mtow_kg, mlw_kg, mzfw_kg, oew_kg, mfc_kg,
                seats_typical, pax_mass_kg, baggage_mass_kg, updated_at)
            VALUES (@Icao, @MtowKg, @MlwKg, @MzfwKg, @OewKg, @MfcKg,
                @SeatsTypical, @PaxMassKg, @BaggageMassKg, @UpdatedAt)
            ON CONFLICT(icao) DO UPDATE SET
                mtow_kg = excluded.mtow_kg, mlw_kg = excluded.mlw_kg,
                mzfw_kg = excluded.mzfw_kg, oew_kg = excluded.oew_kg,
                mfc_kg = excluded.mfc_kg, seats_typical = excluded.seats_typical,
                pax_mass_kg = excluded.pax_mass_kg,
                baggage_mass_kg = excluded.baggage_mass_kg,
                updated_at = excluded.updated_at
            """, new { ov.Icao, ov.MtowKg, ov.MlwKg, ov.MzfwKg, ov.OewKg, ov.MfcKg,
                ov.SeatsTypical, ov.PaxMassKg, ov.BaggageMassKg,
                UpdatedAt = Database.Now() });
    }

    public void DeleteOverride(string icao)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM aircraft_overrides WHERE icao = @icao", new { icao = icao.ToUpperInvariant() });
    }

    // ── internals ─────────────────────────────────────────────────────────────

    private Dictionary<string, AircraftOverride> LoadOverrides()
    {
        using var c = _db.Open();
        var rows = c.Query<AircraftOverride>("SELECT * FROM aircraft_overrides");
        return rows.ToDictionary(r => r.Icao, StringComparer.OrdinalIgnoreCase);
    }

    private static AircraftProfile ApplyOverride(AircraftProfile p, AircraftOverride? ov)
    {
        if (ov == null) return p;
        return p with
        {
            MtowKg = ov.MtowKg > 0 ? ov.MtowKg : p.MtowKg,
            MlwKg = ov.MlwKg > 0 ? ov.MlwKg : p.MlwKg,
            MzfwKg = ov.MzfwKg > 0 ? ov.MzfwKg : p.MzfwKg,
            OewKg = ov.OewKg > 0 ? ov.OewKg : p.OewKg,
            MfcKg = ov.MfcKg > 0 ? ov.MfcKg : p.MfcKg,
            SeatsTypical = ov.SeatsTypical > 0 ? ov.SeatsTypical : p.SeatsTypical,
            PaxMassKg = ov.PaxMassKg > 0 ? ov.PaxMassKg : p.PaxMassKg,
            BaggageMassKg = ov.BaggageMassKg > 0 ? ov.BaggageMassKg : p.BaggageMassKg,
            IsOverridden = true,
        };
    }

    /// <summary>ISA pressure ratio at given altitude in feet.</summary>
    public static double DeltaAt(double altFt) =>
        Math.Pow(Math.Max(1.0 - altFt / 145_442.0, 0.01), 5.256);

    /// <summary>Converts TAS (kts) to Mach given pressure altitude and ISA deviation (K).</summary>
    public static double MachFromTas(double tasKts, double altFt, double isaDevK = 0.0)
    {
        double delta = DeltaAt(altFt);
        double theta = Math.Pow(delta, 0.2857) + isaDevK / 288.15;
        double aKts = 661.5 * Math.Sqrt(Math.Max(theta, 0.01));
        return aKts > 0 ? tasKts / aKts : 0;
    }

    private static Dictionary<string, AircraftProfile> LoadProfiles(string path)
    {
        using var s = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<JsonDocument>(s)!;
        var aircraft = doc.RootElement.GetProperty("aircraft");
        var dict = new Dictionary<string, AircraftProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in aircraft.EnumerateObject())
        {
            var el = item.Value;
            string src = el.TryGetProperty("source", out var srcEl) ? srcEl.GetString() ?? "OpenAP" : "OpenAP";
            bool mapped = src.Contains('/') && src.Contains("mapped");
            bool isGa = el.TryGetProperty("category", out var catEl) && catEl.GetString() == "ga";

            // phase_ff
            var phaseMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (el.TryGetProperty("phase_ff", out var phaseEl))
                foreach (var ph in phaseEl.EnumerateObject())
                    phaseMap[ph.Name] = ph.Value.GetDouble();

            double mzfw = el.TryGetProperty("mzfw_kg", out var mzEl) ? mzEl.GetDouble() : 0;
            double mlw = el.TryGetProperty("mlw_kg", out var mlwEl) ? mlwEl.GetDouble() : 0;
            bool mzfwEstimated = mzfw <= 0;
            if (mzfwEstimated) mzfw = mlw - mlw * 0.04; // ~4% MLW margin

            var profile = new AircraftProfile
            {
                Icao = item.Name.ToUpperInvariant(),
                Name = el.GetProperty("name").GetString() ?? item.Name,
                Category = el.TryGetProperty("category", out var c2) ? c2.GetString() ?? "medium" : "medium",
                OewKg = el.TryGetProperty("oew_kg", out var oewEl) ? oewEl.GetDouble() : 0,
                MtowKg = el.TryGetProperty("mtow_kg", out var mtowEl) ? mtowEl.GetDouble() : 0,
                MlwKg = mlw,
                MzfwKg = mzfw,
                MzfwEstimated = mzfwEstimated,
                MfcKg = el.TryGetProperty("mfc_kg", out var mfcEl) ? mfcEl.GetDouble() : 0,
                SeatsTypical = el.TryGetProperty("seats_typical", out var seEl) ? seEl.GetInt32() : 0,
                MaxCargoKg = el.TryGetProperty("max_cargo_kg", out var mcEl) ? mcEl.GetDouble() : 0,
                NEngines = el.TryGetProperty("n_engines", out var neEl) ? neEl.GetInt32() : 2,
                Engine = el.TryGetProperty("engine", out var engEl) ? engEl.GetString() ?? "" : "",
                Source = src,
                IsMapped = mapped,
                MapNote = el.TryGetProperty("_map_note", out var mnEl) ? mnEl.GetString() ?? "" : "",
                CruiseMachLo = el.TryGetProperty("cruise_mach_lo", out var cmlEl) ? cmlEl.GetDouble() : 0,
                CruiseMachHi = el.TryGetProperty("cruise_mach_hi", out var cmhEl) ? cmhEl.GetDouble() : 0,
                CruiseMachTyp = el.TryGetProperty("cruise_mach_typ", out var cmtEl) ? cmtEl.GetDouble() : 0,
                CeilingFt = el.TryGetProperty("ceiling_ft", out var cfEl) ? cfEl.GetInt32() : 0,
                ClimbCasLoKts = el.TryGetProperty("climb_cas_lo_kts", out var cclEl) ? cclEl.GetDouble() : 0,
                ClimbCasHiKts = el.TryGetProperty("climb_cas_hi_kts", out var cchEl) ? cchEl.GetDouble() : 0,
                ClimbMach = el.TryGetProperty("climb_mach", out var cmEl) ? cmEl.GetDouble() : 0,
                ClimbRateLoFpm = el.TryGetProperty("climb_rate_lo_fpm", out var crlEl) ? crlEl.GetDouble() : 0,
                ClimbRateHiFpm = el.TryGetProperty("climb_rate_hi_fpm", out var crhEl) ? crhEl.GetDouble() : 0,
                DescentCasLoKts = el.TryGetProperty("descent_cas_lo_kts", out var dclEl) ? dclEl.GetDouble() : 0,
                DescentCasHiKts = el.TryGetProperty("descent_cas_hi_kts", out var dchEl) ? dchEl.GetDouble() : 0,
                DescentMach = el.TryGetProperty("descent_mach", out var dmEl) ? dmEl.GetDouble() : 0,
                DescentRateLoFpm = el.TryGetProperty("descent_rate_lo_fpm", out var drlEl) ? drlEl.GetDouble() : 0,
                DescentRateHiFpm = el.TryGetProperty("descent_rate_hi_fpm", out var drhEl) ? drhEl.GetDouble() : 0,
                NomCruiseFfKgH = el.TryGetProperty("nom_cruise_ff_kg_h", out var ffEl) ? ffEl.GetDouble() : 0,
                MassExp = el.TryGetProperty("mass_exp", out var meEl) ? meEl.GetDouble() : 0.75,
                AltExp = el.TryGetProperty("alt_exp", out var aeEl) ? aeEl.GetDouble() : 0.5,
                PhaseFf = phaseMap,
                // Defaults: 84 kg pax (incl. carry-on), 15 kg baggage per pax per spec.
                PaxMassKg = isGa ? 84 : 84,
                BaggageMassKg = isGa ? 10 : 15,
            };
            dict[profile.Icao] = profile;
        }
        return dict;
    }

    /// <summary>Path to the bundled profiles JSON (for test fixture access).</summary>
    public static string ProfilesPath => _profilesPath;

    /// <summary>Total count of bundled profiles.</summary>
    public static int BundledCount => _profiles.Count;
}

/// <summary>Full aircraft performance profile (with any staff overrides applied).</summary>
public sealed record AircraftProfile
{
    public string Icao { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "medium"; // ga, light, medium, heavy, super
    public string Source { get; init; } = "OpenAP";
    public bool IsMapped { get; init; }
    public string MapNote { get; init; } = "";

    // Weights
    public double OewKg { get; init; }
    public double MtowKg { get; init; }
    public double MlwKg { get; init; }
    public double MzfwKg { get; init; }
    public bool MzfwEstimated { get; init; }
    public double MfcKg { get; init; }
    public double MaxCargoKg { get; init; }

    // Capacity
    public int SeatsTypical { get; init; }
    public int NEngines { get; init; }
    public string Engine { get; init; } = "";

    // Standard masses (configurable by staff)
    public double PaxMassKg { get; set; } = 84;    // pax incl. carry-on
    public double BaggageMassKg { get; set; } = 15; // hold baggage per pax

    // Cruise
    public double CruiseMachLo { get; init; }
    public double CruiseMachHi { get; init; }
    public double CruiseMachTyp { get; init; }
    public int CeilingFt { get; init; }

    // Climb (WRAP kinematic)
    public double ClimbCasLoKts { get; init; }
    public double ClimbCasHiKts { get; init; }
    public double ClimbMach { get; init; }
    public double ClimbRateLoFpm { get; init; }
    public double ClimbRateHiFpm { get; init; }

    // Descent (WRAP kinematic)
    public double DescentCasLoKts { get; init; }
    public double DescentCasHiKts { get; init; }
    public double DescentMach { get; init; }
    public double DescentRateLoFpm { get; init; }
    public double DescentRateHiFpm { get; init; }

    // Fuel model
    public double NomCruiseFfKgH { get; init; }
    public double MassExp { get; init; } = 0.75;
    public double AltExp { get; init; } = 0.5;
    public Dictionary<string, double> PhaseFf { get; init; } = new();

    // Staff editing
    public bool IsOverridden { get; init; }
}

/// <summary>Staff-adjustable overrides stored in SQLite.</summary>
public sealed class AircraftOverride
{
    public string Icao { get; set; } = "";
    public double MtowKg { get; set; }
    public double MlwKg { get; set; }
    public double MzfwKg { get; set; }
    public double OewKg { get; set; }
    public double MfcKg { get; set; }
    public int SeatsTypical { get; set; }
    public double PaxMassKg { get; set; }
    public double BaggageMassKg { get; set; }
    public long UpdatedAt { get; set; }
}
