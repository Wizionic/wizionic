using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

namespace App.Services;

/// <summary>
/// Brevo (Sendinblue) HTTP API configuration for sending transactional emails.
/// The API key must be provided via the BREVO_API_KEY environment variable (preferred),
/// or Email__BrevoApiKey / Email:BrevoApiKey.
/// 
/// Example appsettings (non-secret parts only):
/// "Brevo": {
///   "From": "no-reply@wizionic.com",
///   "SenderName": "Wizionic"
/// }
/// 
/// Set the secret via environment variable:
///   BREVO_API_KEY= xkeysib-...   (or the v3 API key from Brevo)
/// </summary>
public class BrevoEmailOptions
{
    public string From { get; set; } = "no-reply@app.local";
    public string SenderName { get; set; } = "Wizionic";
}

/// <summary>
/// Brevo HTTP API implementation of IEmailSender.
/// Uses POST https://api.brevo.com/v3/smtp/email (transactional email endpoint).
/// Leaves the original SMTP EmailSender untouched for fallback / other deployments.
/// </summary>
public class BrevoEmailSender : IEmailSender
{
    private readonly BrevoEmailOptions _options;
    private readonly ILogger<BrevoEmailSender> _logger;
    private readonly IHttpClientFactory _httpFactory;

    public BrevoEmailSender(
        IOptions<BrevoEmailOptions> options,
        ILogger<BrevoEmailSender> logger,
        IHttpClientFactory httpFactory)
    {
        _options = options.Value;
        _logger = logger;
        _httpFactory = httpFactory;
    }

    private string? ReadApiKeyFromEnv()
    {
        // Preferred clean name
        string? key = Environment.GetEnvironmentVariable("BREVO_API_KEY");

        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable("Email__BrevoApiKey");

        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable("Email__BrevoApiKey", EnvironmentVariableTarget.User);

        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable("Email:BrevoApiKey");

        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ReadApiKeyFromEnv()) && !string.IsNullOrWhiteSpace(_options.From);

    public Task<bool> SendLoginEmailAsync(string toEmail, string loginCode)
    {
        var (text, html) = LoginEmailContent.Build(loginCode);
        return SendAsync(toEmail, LoginEmailContent.Subject, text, html, "login");
    }

    public Task<bool> SendSecurityNoticeAsync(string toEmail, string subject, string textBody, string htmlBody) =>
        SendAsync(toEmail, subject, textBody, htmlBody, "security");

    private async Task<bool> SendAsync(string toEmail, string subject, string textBody, string htmlBody, string kind)
    {
        string? apiKey = ReadApiKeyFromEnv();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(_options.From))
        {
            _logger.LogWarning("[BrevoEmailSender] Not configured; {Kind} email not sent.", kind);
            return false;
        }

        var payload = new
        {
            sender = new { name = _options.SenderName, email = _options.From },
            to = new[] { new { email = toEmail } },
            subject,
            htmlContent = htmlBody,
            textContent = textBody
        };

        try
        {
            var client = _httpFactory.CreateClient("brevo");
            using var request = new HttpRequestMessage(HttpMethod.Post, "smtp/email")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("api-key", apiKey);

            var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                string? messageId = null;
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("messageId", out var mid))
                        messageId = mid.GetString();
                }
                catch { /* best effort */ }

                _logger.LogInformation("{Kind} email sent via Brevo (messageId: {MessageId})", kind, messageId ?? "(none)");
                return true;
            }

            _logger.LogError("[BrevoEmailSender] Brevo API returned {Status} for {Kind} to {Email}. Body: {Body}",
                (int)response.StatusCode, kind, toEmail, responseBody);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send {Kind} email to {Email} via Brevo HTTP API", kind, toEmail);
            return false;
        }
    }
}
