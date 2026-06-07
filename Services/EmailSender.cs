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

        // Loud diagnostic so you can see exactly what the options contain vs the live env var
        // (useful when using dotnet watch and env vars set in the shell).
        string liveEnvUser = Environment.GetEnvironmentVariable("Email__SmtpUser")
                          ?? Environment.GetEnvironmentVariable("Email__SmtpUser", EnvironmentVariableTarget.User)
                          ?? Environment.GetEnvironmentVariable("Email:SmtpUser") ?? "(not set)";
        string liveEnvPass = Environment.GetEnvironmentVariable("Email__SmtpPass")
                          ?? Environment.GetEnvironmentVariable("Email__SmtpPass", EnvironmentVariableTarget.User)
                          ?? Environment.GetEnvironmentVariable("Email:SmtpPass") ?? "(not set)";
        int livePassLen = liveEnvPass == "(not set)" ? 0 : liveEnvPass.Length;
        string passPreview = livePassLen >= 8 ? liveEnvPass.Substring(0, 4) + "..." + liveEnvPass.Substring(livePassLen - 4) : liveEnvPass;
        Console.WriteLine($"[SMTP-DIAG][ctor] options.User='{_options.SmtpUser}' | live $env:Email__SmtpUser='{liveEnvUser}' | passLen={livePassLen} preview={passPreview}");
        if (_options.SmtpUser.Contains("smtp-brevo.com") && ! (liveEnvPass.StartsWith("xsmtpsib-") || liveEnvPass.StartsWith("xkeysib-")) )
        {
            Console.WriteLine($"[SMTP-DIAG][ctor] !!! WARNING: The pass we read does not start with xsmtpsib- (Brevo SMTP keys should). It starts with '{liveEnvPass.Substring(0, Math.Min(10, livePassLen))}'");
        }
    }

    public async Task SendMagicLinkEmailAsync(string toEmail, string magicLinkUrl)
    {
        if (string.IsNullOrWhiteSpace(_options.SmtpHost) || string.IsNullOrWhiteSpace(_options.From))
        {
            _logger.LogWarning("[EmailSender] SMTP not configured. Would have sent magic link to {Email}:\n{Link}", toEmail, magicLinkUrl);
            // In dev without config we still allow the flow (link is in server log from the endpoint).
            return;
        }

        _logger.LogInformation("[EmailSender] SMTP ready to send (host={Host}, port={Port}, hasAuthUser={HasUser}, user=\"{User}\", starttls={Tls})", _options.SmtpHost, _options.SmtpPort, !string.IsNullOrWhiteSpace(_options.SmtpUser), _options.SmtpUser, _options.UseStartTls);

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
           // var secure = _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            //await smtp.ConnectAsync(_options.SmtpHost, _options.SmtpPort, secure);


// Force SslOnConnect for port 465, fallback to STARTTLS for others
            var secure = _options.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : (_options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);

            await smtp.ConnectAsync(_options.SmtpHost, _options.SmtpPort, secure);

            if (!string.IsNullOrWhiteSpace(_options.SmtpUser))
            {
                var passLen = _options.SmtpPass?.Length ?? 0;
                if (passLen == 0)
                {
                    _logger.LogWarning("[EmailSender] SmtpUser is set but SmtpPass is empty! Authentication will almost certainly fail.");
                }

                // === THIS IS THE KEY DIAGNOSTIC YOU ASKED FOR ===
                // It shows exactly what we are about to pass to AuthenticateAsync,
                // plus a fresh read of the env var in the same process at the exact moment of the call.
                string liveUserNow = Environment.GetEnvironmentVariable("Email__SmtpUser")
                                  ?? Environment.GetEnvironmentVariable("Email:SmtpUser")
                                  ?? "(not set in env right now)";
                string livePassNow = Environment.GetEnvironmentVariable("Email__SmtpPass")
                                  ?? Environment.GetEnvironmentVariable("Email:SmtpPass")
                                  ?? "(not set in env right now)";
                int livePassLenNow = livePassNow == "(not set in env right now)" ? 0 : livePassNow.Length;
                string passPreviewNow = livePassLenNow >= 8
                    ? livePassNow.Substring(0, 4) + "..." + livePassNow.Substring(livePassLenNow - 4)
                    : livePassNow;

                Console.WriteLine($"[SMTP-DIAG][auth] >>> SENDING TO BREVO <<<");
                Console.WriteLine($"[SMTP-DIAG][auth] options.SmtpUser = '{_options.SmtpUser}'");
                Console.WriteLine($"[SMTP-DIAG][auth] options.SmtpPass length = {passLen}");
                Console.WriteLine($"[SMTP-DIAG][auth] LIVE env at this instant: Email__SmtpUser='{liveUserNow}'");
                Console.WriteLine($"[SMTP-DIAG][auth] LIVE env at this instant: Email__SmtpPass len={livePassLenNow} preview={passPreviewNow}");
                if (_options.SmtpUser.Contains("smtp-brevo.com") && !(_options.SmtpPass?.StartsWith("xsmtpsib-") == true || _options.SmtpPass?.StartsWith("xkeysib-") == true))
                {
                    Console.WriteLine($"[SMTP-DIAG][auth] !!! WARNING: Pass does not start with xsmtpsib- or xkeysib-. Brevo SMTP keys almost always do. Current pass starts with '{_options.SmtpPass?.Substring(0, Math.Min(10, passLen))}'");
                }
                Console.WriteLine($"[SMTP-DIAG][auth] >>> calling AuthenticateAsync now...");

                _logger.LogInformation("[EmailSender] Authenticating with SMTP user \"{User}\" (pass length={PassLen})", _options.SmtpUser, passLen);

                await smtp.AuthenticateAsync(_options.SmtpUser, _options.SmtpPass);
            }

            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Magic link email sent to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            // For auth failures (535 5.7.8 etc.) this will include the attempted user in preceding log lines.
            _logger.LogError(ex, "Failed to send magic link email to {Email} (attempted SMTP user: \"{User}\"). Link was: {Link}", toEmail, _options.SmtpUser, magicLinkUrl);

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
