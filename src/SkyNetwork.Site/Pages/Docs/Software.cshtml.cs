using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Pages.Docs;

public sealed class SoftwareModel(ReleaseService releases) : PageModel
{
    public Release? SkyPilot { get; private set; }
    public Release? NetworkAtc { get; private set; }

    public void OnGet()
    {
        SkyPilot = releases.LatestPublished("skypilot");
        NetworkAtc = releases.LatestPublished("network-atc");
    }
}
