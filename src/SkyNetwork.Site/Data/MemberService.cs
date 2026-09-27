using Dapper;
using Microsoft.Extensions.Options;
using SkyNetwork.Site.Security;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Data;

public sealed class MemberService(Database db, IOptions<SiteOptions> options, AuditService audit, Notifications notify)
{
    private const string Select = """
        SELECT m.cid, m.name, m.rating, m.staff_rank, m.suspended, p.email, COALESCE(p.country, '') AS country,
               p.registered_at, p.last_login_at, COALESCE(p.suspension_reason, '') AS suspension_reason,
               p.suspended_until, COALESCE(p.pilot_rating, 0) AS pilot_rating, COALESCE(p.military_rating, 0) AS military_rating,
               COALESCE(p.email_verified, 1) AS email_verified, COALESCE(p.avatar, '') AS avatar
        FROM members m LEFT JOIN member_profiles p ON p.cid = m.cid
        """;

    public Member? Find(long cid)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Member>(Select + " WHERE m.cid = @cid", new { cid });
    }

    /// <summary>A member others may see (public profile, API): a registration whose email is not confirmed does not count.</summary>
    public Member? FindConfirmed(long cid) => Find(cid) is { EmailVerified: true } m ? m : null;

    public bool EmailTaken(string email)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM member_profiles WHERE email = @email COLLATE NOCASE", new { email }) > 0;
    }

    /// <summary>
    /// Creates a member with the next CID never given before (a deleted member's CID is not reused) and returns it;
    /// <paramref name="verified"/> false until the email is confirmed.
    /// </summary>
    public long Register(string name, string email, string country, string password, bool verified = true)
    {
        var (salt, hash) = PasswordHasher.Hash(password);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        long last = Math.Max(c.ExecuteScalar<long?>("SELECT value FROM site_counters WHERE name = 'last_cid'", transaction: tx) ?? 0,
                             c.ExecuteScalar<long?>("SELECT MAX(cid) FROM members", transaction: tx) ?? 0);
        long cid = Math.Max(options.Value.FirstCid, last + 1);
        c.Execute("INSERT INTO members (cid, name, rating, salt, hash) VALUES (@cid, @name, 1, @salt, @hash)",
            new { cid, name, salt, hash }, tx);
        c.Execute("INSERT INTO member_profiles (cid, email, country, registered_at, email_verified) VALUES (@cid, @email, @country, @now, @v)",
            new { cid, email, country, now = Database.Now(), v = verified ? 1 : 0 }, tx);
        RememberCid(c, tx, cid);
        tx.Commit();
        return cid;
    }

    private static void RememberCid(System.Data.IDbConnection c, System.Data.IDbTransaction tx, long cid) =>
        c.Execute("""
            INSERT INTO site_counters (name, value) VALUES ('last_cid', @cid)
            ON CONFLICT(name) DO UPDATE SET value = MAX(value, @cid)
            """, new { cid }, tx);

    /// <summary>
    /// Deletes a member and everything the site keeps about them: profile, flight plans, flights and hours, bookings,
    /// support tickets, sign-ins to other sites, rating requests and team notes. Returns the profile picture's file
    /// name for the caller to delete (null when there is no such member). The deletion stays in the audit log; the
    /// CID is never given to anyone else.
    /// </summary>
    public string? Delete(long actor, long cid, string reason, string action = "account-delete")
    {
        if (Find(cid) is not { } m) return null;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var p = new { cid };
        c.Execute("DELETE FROM ticket_messages WHERE ticket_id IN (SELECT id FROM tickets WHERE cid = @cid)", p, tx);
        foreach (var table in new[]
                 {
                     "tickets", "email_tokens", "oauth_codes", "oauth_tokens", "oauth_consents", "bookings", "flight_plans",
                     "network_sessions", "rating_requests", "staff_roles", "staff_notes", "member_profiles", "members",
                 })
            c.Execute($"DELETE FROM {table} WHERE cid = @cid", p, tx);
        RememberCid(c, tx, cid);
        tx.Commit();
        audit.Log(actor, action, cid.ToString(), string.IsNullOrWhiteSpace(reason) ? m.Name : $"{m.Name}: {reason.Trim()}");
        return m.Avatar;
    }

    /// <summary>
    /// Registrations whose email was not confirmed in time do not count: registered at least <paramref name="after"/>
    /// ago and no confirmation link still works, they are deleted (never staff, never a member with a rating).
    /// Returns the profile pictures of those deleted, for the caller to delete.
    /// </summary>
    public IReadOnlyList<(long Cid, string Avatar)> DeleteUnconfirmed(TimeSpan after)
    {
        long now = Database.Now();
        using var c = db.Open();
        var due = c.Query<long>("""
            SELECT m.cid FROM members m JOIN member_profiles p ON p.cid = m.cid
            WHERE p.email_verified = 0 AND m.staff_rank = 0 AND m.rating <= 1 AND p.registered_at <= @before
              AND NOT EXISTS (SELECT 1 FROM email_tokens t
                              WHERE t.cid = m.cid AND t.purpose = 'verify' AND t.used = 0 AND t.expires_at > @now)
            """, new { before = now - (long)after.TotalSeconds, now }).ToList();
        return due.Select(cid => (cid, Delete(0, cid, "email not confirmed in time", "account-expired") ?? "")).ToList();
    }

    /// <summary>Checks CID and password like the FSD server does; suspended members are refused.</summary>
    public Member? Authenticate(long cid, string password)
    {
        using var c = db.Open();
        var row = c.QuerySingleOrDefault<Credentials>("SELECT salt, hash, suspended FROM members WHERE cid = @cid", new { cid });
        if (row == null || row.Suspended != 0 || !PasswordHasher.Verify(password, row.Salt, row.Hash)) return null;
        // Members created with skynet-admin have no profile row yet.
        c.Execute("""
            INSERT INTO member_profiles (cid, registered_at, last_login_at) VALUES (@cid, @now, @now)
            ON CONFLICT(cid) DO UPDATE SET last_login_at = @now
            """, new { cid, now = Database.Now() });
        return Find(cid);
    }

    private sealed class Credentials
    {
        public byte[] Salt { get; set; } = [];
        public byte[] Hash { get; set; } = [];
        public long Suspended { get; set; }
    }

    /// <summary>Password check only (ignores suspension), to tell a suspended member why they cannot log in.</summary>
    public bool PasswordMatches(long cid, string password)
    {
        using var c = db.Open();
        var row = c.QuerySingleOrDefault<Credentials>("SELECT salt, hash, suspended FROM members WHERE cid = @cid", new { cid });
        return row != null && PasswordHasher.Verify(password, row.Salt, row.Hash);
    }

    public bool IsSuspended(long cid)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT suspended FROM members WHERE cid = @cid", new { cid }) != 0;
    }

    public void ChangePassword(long cid, string password)
    {
        var (salt, hash) = PasswordHasher.Hash(password);
        using var c = db.Open();
        c.Execute("UPDATE members SET salt = @salt, hash = @hash WHERE cid = @cid", new { cid, salt, hash });
    }

    public void UpdateProfile(long cid, string email, string country)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO member_profiles (cid, email, country, registered_at) VALUES (@cid, @email, @country, @now)
            ON CONFLICT(cid) DO UPDATE SET email = @email, country = @country
            """, new { cid, email, country, now = Database.Now() });
    }

    /// <summary>Sets the profile picture (empty removes it); returns the previous file name, for the caller to delete.</summary>
    public string SetAvatar(long cid, string avatar)
    {
        using var c = db.Open();
        var old = c.ExecuteScalar<string>("SELECT avatar FROM member_profiles WHERE cid = @cid", new { cid }) ?? "";
        c.Execute("""
            INSERT INTO member_profiles (cid, avatar, registered_at) VALUES (@cid, @avatar, @now)
            ON CONFLICT(cid) DO UPDATE SET avatar = @avatar
            """, new { cid, avatar, now = Database.Now() });
        return old;
    }

    /// <summary>The address is confirmed (for an email change it becomes the member's address).</summary>
    public void ConfirmEmail(long cid, string email)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO member_profiles (cid, email, registered_at, email_verified) VALUES (@cid, @email, @now, 1)
            ON CONFLICT(cid) DO UPDATE SET email = @email, email_verified = 1
            """, new { cid, email, now = Database.Now() });
    }

    public void UpdateCountry(long cid, string country)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO member_profiles (cid, country, registered_at) VALUES (@cid, @country, @now)
            ON CONFLICT(cid) DO UPDATE SET country = @country
            """, new { cid, country, now = Database.Now() });
    }

    /// <summary>A member by CID or by email (for "forgot password").</summary>
    public Member? FindByCidOrEmail(string text)
    {
        text = text.Trim();
        if (long.TryParse(text, out var cid)) return Find(cid);
        using var c = db.Open();
        return c.QuerySingleOrDefault<Member>(Select + " WHERE p.email = @text COLLATE NOCASE", new { text });
    }

    public IReadOnlyList<string> RolesOf(long cid)
    {
        using var c = db.Open();
        // Roles no longer in use (e.g. the old "training" role) are ignored.
        return c.Query<string>("SELECT role FROM staff_roles WHERE cid = @cid ORDER BY role", new { cid })
            .Where(Permissions.Roles.ContainsKey).ToList();
    }

    /// <param name="filter">"" everyone, "suspended" or "unconfirmed" (the email is not confirmed yet).</param>
    public IReadOnlyList<Member> Search(string? query, string filter = "", int limit = 100)
    {
        using var c = db.Open();
        query = (query ?? "").Trim();
        string where = filter switch
        {
            "suspended" => " AND m.suspended = 1",
            "unconfirmed" => " AND COALESCE(p.email_verified, 1) = 0",
            _ => "",
        };
        if (query.Length == 0)
            return c.Query<Member>(Select + " WHERE 1 = 1" + where + " ORDER BY m.cid DESC LIMIT @limit", new { limit }).ToList();
        return c.Query<Member>(Select + """
             WHERE (CAST(m.cid AS TEXT) = @query OR m.name LIKE @like OR p.email LIKE @like)
            """ + where + " ORDER BY m.cid DESC LIMIT @limit", new { query, like = "%" + query + "%", limit }).ToList();
    }

    public IReadOnlyList<Member> Staff()
    {
        using var c = db.Open();
        return c.Query<Member>(Select + """
             WHERE m.staff_rank > 0 OR m.rating >= 8 OR m.cid IN (SELECT cid FROM staff_roles)
             ORDER BY m.staff_rank DESC, m.rating DESC, m.cid
            """).ToList();
    }

    /// <summary>Members, not counting registrations whose email is not confirmed yet.</summary>
    public long Count()
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM members m LEFT JOIN member_profiles p ON p.cid = m.cid WHERE COALESCE(p.email_verified, 1) = 1");
    }

    /// <summary>Registrations waiting for the email to be confirmed.</summary>
    public long CountUnconfirmed()
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM member_profiles WHERE email_verified = 0");
    }

    // ---- staff actions (always audited) ----------------------------------------------------------

    /// <summary>Controller rating (OBS…I3); staff ranks go through <see cref="SetStaffRank"/>.</summary>
    public void SetRating(long actor, long cid, int rating)
    {
        if (!Ratings.IsController(rating)) throw new ArgumentOutOfRangeException(nameof(rating));
        using var c = db.Open();
        int old = c.ExecuteScalar<int>("SELECT rating FROM members WHERE cid = @cid", new { cid });
        c.Execute("UPDATE members SET rating = @rating WHERE cid = @cid", new { cid, rating });
        audit.Log(actor, "rating", cid.ToString(), $"{Ratings.Short(old)} → {Ratings.Short(rating)}");
        if (old != rating) notify.RatingChanged(cid, "atc", Ratings.Short(old), Ratings.Short(rating), rating > old);
    }

    /// <summary>
    /// Suspends (for <paramref name="days"/>, or for good when null) or lifts a suspension. The FSD
    /// server reads the same flag: it refuses the login and disconnects a member already online.
    /// </summary>
    public void SetSuspended(long actor, long cid, bool suspended, string reason, int? days = null)
    {
        long now = Database.Now();
        long? until = suspended && days is > 0 ? now + days.Value * 86400L : null;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("UPDATE members SET suspended = @s WHERE cid = @cid", new { cid, s = suspended ? 1 : 0 }, tx);
        c.Execute("""
            INSERT INTO member_profiles (cid, registered_at, suspension_reason, suspended_until) VALUES (@cid, @now, @reason, @until)
            ON CONFLICT(cid) DO UPDATE SET suspension_reason = @reason, suspended_until = @until
            """, new { cid, now, reason = suspended ? reason : "", until }, tx);
        tx.Commit();
        string details = suspended ? (days is > 0 ? $"{days} d: {reason}" : $"permanent: {reason}") : reason;
        audit.Log(actor, suspended ? "suspend" : "unsuspend", cid.ToString(), details);
        if (suspended) notify.Suspended(cid, reason, until);
        else notify.Unsuspended(cid, expired: actor == 0);
    }

    /// <summary>Lifts temporary suspensions whose time is up; returns the CIDs released.</summary>
    public IReadOnlyList<long> LiftExpiredSuspensions()
    {
        using var c = db.Open();
        var due = c.Query<long>("""
            SELECT m.cid FROM members m JOIN member_profiles p ON p.cid = m.cid
            WHERE m.suspended = 1 AND p.suspended_until IS NOT NULL AND p.suspended_until <= @now
            """, new { now = Database.Now() }).ToList();
        foreach (var cid in due) SetSuspended(0, cid, false, "suspension expired");
        return due;
    }

    public void SetPilotRatings(long actor, long cid, int pilot, int military)
    {
        var old = Find(cid);
        if (old == null) return;
        using var c = db.Open();
        c.Execute("""
            INSERT INTO member_profiles (cid, registered_at, pilot_rating, military_rating) VALUES (@cid, @now, @pilot, @military)
            ON CONFLICT(cid) DO UPDATE SET pilot_rating = @pilot, military_rating = @military
            """, new { cid, now = Database.Now(), pilot, military });
        if (old.PilotRating != pilot)
        {
            audit.Log(actor, "pilot-rating", cid.ToString(), $"{PilotRatings.Pilot.Short(old.PilotRating)} → {PilotRatings.Pilot.Short(pilot)}");
            notify.RatingChanged(cid, "pilot", PilotRatings.Pilot.Short(old.PilotRating), PilotRatings.Pilot.Short(pilot), pilot > old.PilotRating);
        }
        if (old.MilitaryRating != military)
        {
            audit.Log(actor, "military-rating", cid.ToString(), $"{PilotRatings.Military.Short(old.MilitaryRating)} → {PilotRatings.Military.Short(military)}");
            notify.RatingChanged(cid, "military", PilotRatings.Military.Short(old.MilitaryRating), PilotRatings.Military.Short(military), military > old.MilitaryRating);
        }
    }

    public void ResetPassword(long actor, long cid, string password)
    {
        ChangePassword(cid, password);
        audit.Log(actor, "password-reset", cid.ToString());
        notify.PasswordResetByStaff(cid);
    }

    /// <summary>Staff rank: 0 (none), SUP or ADM. The FSD server reads it for supervisor rights.</summary>
    public void SetStaffRank(long actor, long cid, int rank)
    {
        if (!Ratings.IsStaffRank(rank)) throw new ArgumentOutOfRangeException(nameof(rank));
        using var c = db.Open();
        int old = c.ExecuteScalar<int>("SELECT staff_rank FROM members WHERE cid = @cid", new { cid });
        c.Execute("UPDATE members SET staff_rank = @rank WHERE cid = @cid", new { cid, rank });
        static string Name(int r) => r == 0 ? "—" : Ratings.Short(r);
        audit.Log(actor, "staff-rank", cid.ToString(), $"{Name(old)} → {Name(rank)}");
        if (old != rank) notify.StaffRankChanged(cid, rank == 0 ? "" : Ratings.Short(rank));
    }

    /// <summary>First and last name, as on registration; returns an English error or null.</summary>
    public static string? ValidateName(string name)
    {
        if (name.Length < 3 || name.Length > 60 || !name.Contains(' ')) return "Enter your first and last name";
        if (name.Any(char.IsControl) || name.Contains(':')) return "The name contains characters that are not allowed";
        return null;
    }

    /// <summary>Renames a member (the network shows the new name from their next connection).</summary>
    public void SetName(long actor, long cid, string name)
    {
        using var c = db.Open();
        var old = c.ExecuteScalar<string>("SELECT name FROM members WHERE cid = @cid", new { cid }) ?? "";
        c.Execute("UPDATE members SET name = @name WHERE cid = @cid", new { cid, name });
        audit.Log(actor, "name", cid.ToString(), $"{old} → {name}");
    }

    public void SetRoles(long actor, long cid, IEnumerable<string> roles)
    {
        var list = roles.Where(Permissions.Roles.ContainsKey).Distinct().ToList();
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM staff_roles WHERE cid = @cid", new { cid }, tx);
        foreach (var role in list) c.Execute("INSERT INTO staff_roles (cid, role) VALUES (@cid, @role)", new { cid, role }, tx);
        tx.Commit();
        audit.Log(actor, "roles", cid.ToString(), list.Count == 0 ? "—" : string.Join(", ", list));
    }

    public IReadOnlyList<StaffNote> Notes(long cid)
    {
        using var c = db.Open();
        return c.Query<StaffNote>("""
            SELECT n.*, COALESCE(m.name, '') AS author_name FROM staff_notes n LEFT JOIN members m ON m.cid = n.author_cid
            WHERE n.cid = @cid ORDER BY n.id DESC
            """, new { cid }).ToList();
    }

    public void AddNote(long actor, long cid, string body)
    {
        using var c = db.Open();
        c.Execute("INSERT INTO staff_notes (cid, author_cid, body, created_at) VALUES (@cid, @actor, @body, @now)",
            new { cid, actor, body, now = Database.Now() });
        audit.Log(actor, "note", cid.ToString());
    }
}
