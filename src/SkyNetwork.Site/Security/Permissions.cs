using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Security;

[Flags]
public enum Perm
{
    None = 0,
    /// <summary>May see the staff area at all (it is a 404 for everyone else).</summary>
    StaffArea = 1 << 0,
    ViewMembers = 1 << 1,
    Suspend = 1 << 2,
    EditRatings = 1 << 3,
    ManageRoles = 1 << 4,
    Tickets = 1 << 5,
    // 1 << 6 was Training (training requests moved to the division websites).
    Events = 1 << 7,
    News = 1 << 8,
    Bookings = 1 << 9,
    Audit = 1 << 10,
    Online = 1 << 11,
    Notes = 1 << 12,
    ResetPasswords = 1 << 13,
    /// <summary>Pilot and military ratings (site only).</summary>
    PilotRatings = 1 << 14,
    /// <summary>Change a member's first and last name.</summary>
    EditNames = 1 << 15,
    /// <summary>Issue and revoke division API keys.</summary>
    ManageDivisions = 1 << 16,
    /// <summary>Approve or decline the rating requests divisions send after an exam.</summary>
    ApproveRatings = 1 << 17,
    /// <summary>The status page: how the servers and services of the network are doing.</summary>
    SystemStatus = 1 << 18,
    All = ~0,
}

/// <summary>
/// Who may do what. Network ratings give the base: administrators everything, supervisors run the
/// network day to day, instructors rate controllers. Administrators can add site roles on top
/// (event manager, news editor, support).
/// </summary>
public static class Permissions
{
    public static readonly IReadOnlyDictionary<string, string> Roles = new Dictionary<string, string>
    {
        ["events"] = "Events",
        ["news"] = "News",
        ["support"] = "Support",
        // Supervisor commands in the controller client (.kill, .warn, .find...), below a supervisor.
        ["fsup"] = "Facility supervisor",
    };

    private const Perm Supervisor = Perm.StaffArea | Perm.ViewMembers | Perm.Suspend | Perm.Notes | Perm.Tickets | Perm.Online |
                                    Perm.Bookings | Perm.Audit | Perm.Events | Perm.News | Perm.PilotRatings |
                                    Perm.EditNames | Perm.ApproveRatings | Perm.SystemStatus;

    private const Perm Instructor = Perm.StaffArea | Perm.ViewMembers | Perm.Notes | Perm.EditRatings | Perm.Online |
                                    Perm.PilotRatings;

    /// <param name="rating">Controller rating: instructors (I1–I3) train and rate.</param>
    /// <param name="staffRank">Staff rank: supervisors run the network, administrators everything.</param>
    public static Perm For(int rating, int staffRank, IEnumerable<string> roles)
    {
        if (staffRank == Ratings.ADM) return Perm.All;
        var p = staffRank == Ratings.SUP ? Supervisor : Perm.None;
        if (rating is >= Ratings.I1 and <= Ratings.I3) p |= Instructor;
        foreach (var role in roles)
            p |= role switch
            {
                "events" => Perm.StaffArea | Perm.Events,
                "news" => Perm.StaffArea | Perm.News,
                "support" => Perm.StaffArea | Perm.Tickets | Perm.ViewMembers | Perm.ResetPasswords,
                "fsup" => Perm.StaffArea | Perm.Online,
                _ => Perm.None,
            };
        return p;
    }

    /// <summary>
    /// Whether an actor with staff rank <paramref name="actorStaffRank"/> may move a member's controller
    /// rating from <paramref name="from"/> to <paramref name="to"/>: administrators any controller rating;
    /// everyone else with EditRatings only up to C3.
    /// </summary>
    public static bool CanSetRating(int actorStaffRank, Perm actor, int from, int to)
    {
        if (!actor.HasFlag(Perm.EditRatings) || !Ratings.IsController(to)) return false;
        if (actorStaffRank == Ratings.ADM) return true;
        return from <= Ratings.C3 && to <= Ratings.C3;
    }

    /// <summary>
    /// Whether a rating request from a division may be approved: supervisors approve controller ratings
    /// up to C3 and every pilot and military rating; instructor ratings (I1–I3) only administrators.
    /// </summary>
    public static bool CanApproveRating(int actorStaffRank, Perm actor, string track, int target)
    {
        if (!actor.HasFlag(Perm.ApproveRatings)) return false;
        if (actorStaffRank == Ratings.ADM) return true;
        return track != "atc" || target <= Ratings.C3;
    }

    /// <summary>
    /// Names are changed by supervisors and administrators; a supervisor not for other members of the
    /// team with a rank (only an administrator renames supervisors and administrators).
    /// </summary>
    public static bool CanEditName(Member actor, Perm actorPerms, Member target) =>
        actorPerms.HasFlag(Perm.EditNames) && (actor.StaffRank == Ratings.ADM || target.StaffRank == 0 || actor.Cid == target.Cid);

    /// <summary>Staff ranks (SUP, ADM) are given and taken only by administrators, never to themselves.</summary>
    public static bool CanSetStaffRank(Member actor, Member target) => actor.StaffRank == Ratings.ADM && actor.Cid != target.Cid;

    /// <summary>
    /// Who may suspend whom: nobody themselves, administrators anyone else, supervisors only members
    /// below supervisor (not other supervisors or administrators).
    /// </summary>
    public static bool CanSuspend(Member actor, Perm actorPerms, Member target) =>
        actorPerms.HasFlag(Perm.Suspend) && actor.Cid != target.Cid &&
        (actor.StaffRank == Ratings.ADM || target.StaffRank == 0);
}
