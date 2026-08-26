using Microsoft.Extensions.Logging;

namespace App.Services;

/// <summary>
/// Tries Brevo first, then SMTP. Login send returns false only if both fail.
/// Security notices never throw.
/// </summary>
public sealed class FallbackEmailSender : IEmailSender
{
    private readonly BrevoEmailSender _brevo;
    private readonly EmailSender _smtp;
    private readonly ILogger<FallbackEmailSender> _logger;

    public FallbackEmailSender(BrevoEmailSender brevo, EmailSender smtp, ILogger<FallbackEmailSender> logger)
    {
        _brevo = brevo;
        _smtp = smtp;
        _logger = logger;
    }

    public async Task<bool> SendLoginEmailAsync(string toEmail, string loginCode)
    {
        if (_brevo.IsConfigured)
        {
            var ok = await TryLogin(_brevo, toEmail, loginCode, "Brevo");
            if (ok)
                return true;
            _logger.LogWarning("[Email] Brevo login send failed; trying SMTP.");
        }

        if (_smtp.IsConfigured)
            return await TryLogin(_smtp, toEmail, loginCode, "SMTP");

        _logger.LogWarning("[Email] No email provider configured; login code not sent.");
        return false;
    }

    public async Task<bool> SendSecurityNoticeAsync(string toEmail, string subject, string textBody, string htmlBody)
    {
        try
        {
            if (_brevo.IsConfigured)
            {
                var ok = await _brevo.SendSecurityNoticeAsync(toEmail, subject, textBody, htmlBody);
                if (ok)
                    return true;
            }

            if (_smtp.IsConfigured)
                return await _smtp.SendSecurityNoticeAsync(toEmail, subject, textBody, htmlBody);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Email] Security notice not sent to {Email}.", toEmail);
        }

        return false;
    }

    private async Task<bool> TryLogin(IEmailSender sender, string toEmail, string loginCode, string name)
    {
        try
        {
            return await sender.SendLoginEmailAsync(toEmail, loginCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Email] {Provider} login send threw.", name);
            return false;
        }
    }
}
