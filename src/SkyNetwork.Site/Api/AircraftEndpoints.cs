using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Api;

public static class AircraftEndpoints
{
    public static void MapAircraftApi(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1").RequireCors("api");

        // GET /api/v1/aircraft — list: code, name, category
        v1.MapGet("/aircraft", (AircraftService aircraft) =>
            Results.Ok(aircraft.All().Select(p => new
            {
                code = p.Icao,
                name = p.Name,
                category = p.Category,
            })));

        // GET /api/v1/aircraft/{code} — full profile with estimated/override flags
        v1.MapGet("/aircraft/{code}", (string code, AircraftService aircraft) =>
        {
            var p = aircraft.Get(code);
            if (p == null) return Results.NotFound();

            return Results.Ok(new
            {
                code = p.Icao,
                name = p.Name,
                category = p.Category,
                source = p.Source,
                is_mapped = p.IsMapped,
                map_note = p.MapNote.Length > 0 ? p.MapNote : null,
                is_overridden = p.IsOverridden,

                weights = new
                {
                    oew_kg = p.OewKg,
                    mtow_kg = p.MtowKg,
                    mlw_kg = p.MlwKg,
                    mzfw_kg = p.MzfwKg,
                    mzfw_estimated = p.MzfwEstimated,
                    mfc_kg = p.MfcKg,
                    max_cargo_kg = p.MaxCargoKg,
                },

                capacity = new
                {
                    seats_typical = p.SeatsTypical,
                    n_engines = p.NEngines,
                    engine = p.Engine,
                    pax_mass_kg = p.PaxMassKg,
                    baggage_mass_kg = p.BaggageMassKg,
                },

                cruise = new
                {
                    mach_lo = p.CruiseMachLo,
                    mach_hi = p.CruiseMachHi,
                    mach_typ = p.CruiseMachTyp,
                    ceiling_ft = p.CeilingFt,
                },

                climb = new
                {
                    cas_lo_kts = p.ClimbCasLoKts,
                    cas_hi_kts = p.ClimbCasHiKts,
                    mach = p.ClimbMach,
                    rate_lo_fpm = p.ClimbRateLoFpm,
                    rate_hi_fpm = p.ClimbRateHiFpm,
                },

                descent = new
                {
                    cas_lo_kts = p.DescentCasLoKts,
                    cas_hi_kts = p.DescentCasHiKts,
                    mach = p.DescentMach,
                    rate_lo_fpm = p.DescentRateLoFpm,
                    rate_hi_fpm = p.DescentRateHiFpm,
                },

                fuel_model = new
                {
                    nom_cruise_ff_kg_h = p.NomCruiseFfKgH,
                    ref_alt_ft = 35000,
                    ref_mass_fraction = 0.80,
                    mass_exp = p.MassExp,
                    alt_exp = p.AltExp,
                    phase_factors = p.PhaseFf,
                },
            });
        });
    }
}
