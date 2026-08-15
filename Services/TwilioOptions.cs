namespace App.Services;

/// <summary>
/// Twilio Verify configuration. Secrets belong in env / user-secrets, not git.
/// Authenticate with API Key SID + secret (not the account auth token).
/// </summary>
public sealed class TwilioOptions
{
    public const string SectionName = "Twilio";

    public string AccountSid { get; set; } = "";
    public string ApiKeySid { get; set; } = "";
    public string ApiKeySecret { get; set; } = "";
    public string VerifyServiceSid { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountSid)
        && AccountSid.StartsWith("AC", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(ApiKeySid)
        && ApiKeySid.StartsWith("SK", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(ApiKeySecret)
        && !string.IsNullOrWhiteSpace(VerifyServiceSid)
        && VerifyServiceSid.StartsWith("VA", StringComparison.Ordinal);
}
