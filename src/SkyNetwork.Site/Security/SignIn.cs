using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Security;

public static class SignIn
{
    public const string StampClaim = "stamp";

    public static Task SignInMemberAsync(this HttpContext ctx, Member m, bool remember)
    {
        var identity = new ClaimsIdentity(
            [new Claim(CurrentUser.CidClaim, m.Cid.ToString()), new Claim(ClaimTypes.Name, m.Name),
             new Claim(StampClaim, m.SecurityStamp)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = remember });
    }
}
