using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SkyNetwork.Site.Data;

/// <summary>
/// SQLite storage. The <c>members</c> table is the FSD server's own account table (same file, same
/// schema), so a member registered on the site can connect to the network with the same CID and
/// password. Everything else belongs to the site.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    static Database() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public Database(IOptions<SiteOptions> options)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.Value.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA busy_timeout = 3000;");
        return c;
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public void Migrate()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode = WAL;");
        c.Execute("""
            -- Shared with skynet-fsd / skynet-admin: do not change these columns.
            CREATE TABLE IF NOT EXISTS members (
                cid INTEGER PRIMARY KEY, name TEXT NOT NULL,
                rating INTEGER NOT NULL DEFAULT 1,
                salt BLOB NOT NULL, hash BLOB NOT NULL,
                suspended INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS member_profiles (
                cid INTEGER PRIMARY KEY,
                email TEXT UNIQUE,
                country TEXT NOT NULL DEFAULT '',
                registered_at INTEGER NOT NULL,
                last_login_at INTEGER,
                suspension_reason TEXT NOT NULL DEFAULT '');

            CREATE TABLE IF NOT EXISTS staff_roles (
                cid INTEGER NOT NULL, role TEXT NOT NULL, PRIMARY KEY (cid, role));

            CREATE TABLE IF NOT EXISTS staff_notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT, cid INTEGER NOT NULL, author_cid INTEGER NOT NULL,
                body TEXT NOT NULL, created_at INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS audit_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT, actor_cid INTEGER NOT NULL, action TEXT NOT NULL,
                target TEXT NOT NULL DEFAULT '', details TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS flight_plans (
                id INTEGER PRIMARY KEY AUTOINCREMENT, cid INTEGER NOT NULL, callsign TEXT NOT NULL,
                rules TEXT NOT NULL, aircraft TEXT NOT NULL, cruise_speed INTEGER NOT NULL,
                departure TEXT NOT NULL, destination TEXT NOT NULL, alternate TEXT NOT NULL DEFAULT '',
                departure_time TEXT NOT NULL, cruise_altitude TEXT NOT NULL,
                enroute_minutes INTEGER NOT NULL, fuel_minutes INTEGER NOT NULL,
                route TEXT NOT NULL, remarks TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_flight_plans_cid ON flight_plans (cid, id);

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, summary TEXT NOT NULL DEFAULT '',
                body TEXT NOT NULL DEFAULT '', airports TEXT NOT NULL DEFAULT '',
                starts_at INTEGER NOT NULL, ends_at INTEGER NOT NULL,
                published INTEGER NOT NULL DEFAULT 1, created_by INTEGER NOT NULL, created_at INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS news (
                id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, body TEXT NOT NULL,
                published INTEGER NOT NULL DEFAULT 1, author_cid INTEGER NOT NULL, created_at INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS bookings (
                id INTEGER PRIMARY KEY AUTOINCREMENT, cid INTEGER NOT NULL, callsign TEXT NOT NULL,
                starts_at INTEGER NOT NULL, ends_at INTEGER NOT NULL, created_at INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS network_sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT, cid INTEGER NOT NULL, callsign TEXT NOT NULL,
                kind TEXT NOT NULL, details TEXT NOT NULL DEFAULT '',
                started_at INTEGER NOT NULL, ended_at INTEGER);
            CREATE INDEX IF NOT EXISTS ix_sessions_cid ON network_sessions (cid, started_at);

            CREATE TABLE IF NOT EXISTS tickets (
                id INTEGER PRIMARY KEY AUTOINCREMENT, cid INTEGER, email TEXT NOT NULL DEFAULT '',
                subject TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'open',
                created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS ticket_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT, ticket_id INTEGER NOT NULL REFERENCES tickets(id),
                cid INTEGER, staff INTEGER NOT NULL DEFAULT 0, body TEXT NOT NULL, created_at INTEGER NOT NULL);

            -- Divisions (e.g. SKYRUS) send rating requests through the division API with their key;
            -- a supervisor approves them in the staff area.
            CREATE TABLE IF NOT EXISTS divisions (
                id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT NOT NULL UNIQUE COLLATE NOCASE, name TEXT NOT NULL DEFAULT '',
                api_key_hash TEXT UNIQUE, api_key_hint TEXT NOT NULL DEFAULT '', api_key_created_at INTEGER,
                created_at INTEGER NOT NULL);

            -- SkyNetwork Connect: sign-in on other sites (division sites and the like) through the
            -- network, OAuth 2.0 authorization code flow. Codes and tokens are stored as SHA-256 only.
            CREATE TABLE IF NOT EXISTS oauth_clients (
                id INTEGER PRIMARY KEY AUTOINCREMENT, client_id TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
                secret_hash TEXT NOT NULL, secret_hint TEXT NOT NULL, redirect_uris TEXT NOT NULL,
                active INTEGER NOT NULL DEFAULT 1, created_by INTEGER NOT NULL, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS oauth_codes (
                code_hash TEXT PRIMARY KEY, client_id TEXT NOT NULL, cid INTEGER NOT NULL, redirect_uri TEXT NOT NULL,
                scope TEXT NOT NULL, code_challenge TEXT NOT NULL DEFAULT '', expires_at INTEGER NOT NULL, used INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS oauth_tokens (
                token_hash TEXT PRIMARY KEY, client_id TEXT NOT NULL, cid INTEGER NOT NULL, scope TEXT NOT NULL, expires_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS oauth_consents (
                cid INTEGER NOT NULL, client_id TEXT NOT NULL, scope TEXT NOT NULL, created_at INTEGER NOT NULL,
                PRIMARY KEY (cid, client_id));

            CREATE TABLE IF NOT EXISTS rating_requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT, division_id INTEGER NOT NULL REFERENCES divisions(id),
                cid INTEGER NOT NULL, track TEXT NOT NULL, current_rating INTEGER NOT NULL, target_rating INTEGER NOT NULL,
                examiner_cid INTEGER, examiner_name TEXT NOT NULL DEFAULT '', exam_date TEXT NOT NULL DEFAULT '',
                score TEXT NOT NULL DEFAULT '', report_url TEXT NOT NULL DEFAULT '', comment TEXT NOT NULL DEFAULT '',
                external_id TEXT, status TEXT NOT NULL DEFAULT 'pending',
                reviewer_cid INTEGER, review_comment TEXT NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_rating_requests_status ON rating_requests (status, created_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_rating_requests_external ON rating_requests (division_id, external_id);
            """);
        // Staff ranks (SUP, ADM) used to live in members.rating; they get their own column. The FSD
        // server does the same migration: whichever starts first moves them, in one transaction.
        using (var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable))
        {
            var columns = c.Query<string>("SELECT name FROM pragma_table_info('members')", transaction: tx);
            if (!columns.Contains("staff_rank", StringComparer.OrdinalIgnoreCase))
            {
                c.Execute("ALTER TABLE members ADD COLUMN staff_rank INTEGER NOT NULL DEFAULT 0", transaction: tx);
                c.Execute("UPDATE members SET staff_rank = rating, rating = 1 WHERE rating >= 11", transaction: tx);
            }
            tx.Commit();
        }

        // Columns added after the first release.
        // The "training" role went away with the training section (training is on the division sites).
        c.Execute("DELETE FROM staff_roles WHERE role = 'training'");
        AddColumn(c, "member_profiles", "suspended_until", "INTEGER");
        AddColumn(c, "member_profiles", "pilot_rating", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(c, "member_profiles", "military_rating", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(c, "flight_plans", "waypoints", "TEXT NOT NULL DEFAULT ''");

        // Waypoints and airway segments learned from imported SimBrief routes (see NavData).
        c.Execute("""
            CREATE TABLE IF NOT EXISTS nav_fixes (
                ident TEXT NOT NULL, lat REAL NOT NULL, lon REAL NOT NULL, seen_at INTEGER NOT NULL,
                PRIMARY KEY (ident, lat, lon));
            CREATE TABLE IF NOT EXISTS nav_airways (
                name TEXT NOT NULL, a TEXT NOT NULL, a_lat REAL NOT NULL, a_lon REAL NOT NULL,
                b TEXT NOT NULL, b_lat REAL NOT NULL, b_lon REAL NOT NULL, seen_at INTEGER NOT NULL,
                PRIMARY KEY (name, a, b));
            """);
        AddColumn(c, "member_profiles", "simbrief", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "events", "banner", "TEXT NOT NULL DEFAULT ''");
        // Email confirmation: members who were there before it are counted as confirmed.
        if (AddColumn(c, "member_profiles", "email_verified", "INTEGER NOT NULL DEFAULT 0"))
            c.Execute("UPDATE member_profiles SET email_verified = 1");
        c.Execute("""
            CREATE TABLE IF NOT EXISTS email_tokens (
                token_hash TEXT PRIMARY KEY, cid INTEGER NOT NULL, purpose TEXT NOT NULL, email TEXT NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, used INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_email_tokens_cid ON email_tokens (cid, purpose, created_at);
            """);
        AddColumn(c, "news", "banner", "TEXT NOT NULL DEFAULT ''");
        // Banner layout chosen in the editor (see BannerLayout): height on its page and the part kept in view.
        AddColumn(c, "events", "banner_size", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "events", "banner_focus", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "news", "banner_size", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "news", "banner_focus", "TEXT NOT NULL DEFAULT ''");
        // English versions of events and news (optional: the English site falls back to the Russian ones).
        AddColumn(c, "events", "title_en", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "events", "summary_en", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "events", "body_en", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "events", "banner_en", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "news", "title_en", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "news", "body_en", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "news", "banner_en", "TEXT NOT NULL DEFAULT ''");
        // Profile picture: a file name in the uploads directory, empty for the initials.
        AddColumn(c, "member_profiles", "avatar", "TEXT NOT NULL DEFAULT ''");
        // The highest CID ever given: a deleted member's CID is never given to anyone else.
        c.Execute("""
            CREATE TABLE IF NOT EXISTS site_counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
            INSERT OR IGNORE INTO site_counters (name, value) SELECT 'last_cid', COALESCE(MAX(cid), 0) FROM members;
            """);
        // Friends: members a member follows on the map (one way: the other person is not asked or told).
        c.Execute("""
            CREATE TABLE IF NOT EXISTS friends (
                cid INTEGER NOT NULL, friend_cid INTEGER NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (cid, friend_cid));
            CREATE INDEX IF NOT EXISTS ix_friends_friend ON friends (friend_cid);
            """);

    }

    /// <summary>Adds the column if it is missing; true when it was added.</summary>
    private static bool AddColumn(SqliteConnection c, string table, string column, string type)
    {
        var columns = c.Query<string>($"SELECT name FROM pragma_table_info('{table}')");
        if (columns.Contains(column, StringComparer.OrdinalIgnoreCase)) return false;
        c.Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
        return true;
    }
}
