using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Localization;
using SkyNetwork.Site.Security;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Pages.Staff;

public sealed class MemberModel(CurrentUser me, MemberService members, SessionService sessions, AuditService audit, UploadStore uploads) : StaffPageModel(me)
{
    protected override Perm Required => Perm.ViewMembers;

    public Member Member { get; private set; } = new();
    public MemberHours Hours { get; private set; } = new(0, 0, 0, 0);
    public IReadOnlyList<NetworkSession> Sessions { get; private set; } = [];
    public IReadOnlyList<StaffNote> Notes { get; private set; } = [];
    public IReadOnlyList<AuditEntry> History { get; private set; } = [];
    public IReadOnlyList<string> Roles { get; private set; } = [];
    public IReadOnlyList<int> RatingOptions { get; private set; } = [];
    public bool CanSuspend { get; private set; }
    public bool CanSetStaffRank { get; private set; }
    public bool CanEditName { get; private set; }
    public bool CanDelete { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    private bool Load(long cid)
    {
        if (members.Find(cid) is not { } m) return false;
        Member = m;
        Hours = sessions.Hours(cid);
        Sessions = sessions.Recent(cid, 30);
        Roles = members.RolesOf(cid);
        if (Me.Has(Perm.Notes)) Notes = members.Notes(cid);
        if (Me.Has(Perm.Audit)) History = audit.Recent(50, cid.ToString());
        RatingOptions = m.Cid == Me.Cid ? [] // nobody changes their own rating
            : Ratings.Controller.Where(r => r == m.Rating || Permissions.CanSetRating(Me.Member!.StaffRank, Me.Permissions, m.Rating, r)).ToList();
        if (RatingOptions.Count == 1) RatingOptions = [];
        CanSuspend = Permissions.CanSuspend(Me.Member!, Me.Permissions, m);
        CanSetStaffRank = Permissions.CanSetStaffRank(Me.Member!, m);
        CanEditName = Permissions.CanEditName(Me.Member!, Me.Permissions, m);
        CanDelete = Permissions.CanDelete(Me.Member!, Me.Permissions, m);
        return true;
    }

    public IActionResult OnGet(long cid) => Load(cid) ? Page() : NotFound();

    public IActionResult OnPostRating(long cid, int rating)
    {
        if (!Load(cid)) return NotFound();
        if (cid == Me.Cid || !Permissions.CanSetRating(Me.Member!.StaffRank, Me.Permissions, Member.Rating, rating)) Error = "You cannot set this rating";
        else if (rating != Member.Rating)
        {
            members.SetRating(Me.Cid, cid, rating);
            Message = this.T("Rating changed to {0}", Ratings.Short(rating));
        }
        Load(cid);
        return Page();
    }

    public IActionResult OnPostSuspend(long cid, bool suspend, string? reason, int days)
    {
        if (!Load(cid)) return NotFound();
        if (!Me.Has(Perm.Suspend) || cid == Me.Cid) return NotFound();
        // Supervisors cannot lock out other supervisors or administrators.
        if (!CanSuspend) Error = "Only an administrator can suspend supervisors and administrators";
        else if (suspend && string.IsNullOrWhiteSpace(reason)) Error = "Enter a reason";
        else if (suspend == Member.Suspended) Error = suspend ? "The member is already suspended" : "The member is not suspended";
        else
        {
            members.SetSuspended(Me.Cid, cid, suspend, (reason ?? "").Trim(), days is > 0 and <= 3650 ? days : null);
            Message = suspend ? "Member suspended. They will be disconnected from the network within 10 seconds" : "Suspension lifted";
        }
        Load(cid);
        return Page();
    }

    public IActionResult OnPostName(long cid, string? name)
    {
        if (!Load(cid) || !CanEditName) return NotFound();
        name = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (MemberService.ValidateName(name) is { } error) Error = error;
        else if (name != Member.Name)
        {
            members.SetName(Me.Cid, cid, name);
            Message = "Name changed. The network shows it from the member's next connection";
        }
        Load(cid);
        return Page();
    }

    /// <summary>An unsuitable profile picture goes; the same people who may rename a member may do it.</summary>
    public IActionResult OnPostRemoveAvatar(long cid)
    {
        if (!Load(cid) || !CanEditName) return NotFound();
        if (Member.Avatar.Length > 0)
        {
            uploads.Delete(members.SetAvatar(cid, ""));
            audit.Log(Me.Cid, "avatar", cid.ToString(), "removed");
            Message = "Profile picture removed";
        }
        Load(cid);
        return Page();
    }

    public IActionResult OnPostStaffRank(long cid, int rank)
    {
        if (!Load(cid) || !CanSetStaffRank) return NotFound();
        if (!Ratings.IsStaffRank(rank)) Error = "Choose a rank from the list";
        else if (rank != Member.StaffRank)
        {
            members.SetStaffRank(Me.Cid, cid, rank);
            Message = "Staff rank saved";
        }
        Load(cid);
        return Page();
    }

    public IActionResult OnPostPilotRatings(long cid, int pilot, int military)
    {
        if (!Load(cid) || !Me.Has(Perm.PilotRatings) || cid == Me.Cid) return NotFound();
        if (!PilotRatings.Pilot.Valid(pilot) || !PilotRatings.Military.Valid(military)) Error = "Choose a rating from the list";
        else
        {
            members.SetPilotRatings(Me.Cid, cid, pilot, military);
            Message = "Ratings saved";
        }
        Load(cid);
        return Page();
    }

    public IActionResult OnPostResetPassword(long cid)
    {
        if (!Load(cid) || !Me.Has(Perm.ResetPasswords)) return NotFound();
        string temp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', 'x').Replace('/', 'y');
        members.ResetPassword(Me.Cid, cid, temp);
        Message = this.T("Temporary password: {0} — it is shown only once", temp);
        return Page();
    }

    public IActionResult OnPostRoles(long cid, string[] roles)
    {
        if (!Load(cid) || !Me.Has(Perm.ManageRoles)) return NotFound();
        members.SetRoles(Me.Cid, cid, roles);
        Message = "Roles saved";
        Load(cid);
        return Page();
    }

    /// <summary>Deletes the account for good (administrators; not staff with a rank, not oneself).</summary>
    public IActionResult OnPostDelete(long cid, string? reason, long confirmCid)
    {
        if (!Load(cid) || !CanDelete) return NotFound();
        if (string.IsNullOrWhiteSpace(reason)) Error = "Enter a reason";
        else if (confirmCid != cid) Error = "Type the member's CID to confirm";
        else
        {
            uploads.Delete(members.Delete(Me.Cid, cid, reason.Trim()));
            return Redirect($"/staff/members?deleted={cid}");
        }
        return Page();
    }

    public IActionResult OnPostNote(long cid, string body)
    {
        if (!Load(cid) || !Me.Has(Perm.Notes)) return NotFound();
        if (!string.IsNullOrWhiteSpace(body)) members.AddNote(Me.Cid, cid, body.Trim());
        return Redirect($"/staff/members/{cid}");
    }
}
