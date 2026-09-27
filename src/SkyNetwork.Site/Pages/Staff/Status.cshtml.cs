using Microsoft.AspNetCore.Mvc;
using SkyNetwork.Site.Security;
using SkyNetwork.Site.Services.Health;

namespace SkyNetwork.Site.Pages.Staff;

/// <summary>How the network server, the voice server, the bot, the website and the server are doing, with every problem explained.</summary>
public sealed class StatusModel(CurrentUser me, HealthMonitor monitor) : StaffPageModel(me)
{
    protected override Perm Required => Perm.SystemStatus;

    public HealthMonitor Monitor => monitor;

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Right after the site starts nothing has been checked yet.
        if (monitor.LastRun == default) await monitor.RunChecksAsync(ct);
    }

    /// <summary>The panel alone: the page refreshes it every half minute without reloading.</summary>
    public async Task<IActionResult> OnGetPanelAsync(CancellationToken ct)
    {
        if (monitor.LastRun == default) await monitor.RunChecksAsync(ct);
        return Partial("_StatusPanel", monitor);
    }

    public async Task<IActionResult> OnPostCheckAsync(CancellationToken ct)
    {
        await monitor.RunNowAsync(ct);
        return RedirectToPage();
    }
}
