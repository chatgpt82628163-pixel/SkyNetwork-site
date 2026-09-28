namespace SkyNetwork.Site.Data;

internal static class Time
{
    public static DateTime Utc(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
}

public sealed class Member
{
    public long Cid { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Controller rating, OBS…I3.</summary>
    public int Rating { get; set; }
    /// <summary>Staff rank: 0, SUP or ADM.</summary>
    public int StaffRank { get; set; }
    public bool Suspended { get; set; }
    public string? Email { get; set; }
    public string Country { get; set; } = "";
    public long? RegisteredAt { get; set; }
    public long? LastLoginAt { get; set; }
    public string SuspensionReason { get; set; } = "";
    /// <summary>When a temporary suspension ends (unix seconds); null for a permanent one.</summary>
    public long? SuspendedUntil { get; set; }
    public int PilotRating { get; set; }
    public int MilitaryRating { get; set; }
    /// <summary>The email was confirmed by a link sent to it (members without a profile count as confirmed).</summary>
    public bool EmailVerified { get; set; } = true;
    /// <summary>File name of the profile picture in the uploads, empty when there is none.</summary>
    public string Avatar { get; set; } = "";
    public string? AvatarUrl => Avatar.Length > 0 ? "/uploads/" + Avatar : null;

    public string RatingShort => Ratings.Short(Rating);
    public string RatingLong => Ratings.Long(Rating);
    public bool IsStaff => StaffRank > 0;
    /// <summary>Rating as shown to people: the staff rank first when there is one, "SUP · C1".</summary>
    public string DisplayShort => IsStaff ? $"{Ratings.Short(StaffRank)} · {RatingShort}" : RatingShort;
    /// <summary>The highest level the member may connect to the network with.</summary>
    public int NetworkRating => Math.Max(Rating, StaffRank);
    public DateTime? SuspensionEnds => SuspendedUntil is { } u ? Time.Utc(u) : null;
    public DateTime? Registered => RegisteredAt is { } r ? Time.Utc(r) : null;
}

public sealed class FlightPlan
{
    public long Id { get; set; }
    public long Cid { get; set; }
    public string Callsign { get; set; } = "";
    public string Rules { get; set; } = "IFR";
    public string Aircraft { get; set; } = "";
    public int CruiseSpeed { get; set; }
    public string Departure { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Alternate { get; set; } = "";
    public string DepartureTime { get; set; } = "";
    public string CruiseAltitude { get; set; } = "";
    public int EnrouteMinutes { get; set; }
    public int FuelMinutes { get; set; }
    public string Route { get; set; } = "";
    public string Remarks { get; set; } = "";
    /// <summary>Route points with coordinates, JSON [[ident, lat, lon], …] (SimBrief import); empty when filed by hand.</summary>
    public string Waypoints { get; set; } = "";
    public long CreatedAt { get; set; }
    public DateTime Created => Time.Utc(CreatedAt);
}

public sealed class NetworkEvent
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>The English version (optional): the English site shows it when it has a title.</summary>
    public string TitleEn { get; set; } = "";
    public string SummaryEn { get; set; } = "";
    public string BodyEn { get; set; } = "";
    public string Airports { get; set; } = "";
    public long StartsAt { get; set; }
    public long EndsAt { get; set; }
    public bool Published { get; set; }
    /// <summary>Uploaded banner file name ("" for none), served from /uploads/.</summary>
    public string Banner { get; set; } = "";
    public string? BannerUrl => Banner.Length > 0 ? "/uploads/" + Banner : null;
    /// <summary>Banner height on the event's page and the part kept in view when it is cropped (<see cref="BannerLayout"/>).</summary>
    public string BannerSize { get; set; } = "";
    public string BannerFocus { get; set; } = "";
    public string? BannerClasses => BannerLayout.Classes(BannerSize, BannerFocus);
    /// <summary>The whole picture is shown uncropped; on cards it sits over a blurred copy of itself.</summary>
    public bool BannerWhole => BannerLayout.Size(BannerSize) == "full";
    /// <summary>Banner of the English site ("" for none: it shows the Russian one).</summary>
    public string BannerEn { get; set; } = "";
    public string? BannerEnUrl => BannerEn.Length > 0 ? "/uploads/" + BannerEn : null;
    public long CreatedBy { get; set; }
    public DateTime Start => Time.Utc(StartsAt);
    public DateTime End => Time.Utc(EndsAt);

