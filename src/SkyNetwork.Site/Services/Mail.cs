using System.Threading.Channels;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace SkyNetwork.Site.Services;

/// <summary>
/// Settings from the "Mail" section: the SMTP server letters are sent through. Keep the password out of the
/// repository: put it in appsettings.Production.json on the server or in the environment (Mail__Password).
/// With no <see cref="Host"/> the site sends nothing and does not ask members to confirm their email.
/// </summary>
public sealed class MailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>"StartTls" (port 587), "Ssl" (port 465), "None", or "Auto".</summary>
    public string Security { get; set; } = "Auto";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Sender address, e.g. noreply@example.com; the username when empty.</summary>
    public string From { get; set; } = "";
    public string FromName { get; set; } = "SkyNetwork";
    /// <summary>Public address of the website for links in letters, e.g. https://example.com (else the address it was reached at).</summary>
    public string SiteUrl { get; set; } = "";

    public bool Enabled => Host.Trim().Length > 0;
}

/// <summary>A letter to one address, plain text.</summary>
public sealed record Letter(string To, string Subject, string Body);

/// <summary>Delivers a letter (the SMTP server in production; tests record them).</summary>
public interface IMailSender
{
    Task SendAsync(Letter letter, CancellationToken ct);
}

public sealed class SmtpMailSender(IOptions<MailOptions> options) : IMailSender
{
    public async Task SendAsync(Letter letter, CancellationToken ct)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.From.Length > 0 ? o.From : o.Username));
        message.To.Add(MailboxAddress.Parse(letter.To));
        message.Subject = letter.Subject;
        message.Body = new TextPart("plain") { Text = letter.Body };

        using var smtp = new SmtpClient();
        var security = o.Security.ToLowerInvariant() switch
        {
            "starttls" => SecureSocketOptions.StartTls,
            "ssl" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        await smtp.ConnectAsync(o.Host.Trim(), o.Port, security, ct);
        if (o.Username.Length > 0) await smtp.AuthenticateAsync(o.Username, o.Password, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}

/// <summary>
/// Letters go out in the background, so a page never waits for the mail server; a failed letter is tried
/// three times and then logged.
/// </summary>
public sealed class Mailer(IOptions<MailOptions> options, IMailSender sender, ILogger<Mailer> log) : BackgroundService
{
    private readonly Channel<Letter> _queue = Channel.CreateUnbounded<Letter>();

    public bool Enabled => options.Value.Enabled;
    public string SiteUrl => options.Value.SiteUrl.TrimEnd('/');

    /// <summary>When a letter last went out (for the status page).</summary>
    public DateTime? LastSent { get; private set; }
    /// <summary>The last letter that could not be sent after all tries, and why (for the status page).</summary>
    public (DateTime At, string Error)? LastFailure { get; private set; }
    /// <summary>Letters waiting to go out.</summary>
    public int Waiting => _queue.Reader.Count;

    /// <summary>Queues a letter; nothing happens when mail is not set up or there is no address.</summary>
    public void Send(string? to, string subject, string body)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(to)) return;
        _queue.Writer.TryWrite(new Letter(to.Trim(), subject, body));
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var letter in _queue.Reader.ReadAllAsync(stop))
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await sender.SendAsync(letter, stop);
                    LastSent = DateTime.UtcNow;
                    log.LogInformation("Mail \"{Subject}\" sent to {To}", letter.Subject, letter.To);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt >= 3)
                    {
                        LastFailure = (DateTime.UtcNow, ex.Message);
                        log.LogWarning(ex, "Mail \"{Subject}\" to {To} not sent", letter.Subject, letter.To);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(10 * attempt), stop);
                }
            }
        }
    }
}
