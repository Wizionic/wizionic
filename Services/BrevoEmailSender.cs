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

    public async Task SendLoginEmailAsync(string toEmail, string loginCode, string magicLinkUrl)
    {
        string? apiKey = ReadApiKeyFromEnv();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("[BrevoEmailSender] BREVO_API_KEY not configured; login email not sent.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.From))
        {
            _logger.LogWarning("[BrevoEmailSender] No From address configured. Cannot send email to {Email}.", toEmail);
            return;
        }

        _logger.LogInformation("[BrevoEmailSender] Sending login email via Brevo HTTP API.");

        var (textBody, htmlBody) = LoginEmailContent.Build(loginCode, magicLinkUrl);

        var payload = new
        {
            sender = new
            {
                name = _options.SenderName,
                email = _options.From
            },
            to = new[]
            {
                new { email = toEmail }
            },
            subject = LoginEmailContent.Subject,
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
            // 'accept' and 'content-type' are handled by JsonContent + defaults on the named client

            var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                // Brevo typically returns 201 with something like {"messageId": "..."}
                string? messageId = null;
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("messageId", out var mid))
                        messageId = mid.GetString();
                }
                catch { /* best effort */ }

                _logger.LogInformation("Login email sent via Brevo (messageId: {MessageId})", messageId ?? "(none)");
            }
            else
            {
                _logger.LogError("[BrevoEmailSender] Brevo API returned {Status} for {Email}. Body: {Body}", (int)response.StatusCode, toEmail, responseBody);

                // Common Brevo errors: 401 unauthorized (bad key), 400 (unverified sender, invalid payload), 422, etc.
                if ((int)response.StatusCode == 401 || responseBody.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || responseBody.Contains("invalid api key", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[BrevoEmailSender] Authentication failed (401). Check that BREVO_API_KEY is a valid Brevo v3 API key (starts with xkeysib- usually) and has the 'Transactional' permission.");
                }
                else if (responseBody.Contains("verified", StringComparison.OrdinalIgnoreCase) || responseBody.Contains("sender", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[BrevoEmailSender] Sender address '{From}' may not be verified in your Brevo account. Verify the domain or single address in Brevo dashboard.", _options.From);
                }

                // Throw so the caller can surface a useful error (same behavior as the SMTP sender)
                throw new InvalidOperationException($"Brevo email send failed with status {(int)response.StatusCode}: {responseBody}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to send login email to {Email} via Brevo HTTP API", toEmail);
            throw;
        }
    }
}
