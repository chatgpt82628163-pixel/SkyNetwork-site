using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;

namespace SkyNetwork.Site.Pages.Members;

public sealed class DetailsModel(MemberService members, SessionService sessions, CurrentUser me, FriendService friends) : PageModel
{
    public Member Member { get; private set; } = new();
    public MemberHours Hours { get; private set; } = new(0, 0, 0, 0);
    public IReadOnlyList<NetworkSession> Sessions { get; private set; } = [];
    /// <summary>Signed in, looking at someone else's profile: the friends button shows.</summary>
    public bool CanFriend { get; private set; }
    public bool IsFriend { get; private set; }

    public IActionResult OnGet(long cid)
    {
        // A registration whose email is not confirmed does not count: no public profile.
        if (members.FindConfirmed(cid) is not { } m) return NotFound();
        Member = m;
        Hours = sessions.Hours(cid);
        Sessions = sessions.Recent(cid, 20);
        CanFriend = me.IsSignedIn && me.Cid != cid;
        IsFriend = CanFriend && friends.IsFriend(me.Cid, cid);
        return Page();
    }

    public IActionResult OnPostFriend(long cid, bool add)
    {
        if (!me.IsSignedIn) return Redirect($"/login?returnUrl={Uri.EscapeDataString($"/members/{cid}")}");
        if (add) friends.Add(me.Cid, cid); else friends.Remove(me.Cid, cid);
        return Redirect($"/members/{cid}");
    }
}
