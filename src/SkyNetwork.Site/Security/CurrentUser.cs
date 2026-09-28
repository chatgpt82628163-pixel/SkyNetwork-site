using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Security;

/// <summary>
/// The signed-in member, re-read from the database on every request so that a new rating, role or a
/// suspension takes effect immediately.
/// </summary>
public sealed class CurrentUser(MemberService members)
{
    public const string CidClaim = "cid";
    private bool _loaded;

    public Member? Member { get; private set; }
    public Perm Permissions { get; private set; }
    public IReadOnlyList<string> Roles { get; private set; } = [];

    public bool IsSignedIn => Member != null;
    public bool IsStaff => Permissions.HasFlag(Perm.StaffArea);
    public long Cid => Member?.Cid ?? 0;

    public bool Has(Perm p) => (Permissions & p) == p && p != Perm.None;

    public async Task LoadAsync(HttpContext ctx)
    {
        if (_loaded) return;
        _loaded = true;
        if (!long.TryParse(ctx.User.FindFirstValue(CidClaim), out var cid)) return;
        var m = members.Find(cid);
        if (m == null || m.Suspended)
        {
            // Suspended or deleted: drop the cookie and treat this request as anonymous too.
            await ctx.SignOutAsync();
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
            return;
        }
        // Reject cookies issued before the last password change.
        var cookieStamp = ctx.User.FindFirstValue(SignIn.StampClaim) ?? "";
        if (m.SecurityStamp.Length > 0 && cookieStamp != m.SecurityStamp)
        {
            await ctx.SignOutAsync();
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
            return;
        }
        Member = m;
        Roles = members.RolesOf(cid);
        Permissions = Security.Permissions.For(m.Rating, m.StaffRank, Roles);
    }
}
