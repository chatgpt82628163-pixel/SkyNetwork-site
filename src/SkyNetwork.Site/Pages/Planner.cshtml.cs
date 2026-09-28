using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Pages;

/// <summary>
/// Flight planner studio. The page is the frame (map, panels, fields); the plan itself comes from
/// /api/v1/planner/plan and is drawn by wwwroot/js/planner.js.
/// </summary>
public sealed class PlannerModel(AircraftService aircraft) : PageModel
{
    /// <summary>The types in the aircraft list, as "A320 — Airbus A320-200", by ICAO code.</summary>
    public IReadOnlyList<AircraftProfile> Types { get; private set; } = [];

    public void OnGet() => Types = aircraft.All();
}
