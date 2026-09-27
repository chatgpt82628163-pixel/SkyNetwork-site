using System.Text.Json;

namespace SkyNetwork.Site.Localization;

/// <summary>Texts the map script shows (wwwroot/js/map.js), handed to it translated in the map's data-text attribute.</summary>
public static class MapTexts
{
    public static readonly string[] Keys =
    [
        "Updated", "Server not responding", "Offline", "Close", "{0} min", "{0} h {1} min",
        "On the ground", "Departing", "Climbing", "Cruising", "Descending", "Arriving", "Arrived",
        "No flight plan filed", "Departed", "Planned", "Time online", "ETA", "Distance flown", "Remaining",
        "Altitude", "Ground speed", "Heading", "Squawk", "Vertical speed", "Next point",
        "Speed & altitude graph", "Not enough data yet", "Flight plan", "Aircraft type", "Cruise TAS", "Cruise altitude", "Aircraft registration",
        "Alternate", "Route distance", "Departure", "En route", "Fuel", "Step climbs", "Route", "Remarks",
        "Route from SimBrief (AIRAC {0})", "Route worked out from the flight plan", "Not found in the database: {0}",
        "Center on aircraft", "Follow", "Following", "Share link", "Link copied",
        "Online for {0}", "Online for", "Frequency", "Rating", "Sector:", "Airport:",
        "none", "no data", "Loading…", "Controllers", "nobody", "Departures", "Arrivals", "Not found: {0}", "Controller info",
        "Add to friends", "Remove from friends", "Sign in to add friends", "Friends online",
    ];

    public static string Json(Lang l) => JsonSerializer.Serialize(Keys.ToDictionary(k => k, k => l[k]));
}
