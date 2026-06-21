using ChatfishApp.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace ChatfishApp.Services;

public class MagicLinkService
{
    private readonly ChatfishDbContext _db;
    private readonly KeyProtectionService _keyProtector;

    // Unambiguous uppercase alphanumeric (no 0/O, 1/I/L).
    private const string LoginCodeChars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int LoginCodeLength = 8;

    public MagicLinkService(ChatfishDbContext db, KeyProtectionService keyProtector)
    {
        _db = db;
        _keyProtector = keyProtector;
    }

    private static string GenerateLoginCode()
    {
        Span<char> buffer = stackalloc char[LoginCodeLength];
        for (int i = 0; i < LoginCodeLength; i++)
        {
            var index = RandomNumberGenerator.GetInt32(LoginCodeChars.Length);
            buffer[i] = LoginCodeChars[index];
        }

        return new string(buffer);
    }

    /// <summary>
    /// Generates a fresh random key (base64) that WASM/MAUI clients will use for AES-GCM
    /// encryption of their local history blobs and of blobs transferred during live
    /// (both-devices-open) cross-device sync. The *protected* (server at-rest) form
    /// is what we store in the DB.
    /// </summary>
    private static string GenerateEncryptionKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32); // 256-bit key for AES-GCM
        return Convert.ToBase64String(bytes);
    }

    public async Task<string> CreateMagicLinkTokenAsync(string email)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user == null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                CreatedAt = DateTime.UtcNow
            };

            _db.Users.Add(user);
        }

        if (string.IsNullOrEmpty(user.LocalEncryptionKey))
        {
            var rawKey = GenerateEncryptionKey();
            user.LocalEncryptionKey = _keyProtector.Protect(rawKey);
        }

        user.MagicLinkToken = GenerateLoginCode();
        user.MagicLinkExpiresAt = DateTime.UtcNow.AddMinutes(15);

        await _db.SaveChangesAsync();

        return user.MagicLinkToken;
    }

    public async Task<User?> ValidateMagicLinkAsync(string token)
    {
        var normalizedToken = NormalizeCode(token);
        if (string.IsNullOrEmpty(normalizedToken))
            return null;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.MagicLinkToken == normalizedToken);
        if (user == null)
            return null;

        return await ConsumeLoginTokenAsync(user);
    }

    public async Task<User?> ValidateLoginCodeAsync(string email, string code)
    {
        var normalizedEmail = email.Trim();
        var normalizedCode = NormalizeCode(code);
        if (string.IsNullOrEmpty(normalizedEmail) || string.IsNullOrEmpty(normalizedCode))
            return null;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (user == null)
            return null;

        if (!string.Equals(user.MagicLinkToken, normalizedCode, StringComparison.Ordinal))
            return null;

        return await ConsumeLoginTokenAsync(user);
    }

    private async Task<User?> ConsumeLoginTokenAsync(User user)
    {
        if (user.MagicLinkExpiresAt == null || user.MagicLinkExpiresAt < DateTime.UtcNow)
            return null;

        user.MagicLinkToken = null;
        user.MagicLinkExpiresAt = null;

        bool needsNewKey = string.IsNullOrEmpty(user.LocalEncryptionKey);
        if (!needsNewKey)
        {
            var tryUnprotect = _keyProtector.Unprotect(user.LocalEncryptionKey!);
            if (string.IsNullOrEmpty(tryUnprotect))
                needsNewKey = true;
        }

        if (needsNewKey)
        {
            var rawKey = GenerateEncryptionKey();
            user.LocalEncryptionKey = _keyProtector.Protect(rawKey);
        }

        await _db.SaveChangesAsync();

        return user;
    }

    private static string NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().ToUpperInvariant();
    }
}