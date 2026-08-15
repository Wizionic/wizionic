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
    /// Sends the unified login email with a copy/paste code (web + native) and an optional web magic link.
    /// </summary>
    Task SendLoginEmailAsync(string toEmail, string loginCode, string magicLinkUrl);
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

    public async Task SendLoginEmailAsync(string toEmail, string loginCode, string magicLinkUrl)
    {
        if (string.IsNullOrWhiteSpace(_options.SmtpHost) || string.IsNullOrWhiteSpace(_options.From))
        {
            _logger.LogWarning("[EmailSender] SMTP not configured; login email not sent.");
            return;
        }

        _logger.LogInformation("[EmailSender] SMTP ready to send (host={Host}, port={Port}, hasAuthUser={HasUser}, user=\"{User}\", starttls={Tls})", _options.SmtpHost, _options.SmtpPort, !string.IsNullOrWhiteSpace(_options.SmtpUser), _options.SmtpUser, _options.UseStartTls);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.From));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = LoginEmailContent.Subject;

        var (textBody, htmlBody) = LoginEmailContent.Build(loginCode, magicLinkUrl);

        var bodyBuilder = new BodyBuilder
        {
            TextBody = textBody,
            HtmlBody = htmlBody
        };
        message.Body = bodyBuilder.ToMessageBody();

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

            _logger.LogInformation("Login email sent to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            // For auth failures (535 5.7.8 etc.) this will include the attempted user in preceding log lines.
            _logger.LogError(ex, "Failed to send login email.");

            // Extra guidance on the console for the common Brevo 535 case.
            var msg = (ex.InnerException?.ToString() ?? ex.ToString());
            if (ex is MailKit.Security.AuthenticationException ||
                msg.Contains("5.7.8") || msg.Contains("Authentication failed") || msg.Contains("535"))
            {
                _logger.LogWarning("[EmailSender] SMTP authentication failed (535 5.7.8). Check: 1) the value of $env:Email__SmtpUser (or user-secrets) matches your Brevo SMTP login exactly, 2) the pass is the full generated SMTP key (xsmtpsib-...), 3) you are running the server from a shell where those env vars are defined, 4) the key is still valid in Brevo dashboard.");
            }

            throw; // Let caller decide (usually surface a friendly error to the UI)
        }
    }
}
