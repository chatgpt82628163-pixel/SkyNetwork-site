using Microsoft.AspNetCore.Mvc;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Localization;
using SkyNetwork.Site.Security;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Pages.Staff;

public sealed class MembersModel(CurrentUser me, MemberService members, UploadStore uploads) : StaffPageModel(me)
{
    protected override Perm Required => Perm.ViewMembers;

    /// <summary>At most this many members are listed, the newest first; a search finds the others.</summary>
    public const int Limit = 100;

    public string Query { get; private set; } = "";
    /// <summary>
    /// "" for everyone, "suspicious" (name or email would not pass registration today), "suspended" or "unconfirmed"
    /// (registered, email not confirmed yet: not counted as members).
    /// </summary>
    public string Filter { get; private set; } = "";
    public IReadOnlyList<Member> Results { get; private set; } = [];
    /// <summary>More members match than are listed.</summary>
    public bool Truncated { get; private set; }
    public long Total { get; private set; }
    public long Unconfirmed { get; private set; }
    public bool CanSuspend => Me.Has(Perm.Suspend);
    public bool CanDelete => Me.Has(Perm.DeleteAccounts);
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    // Older links (?suspended=1, ?suspicious=1) still work.
    public void OnGet(string? q, string? show, int? suspended, int? suspicious, long? deleted)
    {
        // Back from a member's card after deleting the account.
        if (deleted is { } cid && members.Find(cid) == null) Message = this.T("Account {0} deleted", cid);
        Find(q, Show(show, suspended, suspicious));
    }

    private static string Show(string? show, int? suspended, int? suspicious) =>
        show is "suspicious" or "suspended" or "unconfirmed" ? show : suspicious == 1 ? "suspicious" : suspended == 1 ? "suspended" : "";

    private void Find(string? q, string filter)
    {
        Query = (q ?? "").Trim();
        Filter = filter;
        Total = members.Count();
        Unconfirmed = members.CountUnconfirmed();
        var found = members.Search(Query, filter is "suspended" or "unconfirmed" ? filter : "", filter == "suspicious" ? 5000 : Limit + 1);
        if (filter == "suspicious") found = found.Where(m => SignupGuard.Suspicion(m) != null).ToList();
        Truncated = found.Count > Limit;
        Results = found.Take(Limit).ToList();
    }

    /// <summary>Suspends the ticked members for good, with the checks of a single suspension (junk registrations).</summary>
    public IActionResult OnPostSuspend(long[] cids, string? reason, string? q, string? show, int? suspended, int? suspicious)
    {
        if (!CanSuspend) return NotFound();
        reason = (reason ?? "").Trim();
        if (reason.Length == 0) Error = "Enter a reason";
        else
        {
            int done = 0, skipped = 0;
            foreach (long cid in cids.Distinct())
            {
                if (members.Find(cid) is not { } m || m.Suspended || !Permissions.CanSuspend(Me.Member!, Me.Permissions, m))
                {
                    skipped++;
                    continue;
                }
                members.SetSuspended(Me.Cid, cid, true, reason);
                done++;
            }
            Message = this.T("Suspended: {0}. Skipped: {1}", done, skipped);
        }
        Find(q, Show(show, suspended, suspicious));
        return Page();
    }

    /// <summary>Deletes the ticked accounts (junk registrations), with the checks of a single deletion.</summary>
    public IActionResult OnPostDelete(long[] cids, string? reason, string? q, string? show, int? suspended, int? suspicious)
    {
        if (!CanDelete) return NotFound();
        int done = 0, skipped = 0;
        foreach (long cid in cids.Distinct())
        {
            if (members.Find(cid) is not { } m || !Permissions.CanDelete(Me.Member!, Me.Permissions, m))
            {
                skipped++;
                continue;
            }
            uploads.Delete(members.Delete(Me.Cid, cid, (reason ?? "").Trim()));
            done++;
        }
        Message = this.T("Deleted: {0}. Skipped: {1}", done, skipped);
        Find(q, Show(show, suspended, suspicious));
        return Page();
    }
}
