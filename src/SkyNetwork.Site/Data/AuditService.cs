using Dapper;

namespace SkyNetwork.Site.Data;

/// <summary>Every staff action is written here and shown in the staff area.</summary>
public sealed class AuditService(Database db)
{
    public void Log(long actor, string action, string target = "", string details = "")
    {
        using var c = db.Open();
        c.Execute("INSERT INTO audit_log (actor_cid, action, target, details, created_at) VALUES (@actor, @action, @target, @details, @now)",
            new { actor, action, target, details, now = Database.Now() });
    }

    public IReadOnlyList<AuditEntry> Recent(int limit = 200, string? target = null)
    {
        using var c = db.Open();
        return c.Query<AuditEntry>("""
            SELECT a.*, COALESCE(m.name, '') AS actor_name FROM audit_log a LEFT JOIN members m ON m.cid = a.actor_cid
            WHERE @target IS NULL OR a.target = @target
            ORDER BY a.id DESC LIMIT @limit
            """, new { limit, target }).ToList();
    }

    /// <summary>English title of an action; pages translate it.</summary>
    public static string Title(string action) => action switch
    {
        "rating" => "Rating",
        "staff-rank" => "Staff rank",
        "name" => "Name",
        "avatar" => "Profile picture",
        "pilot-rating" => "Pilot rating",
        "military-rating" => "Military rating",
        "suspend" => "Suspension",
        "unsuspend" => "Suspension lifted",
        "password-reset" => "Password reset",
        "roles" => "Roles",
        "note" => "Note",
        "event" => "Event",
        "event-delete" => "Event deleted",
        "news" => "News",
        "news-delete" => "News deleted",
        "booking-delete" => "Booking deleted",
        "ticket" => "Support ticket",
        "division-key" => "Division API key",
        "connect-client" => "Sign-in site",
        "rating-request" => "Rating request",
        _ => action,
    };
}
