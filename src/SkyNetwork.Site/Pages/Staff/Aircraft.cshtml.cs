using Microsoft.AspNetCore.Mvc;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Pages.Staff;

/// <summary>View and override aircraft performance profiles (administrators only).</summary>
public sealed class AircraftModel(CurrentUser me, AircraftService aircraft) : StaffPageModel(me)
{
    protected override Perm Required => Perm.ManageAircraft;

    public IReadOnlyList<AircraftProfile> Profiles { get; private set; } = [];
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() => Profiles = aircraft.All();

    public IActionResult OnPostSave(
        string? icao, double? mtow, double? mlw, double? mzfw, double? oew,
        double? mfc, int? seats, double? pax_mass, double? bag_mass)
    {
        if (icao is not { Length: >= 2 and <= 6 }) return BadRequest();
        var ov = new AircraftOverride
        {
            Icao = icao.ToUpperInvariant(),
            MtowKg = mtow ?? 0,
            MlwKg = mlw ?? 0,
            MzfwKg = mzfw ?? 0,
            OewKg = oew ?? 0,
            MfcKg = mfc ?? 0,
            SeatsTypical = seats ?? 0,
            PaxMassKg = pax_mass ?? 0,
            BaggageMassKg = bag_mass ?? 0,
        };
        aircraft.SaveOverride(ov);
        Message = "Overrides saved";
        OnGet();
        return Page();
    }

    public IActionResult OnPostReset(string? icao)
    {
        if (icao is not { Length: >= 2 and <= 6 }) return BadRequest();
        aircraft.DeleteOverride(icao);
        Message = "Reset to OpenAP defaults";
        OnGet();
        return Page();
    }
}
