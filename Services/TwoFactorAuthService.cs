using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using App.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

/// <summary>
/// Password-verified 2FA challenges (email via existing magic codes, SMS via Twilio Verify).
/// </summary>
public sealed class TwoFactorAuthService
{
    public const int ChallengeMinutes = 10;
    public static readonly TimeSpan SmsCooldown = TimeSpan.FromSeconds(45);

    private static readonly ConcurrentDictionary<Guid, DateTime> SmsSentAt = new();

    private readonly AppDbContext _db;
    private readonly MagicLinkService _magic;
    private readonly IEmailSender _email;
    private readonly ITwilioVerifyService _twilio;

    public TwoFactorAuthService(
        AppDbContext db,
        MagicLinkService magic,
        IEmailSender email,
        ITwilioVerifyService twilio)
    {
        _db = db;
        _magic = magic;
        _email = email;
        _twilio = twilio;
    }

    public bool SmsAvailable => _twilio.IsConfigured;

    public IReadOnlyList<string> MethodsFor(User user)
    {
        var methods = new List<string> { "email" };
        if (SmsAvailable && !string.IsNullOrEmpty(user.TwoFactorPhoneE164))
            methods.Add("sms");
        if (RecoveryCodeService.RemainingCount(user) > 0)
            methods.Add("recovery");
        return methods;
    }

    public string PreferredMethod(User user) =>
        SmsAvailable && !string.IsNullOrEmpty(user.TwoFactorPhoneE164) ? "sms" : "email";

    public bool HasLiveChallenge(User user) =>
        !string.IsNullOrEmpty(user.TwoFactorChallengeHash)
        && user.TwoFactorChallengeExpiresAt is { } exp
        && exp > DateTime.UtcNow;

    public string CreateChallenge(User user)
    {
        var id = NewChallengeId();
        user.TwoFactorChallengeHash = HashChallenge(id);
        user.TwoFactorChallengeExpiresAt = DateTime.UtcNow.AddMinutes(ChallengeMinutes);
        return id;
    }

    public void ClearChallenge(User user)
    {
        user.TwoFactorChallengeHash = null;
        user.TwoFactorChallengeExpiresAt = null;
    }

    public Task PersistAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

    public async Task<User?> FindByChallengeAsync(string? challengeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(challengeId))
            return null;

        var hash = HashChallenge(challengeId.Trim());
        var user = await _db.Users.FirstOrDefaultAsync(u => u.TwoFactorChallengeHash == hash, ct);
        if (user == null || !HasLiveChallenge(user))
            return null;

        return user;
    }

    public async Task<(bool Ok, string? Error)> SendAsync(User user, string? method, HttpContext ctx)
    {
        method = NormalizeMethod(method) ?? PreferredMethod(user);

        if (method == "sms")
        {
            if (!SmsAvailable)
                return (false, "SMS verification is not configured on this server.");
            if (string.IsNullOrEmpty(user.TwoFactorPhoneE164))
                return (false, "No phone number is enrolled.");
            if (!TryAcquireSmsSlot(user.Id))
                return (false, "Wait a moment before requesting another SMS.");

            return await _twilio.StartSmsAsync(user.TwoFactorPhoneE164);
        }

        if (method == "email")
        {
            var code = await _magic.CreateLoginCodeAsync(user.Email);
            var sent = await _email.SendLoginEmailAsync(user.Email, code);
            return sent
                ? (true, null)
                : (false, "Could not send an email code. Try again in a moment.");
        }

        return (false, "Unknown verification method.");
    }

    public async Task<(bool Ok, string? Error)> VerifyAsync(User user, string? method, string? code)
    {
        method = NormalizeMethod(method) ?? PreferredMethod(user);
        if (string.IsNullOrWhiteSpace(code))
            return (false, "Incorrect or expired code.");

        if (method == "sms")
        {
            if (string.IsNullOrEmpty(user.TwoFactorPhoneE164))
                return (false, "No phone number is enrolled.");
            return await _twilio.CheckSmsAsync(user.TwoFactorPhoneE164, code);
        }

        if (method == "email")
        {
            var ready = await _magic.ValidateLoginCodeAsync(user.Email, code);
            return ready != null
                ? (true, null)
                : (false, "Incorrect or expired code.");
        }

        if (method == "recovery")
        {
            if (!RecoveryCodeService.TryConsume(user, code))
                return (false, "Incorrect or expired code.");
            await _db.SaveChangesAsync();
            return (true, null);
        }

        return (false, "Unknown verification method.");
    }

    public async Task<(bool Ok, string? Error)> StartPhoneEnrollmentAsync(string phone)
    {
        var e164 = NormalizeE164(phone);
        if (e164 == null)
            return (false, "Enter a phone number in international format, like +15551234567.");
        if (!SmsAvailable)
            return (false, "SMS verification is not configured on this server.");

        return await _twilio.StartSmsAsync(e164);
    }

    public async Task<(bool Ok, string? Error)> ConfirmPhoneEnrollmentAsync(User user, string phone, string code)
    {
        var e164 = NormalizeE164(phone);
        if (e164 == null)
            return (false, "Enter a phone number in international format, like +15551234567.");
        if (!SmsAvailable)
            return (false, "SMS verification is not configured on this server.");

        var (ok, err) = await _twilio.CheckSmsAsync(e164, code);
        if (!ok)
            return (false, err ?? "Incorrect or expired code.");

        user.TwoFactorPhoneE164 = e164;
        user.TwoFactorEnabled = true;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    public static string? NormalizeMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
            return null;
        var value = method.Trim().ToLowerInvariant();
        return value is "sms" or "email" or "recovery" ? value : null;
    }

    public static string? NormalizeE164(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var compact = Regex.Replace(raw, @"[^\d+]", "");
        if (!compact.StartsWith('+') || compact.Length < 9 || compact.Length > 16)
            return null;

        var digits = compact[1..];
        if (digits.Length is < 8 or > 15 || digits.Any(c => !char.IsDigit(c)))
            return null;

        return "+" + digits;
    }

    public static string? MaskPhone(string? e164)
    {
        if (string.IsNullOrEmpty(e164) || e164.Length < 6)
            return null;

        var last4 = e164[^4..];
        var keep = e164.StartsWith("+1", StringComparison.Ordinal) && e164.Length > 6
            ? 2
            : Math.Min(3, e164.Length - 4);
        var bullets = Math.Max(4, e164.Length - keep - 4);
        return e164[..keep] + new string('•', bullets) + last4;
    }

    public static string NewChallengeId()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string HashChallenge(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(hash);
    }

    private static bool TryAcquireSmsSlot(Guid userId)
    {
        var now = DateTime.UtcNow;
        if (SmsSentAt.TryGetValue(userId, out var last) && now - last < SmsCooldown)
            return false;
        SmsSentAt[userId] = now;
        return true;
    }
}
