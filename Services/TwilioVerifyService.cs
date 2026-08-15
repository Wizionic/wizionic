using Microsoft.Extensions.Options;
using Twilio.Clients;
using Twilio.Rest.Verify.V2.Service;

namespace App.Services;

public interface ITwilioVerifyService
{
    bool IsConfigured { get; }
    Task<(bool Ok, string? Error)> StartSmsAsync(string e164, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> CheckSmsAsync(string e164, string code, CancellationToken ct = default);
}

/// <summary>
/// Twilio Verify wrapper. Uses API Key SID + secret + Account SID (never the auth token).
/// </summary>
public sealed class TwilioVerifyService : ITwilioVerifyService
{
    private readonly TwilioOptions _options;
    private readonly ILogger<TwilioVerifyService> _logger;

    public TwilioVerifyService(IOptions<TwilioOptions> options, ILogger<TwilioVerifyService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<(bool Ok, string? Error)> StartSmsAsync(string e164, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return (false, "SMS verification is not configured on this server.");

        try
        {
            var verification = await VerificationResource.CreateAsync(
                to: e164,
                channel: "sms",
                pathServiceSid: _options.VerifyServiceSid,
                client: CreateClient());

            var pending = string.Equals(verification.Status, "pending", StringComparison.OrdinalIgnoreCase);
            return pending
                ? (true, null)
                : (false, "Could not send an SMS code. Try again.");
        }
        catch (Twilio.Exceptions.ApiException)
        {
            return (false, "Could not send an SMS code. Check the number and try again.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twilio Verify start failed.");
            return (false, "Could not send an SMS code.");
        }
    }

    public async Task<(bool Ok, string? Error)> CheckSmsAsync(string e164, string code, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return (false, "SMS verification is not configured on this server.");

        if (string.IsNullOrWhiteSpace(code))
            return (false, "Incorrect or expired code.");

        try
        {
            var check = await VerificationCheckResource.CreateAsync(
                to: e164,
                code: code.Trim(),
                pathServiceSid: _options.VerifyServiceSid,
                client: CreateClient());

            var approved = string.Equals(check.Status, "approved", StringComparison.OrdinalIgnoreCase)
                || check.Valid == true;
            return approved
                ? (true, null)
                : (false, "Incorrect or expired code.");
        }
        catch (Twilio.Exceptions.ApiException)
        {
            return (false, "Incorrect or expired code.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twilio Verify check failed.");
            return (false, "Could not check the SMS code.");
        }
    }

    private ITwilioRestClient CreateClient() =>
        new TwilioRestClient(_options.ApiKeySid, _options.ApiKeySecret, accountSid: _options.AccountSid);
}
