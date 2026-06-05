using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ChatfishApp.Services;

/// <summary>
/// SMTP configuration for sending transactional emails (e.g. magic links).
/// Populate via appsettings "Email" section (or user-secrets / env vars for secrets).
/// Brevo example (port 587 + STARTTLS):
/// "Email": {
///   "SmtpHost": "smtp-relay.brevo.com",
///   "SmtpPort": 587,
///   "SmtpUser": "your-login@smtp-brevo.com",
///   "SmtpPass": "xkeysib-...",
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
    public string From { get; set; } = "no-reply@chatfish.local";
    public bool UseStartTls { get; set; } = true;
}

/// <summary>
/// Abstraction for sending emails. In production you could swap this for SendGrid, Brevo, etc.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends the magic login email containing both a prominent clickable link and the raw URL for copy/paste
    /// (useful when the recipient is not on the same device/browser as the one that requested login).
    /// </summary>
    Task SendMagicLinkEmailAsync(string toEmail, string magicLinkUrl);
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
    }

    public async Task SendMagicLinkEmailAsync(string toEmail, string magicLinkUrl)
    {
        if (string.IsNullOrWhiteSpace(_options.SmtpHost) || string.IsNullOrWhiteSpace(_options.From))
        {
            _logger.LogWarning("[EmailSender] SMTP not configured. Would have sent magic link to {Email}:\n{Link}", toEmail, magicLinkUrl);
            // In dev without config we still allow the flow (link is in server log from the endpoint).
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.From));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Your Chatfish Magic Login Link";

        // Plain text part (always present for clients that prefer text or for copy)
        var textBody = $@"Hello,

You (or someone using your email) requested to log in to Chatfish.

Click the link below or copy and paste it into your browser to sign in:

{magicLinkUrl}

This one-time link expires in 15 minutes.

If you did not request this login link you can safely ignore this email.

-- The Chatfish Team
";

        // HTML part with a big clickable "button" + the raw link shown for easy copy on any device
        var htmlBody = $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"" />
<style>
  body {{ font-family: system-ui, -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, sans-serif; line-height: 1.5; color: #222; max-width: 520px; margin: 0 auto; padding: 16px; }}
  .btn {{ display: inline-block; background: #3BA7FF; color: white !important; padding: 14px 24px; border-radius: 6px; text-decoration: none; font-weight: 600; margin: 12px 0; }}
  .link {{ word-break: break-all; font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, ""Liberation Mono"", ""Courier New"", monospace; background: #f4f4f4; padding: 10px; border-radius: 4px; display: block; margin: 12px 0; }}
  .footer {{ font-size: 0.85em; color: #666; margin-top: 24px; }}
</style>
</head>
<body>
  <p>Hello,</p>
  <p>You (or someone using your email) requested to log in to <strong>Chatfish</strong>.</p>

  <p>Click the button below to sign in:</p>
  <p><a class=""btn"" href=""{magicLinkUrl}"">Log in to Chatfish</a></p>

  <p>Or copy and paste this link (works from any device/browser):</p>
  <p class=""link"">{magicLinkUrl}</p>

  <p style=""font-size:0.9em;"">The link expires in 15 minutes and can only be used once.</p>

  <p style=""font-size:0.9em;"">If you did not request this, you can safely ignore this email.</p>

  <div class=""footer"">-- The Chatfish Team</div>
</body>
</html>";

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
                await smtp.AuthenticateAsync(_options.SmtpUser, _options.SmtpPass);
            }

            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Magic link email sent to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send magic link email to {Email}. Link was: {Link}", toEmail, magicLinkUrl);
            throw; // Let caller decide (usually surface a friendly error to the UI)
        }
    }
}
