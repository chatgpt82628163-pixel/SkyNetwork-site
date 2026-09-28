using System.Net.Mail;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkyNetwork.Site.Data;
using SkyNetwork.Site.Security;
using SkyNetwork.Site.Services;

namespace SkyNetwork.Site.Pages.Account;

public sealed class SettingsModel(CurrentUser me, MemberService members, ConnectService connect, AccountMail mail, UploadStore uploads) : PageModel
{
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Country { get; set; } = "";
    [BindProperty] public string Current { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";
    public string? Message { get; private set; }
    /// <summary>Sites the member signed in to with SkyNetwork Connect.</summary>
    public IReadOnlyList<ConnectConsent> ConnectedSites { get; private set; } = [];
    public string? Error { get; private set; }
    public Member Me => me.Member!;

    public void OnGet()
    {
        ConnectedSites = connect.Consents(me.Cid);
        Email = me.Member!.Email ?? "";
        Country = me.Member.Country;
    }

    public IActionResult OnPostProfile()
    {
        Email = Email.Trim();
        Country = Countries.Normalize(Country) ?? Country.Trim();
        if (!MailAddress.TryCreate(Email, out _)) Error = "Check the email address";
        // A country typed before the list existed can stay as it is.
        else if (Country.Length > 0 && Countries.Normalize(Country) == null && Country != me.Member!.Country) Error = "Choose a country from the list";
        else if (!Email.Equals(me.Member!.Email, StringComparison.OrdinalIgnoreCase) && members.EmailTaken(Email)) Error = "This email is already in use";
        else if (mail.Enabled && !Email.Equals(me.Member!.Email, StringComparison.OrdinalIgnoreCase))
        {
            // A new address takes effect once it is confirmed by the link sent to it.
            members.UpdateCountry(me.Cid, Country);
            Message = mail.SendConfirmation(Request, me.Member!, Email)
                ? "We sent a link to the new address: the email changes once you open it. No letter? Look in the Spam folder"
                : "A letter was sent a moment ago: wait two minutes before asking for another one";
            Email = me.Member!.Email ?? "";
        }
        else
        {
            members.UpdateProfile(me.Cid, Email, Country);
            Message = "Profile saved";
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAvatarAsync(IFormFile? avatar, bool remove)
    {
        OnGet();
        if (remove)
        {
            uploads.Delete(members.SetAvatar(me.Cid, ""));
            Message = "Profile picture removed";
        }
        else if (avatar is not { Length: > 0 }) Error = "Choose a picture";
        else
        {
            var (name, error) = await uploads.SaveImageAsync(avatar, HttpContext.RequestAborted, UploadStore.AvatarMaxBytes);
            if (error != null) Error = error;
            else
            {
                uploads.Delete(members.SetAvatar(me.Cid, name!));
                Message = "Profile picture saved";
            }
        }
        me.Member!.Avatar = members.Find(me.Cid)!.Avatar;
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        OnGet();
        if (members.Authenticate(me.Cid, Current) == null) Error = "The current password is wrong";
        else if (NewPassword.Length < 8) Error = "The new password must be at least 8 characters";
        else if (NewPassword.Contains(':')) Error = "The password cannot contain a colon";
        else if (NewPassword != Confirm) Error = "The passwords do not match";
        else
        {
            var newStamp = members.ChangePassword(me.Cid, NewPassword);
            // Re-issue this session's cookie with the new stamp so the current session stays signed in.
            // Preserve the original IsPersistent flag so a "remember me" login does not lose its 14-day cookie.
            // Patch the stamp on the in-memory member to avoid a second Find() round-trip.
            var existing = await HttpContext.AuthenticateAsync();
            var wasPersistent = existing.Succeeded && (existing.Properties?.IsPersistent ?? false);
            me.SecurityStamp = newStamp;
            await HttpContext.SignInMemberAsync(me, remember: wasPersistent);
            Message = "Password changed. Use the new password to connect to the network too";
        }
        return Page();
    }

    public void OnPostRevoke(string clientId)
    {
        connect.Revoke(me.Cid, clientId);
        Message = "Access withdrawn";
        OnGet();
    }

    /// <summary>
    /// The member deletes their own account after entering the password. Members of the team with a rank cannot:
    /// an administrator takes the rank away first.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(string? password, bool understood)
    {
        OnGet();
        if (Me.StaffRank != 0) Error = "Members of the team with a rank cannot delete their account: ask an administrator to take the rank away first";
        else if (!understood) Error = "Tick the box to confirm that you understand what is deleted";
        else if (!members.PasswordMatches(me.Cid, password ?? "")) Error = "The password is wrong";
        else
        {
            uploads.Delete(members.Delete(me.Cid, me.Cid, "deleted by the member"));
            await HttpContext.SignOutAsync();
            return Redirect("/account-deleted");
        }
        return Page();
    }
}
