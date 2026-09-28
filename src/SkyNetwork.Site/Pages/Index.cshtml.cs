using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Pages;

public sealed class IndexModel(NetworkFeed feed, ContentService content, MemberService members, SessionService sessions) : PageModel
{
    public OnlineSnapshot Online { get; private set; } = OnlineSnapshot.Empty;
    public IReadOnlyList<NetworkEvent> Events { get; private set; } = [];
    /// <summary>All events still to come (the page shows the first three).</summary>
    public int UpcomingCount { get; private set; }
    public IReadOnlyList<NewsPost> News { get; private set; } = [];
    public long MemberCount { get; private set; }
    public int SessionsToday { get; private set; }
    public int SessionsMonth { get; private set; }
    /// <summary>Controllers on a position (observers and ATIS left out), for the "on the air" list.</summary>
    public IReadOnlyList<ControllerOnline> OnPosition { get; private set; } = [];

    public void OnGet()
    {
        Online = feed.Current;
        var upcoming = content.UpcomingEvents(100);
        UpcomingCount = upcoming.Count;
        Events = upcoming.Take(3).ToList();
        News = content.News(3);
        MemberCount = members.Count();
        (SessionsToday, SessionsMonth) = sessions.SessionCounts();
        OnPosition = Online.OnPosition;
    }
}
