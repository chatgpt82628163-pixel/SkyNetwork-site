using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Pages.Account;

public sealed class FriendsModel(CurrentUser me, FriendService friends, NetworkFeed feed) : PageModel
{
    [BindProperty] public string Cid { get; set; } = "";
    public IReadOnlyList<Member> List { get; private set; } = [];
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    private OnlineSnapshot _online = OnlineSnapshot.Empty;

    public void OnGet()
    {
        List = friends.List(me.Cid);
        _online = feed.Current;
    }

    /// <summary>The callsign the member is on the air with right now, or null.</summary>
    public string? OnAir(long cid) =>
        _online.Pilots.FirstOrDefault(p => p.Cid == cid)?.Callsign
        ?? _online.Controllers.FirstOrDefault(c => c.Cid == cid && c.FacilityName != "OBS")?.Callsign;

    public IActionResult OnPostAdd()
    {
        if (!long.TryParse((Cid ?? "").Trim(), out var cid)) Error = "Enter the member's CID";
        else if (friends.Add(me.Cid, cid) is { } error) Error = error;
        else { Message = "Added to friends"; Cid = ""; }
        OnGet();
        return Page();
    }

    public IActionResult OnPostRemove(long cid)
    {
        friends.Remove(me.Cid, cid);
        return Redirect("/account/friends");
    }
}
