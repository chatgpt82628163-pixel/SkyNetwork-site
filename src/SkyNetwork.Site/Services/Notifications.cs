using System.Globalization;
using Dapper;
using Microsoft.Extensions.Options;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Letters to members: confirming the email, resetting the password, and what staff did to their account
/// (suspension, ratings, staff rank, answers to support requests). Each letter is in Russian and English.
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

    private string Footer(string ru, string en)
    {
        string url = mailer.SiteUrl;
        return $"{ru}\n\n— {Network}{(url.Length > 0 ? "\n" + url : "")}\n\n────────\n\n{en}\n\n— {Network}";
    }

    private void ToMember(long cid, string subjectRu, string subjectEn, Func<string, string> ru, Func<string, string> en)
    {
        if (Recipient(cid) is not { Email: { Length: > 0 } email } r) return;
        mailer.Send(email, $"{Network}: {subjectRu} / {subjectEn}", Footer(ru(r.Name), en(r.Name)));
    }

    // ---- account ------------------------------------------------------------------------------------

    public void ConfirmEmail(string email, string name, long cid, string link) =>
        mailer.Send(email, $"{Network}: подтвердите почту / confirm your email", Footer(
            $"Здравствуйте, {name}!\n\nВаш CID: {cid}. Чтобы подтвердить эту почту, откройте ссылку (действует 48 часов):\n{link}\n\n" +
            "Пока почта не подтверждена, подключиться к сети нельзя. Если вы не регистрировались, просто удалите это письмо.\n\n" +
            "Если письмо попало в «Спам», отметьте его «Не спам» — тогда следующие письма SkyNetwork будут приходить во «Входящие».",
            $"Hello {name},\n\nYour CID: {cid}. To confirm this email address, open the link (valid for 48 hours):\n{link}\n\n" +
            "You cannot connect to the network until the address is confirmed. If you did not sign up, just delete this letter.\n\n" +
            "If this letter landed in Spam, mark it as not spam: our next letters will then reach your inbox."));

    public void ResetPassword(string email, string name, long cid, string link) =>
        mailer.Send(email, $"{Network}: смена пароля / password reset", Footer(
            $"Здравствуйте, {name}!\n\nКто-то (надеемся, вы) попросил сменить пароль аккаунта CID {cid}. Новый пароль можно задать по ссылке (действует 1 час):\n{link}\n\n" +
            "Если вы этого не делали, ничего не нажимайте: пароль останется прежним.",
            $"Hello {name},\n\nSomeone (hopefully you) asked to reset the password of CID {cid}. Set a new password here (valid for 1 hour):\n{link}\n\n" +
            "If it was not you, do nothing: your password stays as it is."));

    public void PasswordResetByStaff(long cid) => ToMember(cid, "пароль сброшен", "password reset",
        n => $"Здравствуйте, {n}!\n\nСотрудник сети задал новый пароль вашего аккаунта (CID {cid}) — обычно по вашей просьбе в поддержку. " +
             "Если вы об этом не просили, сразу напишите в поддержку на сайте.",
        n => $"Hello {n},\n\nA staff member set a new password for your account (CID {cid}), usually at your request to support. " +
             "If you did not ask for it, write to support on the website at once.");

    // ---- staff decisions ----------------------------------------------------------------------------

    public void Suspended(long cid, string reason, long? until)
    {
        string whenRu = until is { } u ? $"до {Date(u)}" : "бессрочно";
        string whenEn = until is { } v ? $"until {Date(v)}" : "permanently";
        ToMember(cid, "аккаунт заблокирован", "account suspended",
            n => $"Здравствуйте, {n}!\n\nВаш аккаунт (CID {cid}) заблокирован {whenRu}.\nПричина: {reason}\n\n" +
                 "Пока блокировка действует, войти на сайт и подключиться к сети нельзя. Если вы не согласны с решением, напишите в поддержку на сайте.",
            n => $"Hello {n},\n\nYour account (CID {cid}) is suspended {whenEn}.\nReason: {reason}\n\n" +
                 "While the suspension lasts you cannot sign in or connect to the network. If you disagree, write to support on the website.");
    }

    public void Unsuspended(long cid, bool expired) => ToMember(cid, "блокировка снята", "suspension lifted",
        n => $"Здравствуйте, {n}!\n\n{(expired ? "Срок блокировки вашего аккаунта истёк" : "Блокировка вашего аккаунта снята")} (CID {cid}). " +
             "Вы снова можете входить на сайт и подключаться к сети.",
        n => $"Hello {n},\n\n{(expired ? "The suspension of your account has ended" : "The suspension of your account has been lifted")} (CID {cid}). " +
             "You can sign in and connect to the network again.");

    public void RatingChanged(long cid, string what, string from, string to, bool up) => ToMember(cid,
        up ? "новый рейтинг" : "рейтинг изменён", up ? "new rating" : "rating changed",
        n => $"Здравствуйте, {n}!\n\n{what switch { "pilot" => "Пилотский рейтинг", "military" => "Военный рейтинг", _ => "Рейтинг диспетчера" }} " +
             $"вашего аккаунта (CID {cid}) изменён: {from} → {to}." + (up ? "\n\nПоздравляем!" : ""),
        n => $"Hello {n},\n\n{what switch { "pilot" => "The pilot rating", "military" => "The military rating", _ => "The controller rating" }} " +
             $"of your account (CID {cid}) has changed: {from} → {to}." + (up ? "\n\nCongratulations!" : ""));

    public void StaffRankChanged(long cid, string rank) => ToMember(cid,
        rank.Length > 0 ? "звание в сети" : "звание снято", rank.Length > 0 ? "staff rank" : "staff rank removed",
        n => rank.Length > 0
            ? $"Здравствуйте, {n}!\n\nВам присвоено звание {rank} (CID {cid}). Команды супервайзера доступны в Network-ATC при подключении на позиции {rank}."
            : $"Здравствуйте, {n}!\n\nЗвание сотрудника сети с вашего аккаунта (CID {cid}) снято.",
        n => rank.Length > 0
            ? $"Hello {n},\n\nYou have been given the {rank} rank (CID {cid}). The supervisor commands are available in Network-ATC on a {rank} position."
            : $"Hello {n},\n\nThe staff rank of your account (CID {cid}) has been removed.");

    public void RatingRequestDeclined(long cid, string division, string rating, string comment) => ToMember(cid,
        "заявка на рейтинг отклонена", "rating request declined",
        n => $"Здравствуйте, {n}!\n\nЗаявка дивизиона {division} на рейтинг {rating} (CID {cid}) отклонена.\nКомментарий: {comment}",
        n => $"Hello {n},\n\nThe request of division {division} for the {rating} rating (CID {cid}) has been declined.\nComment: {comment}");

    /// <summary>A staff answer to a support request, sent to the address the request came from.</summary>
    public void TicketAnswered(string email, long ticketId, string subject, string answer, bool member)
    {
        string url = mailer.SiteUrl;
        string link = url.Length > 0 && member ? $"\n\n{url}/account/support/{ticketId}" : "";
        mailer.Send(email, $"{Network}: ответ на обращение #{ticketId} / reply to request #{ticketId}", Footer(
            $"Ответ поддержки на ваше обращение #{ticketId} «{subject}»:\n\n{answer}{link}",
            $"Support replied to your request #{ticketId} \"{subject}\":\n\n{answer}{link}"));
    }
}
