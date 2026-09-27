using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Pages.Members;

public sealed class DetailsModel(MemberService members, SessionService sessions) : PageModel
{
    public Member Member { get; private set; } = new();
    public MemberHours Hours { get; private set; } = new(0, 0, 0, 0);
    public IReadOnlyList<NetworkSession> Sessions { get; private set; } = [];

    public IActionResult OnGet(long cid)
    {
        // A registration whose email is not confirmed does not count: no public profile.
        if (members.FindConfirmed(cid) is not { } m) return NotFound();
        Member = m;
        Hours = sessions.Hours(cid);
        Sessions = sessions.Recent(cid, 20);
        return Page();
    }
}
