using System.Globalization;
using Dapper;
using Microsoft.Extensions.Options;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Letters to members: confirming the email, resetting the password, and what staff did to their account
/// (suspension, ratings, staff rank, answers to support requests). Letters are in English.
/// </summary>
public sealed class Notifications(Database db, Mailer mailer, IOptions<SiteOptions> site)
{
    private string Network => site.Value.Name;

    private static string Date(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private sealed class Person
    {
        public string Name { get; set; } = "";
        public string? Email { get; set; }
    }

    private Person? Recipient(long cid)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Person>(
            "SELECT m.name, p.email FROM members m LEFT JOIN member_profiles p ON p.cid = m.cid WHERE m.cid = @cid", new { cid });
    }

    private string Footer(string text)
    {
        string url = mailer.SiteUrl;
        return $"{text}\n\n— {Network}{(url.Length > 0 ? "\n" + url : "")}";
    }

    private void ToMember(long cid, string subject, Func<string, string> text)
    {
        if (Recipient(cid) is not { Email: { Length: > 0 } email } r) return;
        mailer.Send(email, $"{Network}: {subject}", Footer(text(r.Name)));
    }

    // ---- account ------------------------------------------------------------------------------------

    public void ConfirmEmail(string email, string name, long cid, string link) =>
        mailer.Send(email, $"{Network}: confirm your email", Footer(
            $"Hello {name},\n\nYour CID: {cid}. To confirm this email address, open the link (valid for 48 hours):\n{link}\n\n" +
            "You cannot connect to the network until the address is confirmed. If you did not sign up, just delete this letter.\n\n" +
            "If this letter landed in Spam, mark it as not spam: our next letters will then reach your inbox."));

    public void ResetPassword(string email, string name, long cid, string link) =>
        mailer.Send(email, $"{Network}: password reset", Footer(
            $"Hello {name},\n\nSomeone (hopefully you) asked to reset the password of CID {cid}. Set a new password here (valid for 1 hour):\n{link}\n\n" +
            "If it was not you, do nothing: your password stays as it is."));

    public void PasswordResetByStaff(long cid) => ToMember(cid, "password reset",
        n => $"Hello {n},\n\nAn administrator set a new password for your account (CID {cid}), usually at your request to support. " +
             "If you did not ask for it, write to support on the website at once.");

    // ---- staff decisions ----------------------------------------------------------------------------

    public void Suspended(long cid, string reason, long? until)
    {
        string when = until is { } u ? $"until {Date(u)}" : "permanently";
        ToMember(cid, "account suspended",
            n => $"Hello {n},\n\nYour account (CID {cid}) is suspended {when}.\nReason: {reason}\n\n" +
                 "While the suspension lasts you cannot sign in or connect to the network. If you disagree, write to support on the website.");
    }

    public void Unsuspended(long cid, bool expired) => ToMember(cid, "suspension lifted",
        n => $"Hello {n},\n\n{(expired ? "The suspension of your account has ended" : "The suspension of your account has been lifted")} (CID {cid}). " +
             "You can sign in and connect to the network again.");

    public void RatingChanged(long cid, string what, string from, string to, bool up) => ToMember(cid,
        up ? "new rating" : "rating changed",
        n => $"Hello {n},\n\n{what switch { "pilot" => "The pilot rating", "military" => "The military rating", _ => "The controller rating" }} " +
             $"of your account (CID {cid}) has changed: {from} → {to}." + (up ? "\n\nCongratulations!" : ""));

    public void StaffRankChanged(long cid, string rank) => ToMember(cid,
        rank.Length > 0 ? "staff rank" : "staff rank removed",
        n => rank.Length > 0
            ? $"Hello {n},\n\nYou have been given the {rank} rank (CID {cid}). The supervisor commands are available in Network-ATC on a {rank} position."
            : $"Hello {n},\n\nThe staff rank of your account (CID {cid}) has been removed.");

    public void RatingRequestDeclined(long cid, string division, string rating, string comment) => ToMember(cid,
        "rating request declined",
        n => $"Hello {n},\n\nThe request of division {division} for the {rating} rating (CID {cid}) has been declined.\nComment: {comment}");

    /// <summary>A staff answer to a support request, sent to the address the request came from.</summary>
    public void TicketAnswered(string email, long ticketId, string subject, string answer, bool member)
    {
        string url = mailer.SiteUrl;
        string link = url.Length > 0 && member ? $"\n\n{url}/account/support/{ticketId}" : "";
        mailer.Send(email, $"{Network}: reply to request #{ticketId}", Footer(
            $"Support replied to your request #{ticketId} \"{subject}\":\n\n{answer}{link}"));
    }
}
