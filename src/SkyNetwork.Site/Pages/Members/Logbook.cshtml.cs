using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Pages.Members;

public sealed class LogbookModel(MemberService members, SessionService sessions) : PageModel
{
    public const int PageSize = 50;

    public Member Member { get; private set; } = new();
    public MemberHours Hours { get; private set; } = new(0, 0, 0, 0);
    public LogbookTopStats Top { get; private set; } = new([], [], []);
    public IReadOnlyList<NetworkSession> Sessions { get; private set; } = [];
    public IReadOnlyList<int> Years { get; private set; } = [];

    public string Tab { get; private set; } = "all";
    public int? Year { get; private set; }
    public int CurrentPage { get; private set; }
    public int TotalPages { get; private set; }

    public IActionResult OnGet(long cid, string? tab, int? year, int p = 0)
    {
        if (members.FindConfirmed(cid) is not { } m) return NotFound();
        Member = m;
        Tab = tab is "pilot" or "atc" ? tab : "all";
        Year = year;
        CurrentPage = Math.Max(0, p);

        Hours = sessions.Hours(cid);
        Top = sessions.LogbookTop(cid);
        Years = sessions.LogbookYears(cid);

        string kind = Tab == "all" ? "" : Tab;
        int total = sessions.LogbookCount(cid, kind, Year);
        TotalPages = Math.Max(1, (total + PageSize - 1) / PageSize);
        CurrentPage = Math.Min(CurrentPage, TotalPages - 1);
        Sessions = sessions.LogbookPage(cid, kind, Year, CurrentPage, PageSize);
        return Page();
    }

    /// <summary>Parses the pilot details string "{aircraft} {dep}→{dest}" into its parts.</summary>
    public static (string Aircraft, string Departure, string Destination) ParsePilotDetails(string details)
    {
        if (string.IsNullOrEmpty(details)) return ("", "", "");
        int sp = details.IndexOf(' ');
        string aircraft = sp > 0 ? details[..sp] : details;
        string route = sp > 0 ? details[(sp + 1)..] : "";
        int arr = route.IndexOf('→');
        string dep = arr > 0 ? route[..arr] : route;
        string dest = arr > 0 ? route[(arr + 1)..] : "";
        return (aircraft, dep, dest);
    }

    /// <summary>Parses the controller details string "{freq} {rating}" into frequency and rating.</summary>
    public static (string Frequency, string Rating) ParseAtcDetails(string details)
    {
        if (string.IsNullOrEmpty(details)) return ("", "");
        int sp = details.IndexOf(' ');
        return sp > 0 ? (details[..sp], details[(sp + 1)..]) : (details, "");
    }
}
