using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;

namespace SkyNetwork.Site.Pages.Oauth;

/// <summary>
/// SkyNetwork Connect sign-in: another site sends the member here; after signing in (and agreeing once
/// per site) the member goes back to the site with a one-time code.
/// </summary>
public sealed class AuthorizeModel(CurrentUser me, ConnectService connect) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "response_type")] public string ResponseType { get; set; } = "";
    [BindProperty(SupportsGet = true, Name = "client_id")] public string ClientId { get; set; } = "";
    [BindProperty(SupportsGet = true, Name = "redirect_uri")] public string RedirectUri { get; set; } = "";
    [BindProperty(SupportsGet = true, Name = "scope")] public string Scope { get; set; } = "";
    [BindProperty(SupportsGet = true, Name = "state")] public string State { get; set; } = "";
    [BindProperty(SupportsGet = true, Name = "code_challenge")] public string CodeChallenge { get; set; } = "";
    [BindProperty(SupportsGet = true, Name = "code_challenge_method")] public string CodeChallengeMethod { get; set; } = "";

    public ConnectClient? Client { get; private set; }
    public IReadOnlyList<string> Scopes { get; private set; } = [];
    /// <summary>Set when the request cannot be sent back to the site (unknown site or address).</summary>
    public string? Error { get; private set; }
    public string SiteHost => Uri.TryCreate(RedirectUri, UriKind.Absolute, out var u) ? u.Authority : "";

    /// <summary>Checks the request; returns a result to send when the page is not to be shown.</summary>
    private IActionResult? Check()
    {
        Client = connect.Client(ClientId);
        if (Client is not { Active: true }) { Error = "This site is not registered with SkyNetwork."; return Page(); }
        if (!Client.Redirects.Contains(RedirectUri)) { Error = "The return address does not belong to this site."; return Page(); }
        if (!me.IsSignedIn) return Redirect("/login?returnUrl=" + Uri.EscapeDataString(Request.Path + Request.QueryString));
        if (!me.Member!.EmailVerified) { Error = "Confirm your email first: open the link in the letter we sent you, then try again."; return Page(); }
        if (ResponseType != "code") return Back(("error", "unsupported_response_type"));
        if (ConnectService.ParseScope(Scope) is not { } scopes) return Back(("error", "invalid_scope"));
        if (CodeChallenge.Length > 0 && CodeChallengeMethod != "S256" || CodeChallenge.Length is > 0 and (< 43 or > 128))
            return Back(("error", "invalid_request"), ("error_description", "code_challenge_method must be S256"));
        Scopes = scopes;
        return null;
    }

    private RedirectResult Back(params (string Key, string Value)[] query)
    {
        var parts = query.Append(("state", State)).Where(q => q.Item2.Length > 0).Select(q => $"{q.Item1}={Uri.EscapeDataString(q.Item2)}");
        return Redirect(RedirectUri + (RedirectUri.Contains('?') ? "&" : "?") + string.Join("&", parts));
    }

    private RedirectResult Grant()
    {
        var code = connect.IssueCode(ClientId, me.Cid, RedirectUri, Scopes, CodeChallenge);
        return Back(("code", code));
    }

    public IActionResult OnGet()
    {
        if (Check() is { } result) return result;
        // Agreed before (for the same data): straight back to the site.
        return connect.HasConsent(me.Cid, ClientId, Scopes) ? Grant() : Page();
    }

    public IActionResult OnPost(string? decision)
    {
        if (Check() is { } result) return result;
        if (decision != "allow") return Back(("error", "access_denied"));
        connect.SaveConsent(me.Cid, ClientId, Scopes);
        return Grant();
    }
}