    /// <summary>Whether the English (<paramref name="en"/>) or the Russian site shows the English version: each shows its own when it has a title, else the other.</summary>
    public bool ShowsEnglish(bool en) => en ? TitleEn.Length > 0 : Title.Length == 0 && TitleEn.Length > 0;
    public string TitleIn(bool en) => ShowsEnglish(en) ? TitleEn : Title;
    public string SummaryIn(bool en) => ShowsEnglish(en) ? SummaryEn : Summary;
    public string BodyIn(bool en) => ShowsEnglish(en) ? BodyEn : Body;
    /// <summary>The banner of that site, or the other site's one when it has none.</summary>
    public string? BannerUrlIn(bool en) => en ? BannerEnUrl ?? BannerUrl : BannerUrl ?? BannerEnUrl;
}

public sealed class NewsPost
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>The English version (optional): the English site shows it when it has a headline.</summary>
    public string TitleEn { get; set; } = "";
    public string BodyEn { get; set; } = "";
    public bool Published { get; set; }
    /// <summary>Uploaded banner file name ("" for none), served from /uploads/.</summary>
    public string Banner { get; set; } = "";
    public string? BannerUrl => Banner.Length > 0 ? "/uploads/" + Banner : null;
    /// <summary>Banner height on the post's page and the part kept in view when it is cropped (<see cref="BannerLayout"/>).</summary>
    public string BannerSize { get; set; } = "";
    public string BannerFocus { get; set; } = "";
    public string? BannerClasses => BannerLayout.Classes(BannerSize, BannerFocus);
    /// <summary>The whole picture is shown uncropped; on cards it sits over a blurred copy of itself.</summary>
    public bool BannerWhole => BannerLayout.Size(BannerSize) == "full";
    /// <summary>Banner of the English site ("" for none: it shows the Russian one).</summary>
    public string BannerEn { get; set; } = "";
    public string? BannerEnUrl => BannerEn.Length > 0 ? "/uploads/" + BannerEn : null;
    public long AuthorCid { get; set; }
    public string AuthorName { get; set; } = "";
    public long CreatedAt { get; set; }
    public DateTime Created => Time.Utc(CreatedAt);

    /// <summary>Whether the English (<paramref name="en"/>) or the Russian site shows the English version: each shows its own when it has a headline, else the other.</summary>
    public bool ShowsEnglish(bool en) => en ? TitleEn.Length > 0 : Title.Length == 0 && TitleEn.Length > 0;
    public string TitleIn(bool en) => ShowsEnglish(en) ? TitleEn : Title;
    public string BodyIn(bool en) => ShowsEnglish(en) ? BodyEn : Body;
    /// <summary>The banner of that site, or the other site's one when it has none.</summary>
    public string? BannerUrlIn(bool en) => en ? BannerEnUrl ?? BannerUrl : BannerUrl ?? BannerEnUrl;

    /// <summary>The start of the text for lists.</summary>
    public string ExcerptIn(bool en, int length)
    {
        string text = BodyIn(en);
        return text.Length > length ? text[..length] + "…" : text;
    }
}

public sealed class Booking
{
    public long Id { get; set; }
    public long Cid { get; set; }
    public string Name { get; set; } = "";
    public int Rating { get; set; }
    public string Callsign { get; set; } = "";
    public long StartsAt { get; set; }
    public long EndsAt { get; set; }
    public DateTime Start => Time.Utc(StartsAt);
    public DateTime End => Time.Utc(EndsAt);
}

public sealed class NetworkSession
{
    public long Id { get; set; }
    public long Cid { get; set; }
    public string Callsign { get; set; } = "";
    /// <summary>"pilot" or "atc".</summary>
    public string Kind { get; set; } = "";
    public string Details { get; set; } = "";
    public long StartedAt { get; set; }
    public long? EndedAt { get; set; }
    public DateTime Start => Time.Utc(StartedAt);
    public TimeSpan Duration => TimeSpan.FromSeconds((EndedAt ?? Database.Now()) - StartedAt);
}

public sealed class Ticket
{
    public long Id { get; set; }
    public long? Cid { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Status { get; set; } = "open";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public DateTime Updated => Time.Utc(UpdatedAt);
}

public sealed class TicketMessage
{
    public long Id { get; set; }
    public long TicketId { get; set; }
    public long? Cid { get; set; }
    public string Name { get; set; } = "";
    public bool Staff { get; set; }
    public string Body { get; set; } = "";
    public long CreatedAt { get; set; }
    public DateTime Created => Time.Utc(CreatedAt);
}

public sealed class StaffNote
{
    public long Id { get; set; }
    public long Cid { get; set; }
    public long AuthorCid { get; set; }
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = "";
    public long CreatedAt { get; set; }
    public DateTime Created => Time.Utc(CreatedAt);
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public long ActorCid { get; set; }
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Details { get; set; } = "";
    public long CreatedAt { get; set; }
    public DateTime Created => Time.Utc(CreatedAt);
}

/// <summary>Top-N entries for the logbook summary: aircraft types, routes, and controller positions.</summary>
public sealed record LogbookTopStats(
    IReadOnlyList<(string Key, int Count)> Aircraft,
    IReadOnlyList<(string Key, int Count)> Routes,
    IReadOnlyList<(string Key, int Count)> Positions);
