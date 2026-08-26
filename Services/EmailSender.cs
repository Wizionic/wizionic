using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace App.Services;

/// <summary>
/// SMTP configuration for sending transactional emails (e.g. magic links).
/// Populate via appsettings "Email" section (or user-secrets / env vars for secrets).
/// Brevo example (port 587 + STARTTLS):
/// "Email": {
///   "SmtpHost": "smtp-relay.brevo.com",
///   "SmtpPort": 587,
///   "SmtpUser": "your-login@smtp-brevo.com",
///   "SmtpPass": "<paste-the-REAL-full-key-from-Brevo-SMTP-keys-table>",
///   "From": "your-verified-sender@yourdomain.com",   // must be verified in Brevo!
///   "UseStartTls": true
/// }
/// </summary>
public class EmailOptions
{
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPass { get; set; } = string.Empty;
    public string From { get; set; } = "no-reply@app.local";
    public bool UseStartTls { get; set; } = true;
}

/// <summary>
/// Abstraction for sending emails. In production you could swap this for SendGrid, Brevo, etc.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends the login email with a copy/paste code (web + native). No sign-in URL.
    /// Returns false when the message could not be handed to any provider.
    /// </summary>
    Task<bool> SendLoginEmailAsync(string toEmail, string loginCode);

    /// <summary>
    /// Best-effort security notice. Failures are logged; callers must not fail the user action.
    /// </summary>
    Task<bool> SendSecurityNoticeAsync(string toEmail, string subject, string textBody, string htmlBody);
}

/// <summary>
/// MailKit-based implementation of IEmailSender.
/// Creates a fresh SmtpClient per send (stateless and safe).
/// </summary>
public class EmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IOptions<EmailOptions> options, ILogger<EmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;

        // Belt-and-suspenders: force SmtpUser/SmtpPass from the environment variables
        // (Email__SmtpUser / Email__SmtpPass) when they are defined for this process.
        // This makes the explicitly-set env vars win over user-secrets / appsettings.
        string? envUser = Environment.GetEnvironmentVariable("Email__SmtpUser")
                       ?? Environment.GetEnvironmentVariable("Email__SmtpUser", EnvironmentVariableTarget.User)
                       ?? Environment.GetEnvironmentVariable("Email:SmtpUser");
        if (envUser is not null)
        {
            _options.SmtpUser = envUser.Trim();
        }

        string? envPass = Environment.GetEnvironmentVariable("Email__SmtpPass")
                       ?? Environment.GetEnvironmentVariable("Email__SmtpPass", EnvironmentVariableTarget.User)
                       ?? Environment.GetEnvironmentVariable("Email:SmtpPass");
        if (envPass is not null)
        {
            _options.SmtpPass = envPass.Trim();
        }
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.SmtpHost) && !string.IsNullOrWhiteSpace(_options.From);

    public Task<bool> SendLoginEmailAsync(string toEmail, string loginCode)
    {
        var (text, html) = LoginEmailContent.Build(loginCode);
        return SendAsync(toEmail, LoginEmailContent.Subject, text, html, "login");
    }

    public Task<bool> SendSecurityNoticeAsync(string toEmail, string subject, string textBody, string htmlBody) =>
        SendAsync(toEmail, subject, textBody, htmlBody, "security");

    private async Task<bool> SendAsync(string toEmail, string subject, string textBody, string htmlBody, string kind)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("[EmailSender] SMTP not configured; {Kind} email not sent.", kind);
            return false;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.From));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = textBody, HtmlBody = htmlBody }.ToMessageBody();

        using var smtp = new SmtpClient();
        try
        {
            var secure = _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            await smtp.ConnectAsync(_options.SmtpHost, _options.SmtpPort, secure);

            if (!string.IsNullOrWhiteSpace(_options.SmtpUser))
            {
                if (string.IsNullOrEmpty(_options.SmtpPass))
                    _logger.LogWarning("[EmailSender] SmtpUser is set but SmtpPass is empty.");

                await smtp.AuthenticateAsync(_options.SmtpUser, _options.SmtpPass);
            }

            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);
            _logger.LogInformation("{Kind} email sent via SMTP to {Email}", kind, toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send {Kind} email via SMTP.", kind);
            return false;
        }
    }
}
