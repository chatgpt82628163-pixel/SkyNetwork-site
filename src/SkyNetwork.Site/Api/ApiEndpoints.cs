using SkyNetwork.Site.Data;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Api;

/// <summary>
/// Public JSON API (read-only, CORS open) and the flight plan endpoint SkyPilot uses.
/// Documented on /developers.
/// </summary>
public static class ApiEndpoints
{
    public static void MapSiteApi(this WebApplication app)
    {
        // SkyPilot (see skypilot docs/website-api.md).
        app.MapGet("/api/flightplans/latest", (long cid, FlightPlanService plans) =>
            plans.Latest(cid) is { } p ? Results.Ok(PlanDto(p)) : Results.NotFound()).RequireCors("api");

        // Uploaded banners: random names, never overwritten, so cached for a year.
        app.MapGet("/uploads/{name}", (string name, UploadStore uploads, HttpContext ctx) =>
        {
            if (uploads.PathOf(name) is not { } path) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(path, UploadStore.ContentType(name));
        });

        app.MapGet("/tiles/{layer}/{z:int}/{x:int}/{y:int}.png", async (string layer, int z, int x, int y, TileProxy tiles, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=604800";
            return await tiles.GetAsync(layer, z, x, y, ctx.RequestAborted);
        });

        var v1 = app.MapGroup("/api/v1").RequireCors("api");

        // SkyPilot checks the account here, so the client asks only for CID and password and learns the name.
        // Same attempt limit as the login page. 401 wrong CID/password, 403 suspended.
        v1.MapPost("/auth/pilot", (PilotLogin login, MemberService members) =>
        {
            string password = login.Password ?? "";
            if (login.Cid <= 0 || password.Length == 0) return Results.Unauthorized();
            if (members.Authenticate(login.Cid, password) is { } m)
                return Results.Ok(new { cid = m.Cid, name = m.Name, rating = m.Rating, ratingName = Ratings.Short(m.Rating) });
            return members.PasswordMatches(login.Cid, password) ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Unauthorized();
        }).RequireRateLimiting("auth");

        // For the map: the route as points (SimBrief when the pilot uses it, otherwise worked out from the route text)
        // and the track flown so far.
        v1.MapGet("/pilots/{callsign}/route", async (string callsign, NetworkFeed feed, FlightPlanService plans, Simbrief simbrief, NavData nav, CancellationToken ct) =>
        {
            var p = feed.Current.Pilots.FirstOrDefault(x => x.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
            if (p == null) return Results.NotFound();
            string? source = null;
            StoredRoute? stored = null;
            List<RoutePoint>? points = null;
            List<string> unresolved = [];
            if (p.FlightPlan is { } fp)
            {
                // A plan imported here, otherwise the pilot's latest SimBrief plan for the same flight.
                stored = StoredRoute.Parse(plans.Waypoints(p.Cid, fp.Departure, fp.Destination)
                    ?? await simbrief.RouteForAsync(plans.SimbriefUser(p.Cid), fp.Departure, fp.Destination, ct));
                if (stored != null) { source = "simbrief"; points = stored.Points; }
                else
                {
                    (points, unresolved) = nav.Decode(fp.Departure, fp.Destination, fp.Route);
                    source = points.Count > 1 ? "route" : null;
                }
            }
            return Results.Ok(new
            {
                source,
                waypoints = source != null ? points!.Select(w => new object[] { w.Ident, Math.Round(w.Lat, 4), Math.Round(w.Lon, 4), w.Airway, w.Altitude }) : null,
                unresolved,
                extras = stored?.Extras(),
                track = feed.Track(p.Callsign).Select(t => new object[] { Math.Round(t.Latitude, 4), Math.Round(t.Longitude, 4), t.Altitude, t.Groundspeed, t.Time }),
            });
        });

        // A route text as points, for anyone building on the API (SkyPilot, event pages).
        v1.MapGet("/routes/decode", (string? departure, string? destination, string? route, NavData nav) =>
        {
            if (departure is not { Length: 4 } || destination is not { Length: 4 } || route is not { Length: <= 2000 }) return Results.BadRequest();
            var (points, unresolved) = nav.Decode(departure, destination, route);
            return Results.Ok(new { waypoints = points.Select(w => new object[] { w.Ident, Math.Round(w.Lat, 4), Math.Round(w.Lon, 4), w.Airway }), unresolved });
        });

        // Flight planner: suggest a route, cruise level and fuel estimate.
        v1.MapGet("/routes/suggest", (string? departure, string? destination, string? type, string? alternate, NavData nav) =>
        {
            if (departure is not { Length: >= 3 and <= 4 } || destination is not { Length: >= 3 and <= 4 })
                return Results.BadRequest(new { error = "departure and destination must be 3–4 character ICAO codes" });
            string dep = departure.ToUpperInvariant();
            string dest = destination.ToUpperInvariant();
            string acType = (type ?? "A320").Trim().ToUpperInvariant();
            if (acType.Length is 0 or > 8) return Results.BadRequest(new { error = "invalid type" });

            var found = nav.FindRoute(dep, dest);
            var perf = AircraftPerf.Get(acType);

            // Get airport positions from the route points for bearing calculation
            double depLat = found.Points.Count > 0 ? found.Points[0].Lat : 0;
            double depLon = found.Points.Count > 0 ? found.Points[0].Lon : 0;
            double destLat = found.Points.Count > 0 ? found.Points[^1].Lat : 0;
            double destLon = found.Points.Count > 0 ? found.Points[^1].Lon : 0;

            var est = AircraftPerf.Estimate(found.DistanceNm, perf, alternate, depLat, depLon, destLat, destLon);
            int eteH = est.EteMinutes / 60, eteM = est.EteMinutes % 60;
            return Results.Ok(new
            {
                route = found.Route,
                distanceNm = (int)Math.Round(found.DistanceNm),
                points = found.Points.Select(p => new { name = p.Ident, lat = Math.Round(p.Lat, 4), lon = Math.Round(p.Lon, 4) }),
                cruiseLevel = est.CruiseLevelStr,
                cruiseSpeedKt = est.CruiseSpeedKt,
                ete = $"{eteH}:{eteM:D2}",
                fuel = new
                {
                    trip = est.TripKg,
                    contingency = est.ContingencyKg,
                    reserve = est.FinalReserveKg,
                    alternate = est.AlternateKg,
                    total = est.TotalKg,
                    unit = "kg",
                },
                notes = "Fuel and time are estimates only — not for real-world use.",
            });
        });

        v1.MapGet("/airports/{icao}/layout", async (string icao, AirportLayout layouts, HttpContext ctx) =>
        {
            string? json = await layouts.GetAsync(icao, ctx.RequestAborted);
            if (json == null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Text(json, "application/json");
        });

        v1.MapGet("/metar/{icao}", async (string icao, MetarService metar, CancellationToken ct) =>
            await metar.GetAsync(icao, ct) is { } text ? Results.Ok(new { icao = icao.ToUpperInvariant(), metar = text }) : Results.NotFound());

        v1.MapGet("/status", (Microsoft.Extensions.Options.IOptions<SiteOptions> o, NetworkFeed feed) => new
        {
            network = o.Value.Name,
            fsd = new { host = o.Value.FsdHost, port = o.Value.FsdPort },
            feedAvailable = feed.Current.Available,
            feedUpdated = feed.Current.Available ? feed.Current.Updated : (DateTime?)null,
            api = "v1",
        });

        v1.MapGet("/online", (NetworkFeed feed) =>
        {
            var s = feed.Current;
            return new
            {
                updated = s.Available ? s.Updated : (DateTime?)null,
                available = s.Available,
                pilots = s.Pilots.Select(p => new
                {
                    p.Cid, p.Name, p.Callsign, p.Latitude, p.Longitude, p.Altitude, p.Groundspeed, p.Heading, p.Transponder,
                    onGround = p.OnGround, logonTime = p.LogonTime, flightPlan = p.FlightPlan,
                }),
                controllers = s.Controllers.Select(c => new
                {
                    c.Cid, c.Name, c.Callsign, c.Rating, c.Frequency, facility = c.FacilityName, c.VisualRange, c.Latitude, c.Longitude,
                    logonTime = c.LogonTime, textAtis = c.Text, atisCode = c.AtisCode,
                }),
            };
        });

        v1.MapGet("/stats", (MemberService members, SessionService sessions, NetworkFeed feed) =>
        {
            var (today, month) = sessions.SessionCounts();
            return new
            {
                members = members.Count(),
                pilotsOnline = feed.Current.Pilots.Count,
                // ATIS stations and observers are connected but are not controllers on a position.
                controllersOnline = feed.Current.OnPosition.Count,
                atisOnline = feed.Current.Atis.Count,
                sessionsToday = today,
                sessionsLast30Days = month,
            };
        });

        v1.MapGet("/members/{cid:long}", (long cid, MemberService members, SessionService sessions) =>
        {
            // A registration whose email is not confirmed does not count: not shown to anyone.
            var m = members.FindConfirmed(cid);
            if (m == null) return Results.NotFound();
            var h = sessions.Hours(cid);
            return Results.Ok(new
            {
                m.Cid, m.Name, rating = m.RatingShort, ratingName = m.RatingLong,
                staffRank = m.IsStaff ? Ratings.Short(m.StaffRank) : null, staffRankName = m.IsStaff ? Ratings.Long(m.StaffRank) : null,
                pilotRating = PilotRatings.Pilot.Short(m.PilotRating), pilotRatingName = PilotRatings.Pilot.Long(m.PilotRating),
                militaryRating = PilotRatings.Military.Short(m.MilitaryRating), militaryRatingName = PilotRatings.Military.Long(m.MilitaryRating),
                registered = m.Registered,
                pilotHours = Math.Round(h.PilotHours, 1), atcHours = Math.Round(h.AtcHours, 1), suspended = m.Suspended,
                avatar = m.AvatarUrl,
            });
        });

        // A member's profile picture at a fixed address, for other sites (SkyRUS): a redirect to the file,
        // 404 when there is none. Short cache: a new picture shows within minutes.
        v1.MapGet("/members/{cid:long}/avatar", (long cid, MemberService members, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            return members.FindConfirmed(cid) is { AvatarUrl: { } url } ? Results.Redirect(url) : Results.NotFound();
        });

        v1.MapGet("/members/{cid:long}/sessions", (long cid, SessionService sessions) =>
            sessions.Recent(cid, 50).Select(s => new
            {
                s.Callsign, s.Kind, s.Details, start = s.Start, end = s.EndedAt is { } e ? Time.Utc(e) : (DateTime?)null,
                minutes = (int)s.Duration.TotalMinutes,
            }));

        // Events and news in Russian, or with ?lang=en in English where they have an English version.
        v1.MapGet("/events", (string? lang, ContentService content) => content.UpcomingEvents(50).Select(e => EventDto(e, lang == "en")));
        v1.MapGet("/events/{id:long}", (long id, string? lang, ContentService content) =>
            content.Event(id) is { Published: true } e ? Results.Ok(EventDto(e, lang == "en")) : Results.NotFound());

        v1.MapGet("/bookings", (ContentService content) => content.Bookings().Select(b => new
        {
            b.Id, b.Callsign, b.Cid, b.Name, rating = Ratings.Short(b.Rating), start = b.Start, end = b.End,
        }));

        v1.MapGet("/news", (string? lang, ContentService content) => content.News(20).Select(n => new
        {
            n.Id, title = n.TitleIn(lang == "en"), body = n.BodyIn(lang == "en"), author = n.AuthorName, created = n.Created,
            banner = n.BannerUrlIn(lang == "en"),
        }));
    }

    /// <summary>Body of POST /api/v1/auth/pilot.</summary>
    public sealed record PilotLogin(long Cid, string? Password);

    public static object PlanDto(FlightPlan p) => new
    {
        rules = p.Rules,
        aircraft = p.Aircraft,
        cruiseSpeed = p.CruiseSpeed,
        departure = p.Departure,
        destination = p.Destination,
        alternate = p.Alternate,
        departureTime = p.DepartureTime,
        cruiseAltitude = p.CruiseAltitude,
        enrouteMinutes = p.EnrouteMinutes,
        fuelMinutes = p.FuelMinutes,
        route = p.Route,
        remarks = p.Remarks,
        callsign = p.Callsign,
        filed = p.Created,
    };

    private static object EventDto(NetworkEvent e, bool en) => new
    {
        e.Id, title = e.TitleIn(en), summary = e.SummaryIn(en), body = e.BodyIn(en),
        airports = e.Airports.Split(' ', StringSplitOptions.RemoveEmptyEntries), start = e.Start, end = e.End, banner = e.BannerUrlIn(en),
    };
}
