using Microsoft.Extensions.Options;
using SkyNetwork.Site.Data;

namespace SkyNetwork.Site.Services;

/// <summary>
/// A registration counts once the email is confirmed: accounts left unconfirmed after <see cref="SiteOptions.UnconfirmedHours"/>
/// (whose last link has expired) are deleted, checked every hour.
/// </summary>
public sealed class UnconfirmedAccounts(MemberService members, AccountMail mail, UploadStore uploads, IOptions<SiteOptions> options,
                                        ILogger<UnconfirmedAccounts> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        int hours = options.Value.UnconfirmedHours;
        if (hours <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                if (!mail.Enabled) continue;
                foreach (var (cid, avatar) in members.DeleteUnconfirmed(TimeSpan.FromHours(hours)))
                {
                    uploads.Delete(avatar);
                    log.LogInformation("Registration {Cid} deleted: the email was not confirmed", cid);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not delete unconfirmed registrations");
            }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
