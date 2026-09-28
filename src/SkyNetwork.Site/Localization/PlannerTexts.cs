using System.Text.Json;

namespace SkyNetwork.Site.Localization;

/// <summary>Texts the planner script shows (wwwroot/js/planner.js), handed to it translated in the page's data-text attribute.</summary>
public static class PlannerTexts
{
    public static readonly string[] Keys =
    [
        "kt", "ft", "Departure", "Destination", "Alternate", "Click to take as the alternate",
        "Too many plans, wait a minute", "The plan could not be made", "{0} routes", "Block", "Distance", "Passengers",
        "no METAR", "no TAF", "Wind variable {0} kt", "Wind {0}° {1} kt", "headwind {0}", "tailwind {0}", "crosswind {0} {1}",
        "from the right", "from the left", "Take-off", "Landing",
        "No runway data for {0} yet: OpenStreetMap is being asked, try again in a minute.", "{0} m, true heading {1}°", "Runways",
        "Block fuel", "Trip", "Reserves", "Air time", "Tanks: {0} of {1}", "Empty aircraft", "Payload", "Zero fuel",
        "Basic", "Variant {0}", "Copied", "Link copied",
        "Fix", "Airway", "Crs", "Leg", "Dist", "To go", "Level", "Wind", "Leg time", "Time", "Leg fuel", "Used", "Remaining",
        "Item", "Taxi", "15 min", "Contingency", "5 % of trip", "5 min holding (more than 5 %)", "none", "Final reserve",
        "30 min holding at 1500 ft", "Minimum take-off fuel", "Extra", "at the captain's discretion", "taxi included",
        "Landing fuel", "at {0}",
        "Flight", "Callsign", "Aircraft type", "Date", "Off block", "On block", "Block time", "Plan", "Cruise level", "Cost index",
        "Cruise speed", "Route distance", "Great circle", "Average wind", "Wind component", "ISA deviation", "Units",
        "Winds aloft", "none (calm, ISA)", "Load sheet", "Trip fuel", "Empty weight", "Baggage", "Cargo",
        "For the simulator only. Not for real-world navigation.", "Warnings",
    ];

    public static string Json(Lang l) => JsonSerializer.Serialize(Keys.ToDictionary(k => k, k => l[k]));
}
