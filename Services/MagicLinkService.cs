using App.Core.Auth;
using App.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace App.Services;

public class MagicLinkService
{
    private readonly AppDbContext _db;
    private readonly KeyProtectionService _keyProtector;

    // Unambiguous uppercase alphanumeric (no 0/O, 1/I/L). 10 chars ≈ 50 bits.
    private const string LoginCodeChars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int LoginCodeLength = 10;

    public MagicLinkService(AppDbContext db, KeyProtectionService keyProtector)
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
    /// Issues a hashed one-time code. Does not create a User row (that happens on verify)
    /// and never generates or rotates LocalEncryptionKey.
    /// </summary>
    public async Task<string> CreateLoginCodeAsync(string email)
    {
        var normalized = EmailNormalizer.Normalize(email);
        var code = GenerateLoginCode();
        var hash = LoginCodeHasher.Hash(code);
        var expires = DateTime.UtcNow.AddMinutes(15);

        var user = await UserLookup.ByEmailAsync(_db, normalized);
        if (user != null)
        {
            user.MagicLinkToken = hash;
            user.MagicLinkExpiresAt = expires;
        }
        else
        {
            var pending = await _db.PendingLoginCodes.FirstOrDefaultAsync(p => p.Email == normalized);
            if (pending == null)
            {
                pending = new PendingLoginCode { Email = normalized };
                _db.PendingLoginCodes.Add(pending);
            }

            pending.CodeHash = hash;
            pending.ExpiresAt = expires;
            pending.CreatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return code;
    }

    public Task<string> CreateMagicLinkTokenAsync(string email) =>
        CreateLoginCodeAsync(email);

    public async Task<User?> FindByMagicTokenAsync(string token)
    {
        // Clickable magic-link consume is disabled. Kept so old URLs do not 500.
        _ = token;
        return await Task.FromResult<User?>(null);
    }

    public async Task<User?> ValidateLoginCodeAsync(string email, string code)
    {
        var normalizedEmail = EmailNormalizer.Normalize(email);
        var normalizedCode = NormalizeCode(code);
        if (string.IsNullOrEmpty(normalizedEmail) || string.IsNullOrEmpty(normalizedCode))
            return null;

        var user = await UserLookup.ByEmailAsync(_db, normalizedEmail);
        if (user != null)
        {
            if (user.MagicLinkExpiresAt == null || user.MagicLinkExpiresAt < DateTime.UtcNow)
                return null;
            if (!LoginCodeHasher.Matches(user.MagicLinkToken, normalizedCode))
                return null;

            user.MagicLinkToken = null;
            user.MagicLinkExpiresAt = null;
            return await EnsureUserReadyForSignInAsync(user);
        }

        var pending = await _db.PendingLoginCodes.FirstOrDefaultAsync(p => p.Email == normalizedEmail);
        if (pending == null)
            return null;
        if (pending.ExpiresAt < DateTime.UtcNow)
            return null;
        if (!LoginCodeHasher.Matches(pending.CodeHash, normalizedCode))
            return null;

        _db.PendingLoginCodes.Remove(pending);

        // First successful code: create the account. Encryption key is generated once here
        // and never rotated by later logins.
        user = new User
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            CreatedAt = DateTime.UtcNow,
            LocalEncryptionKey = LocalEncryptionKeyService.GenerateRawKeyBase64()
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<User?> ConsumeLoginTokenAsync(User user)
    {
        if (user.MagicLinkExpiresAt == null || user.MagicLinkExpiresAt < DateTime.UtcNow)
            return null;

        user.MagicLinkToken = null;
        user.MagicLinkExpiresAt = null;

        return await EnsureUserReadyForSignInAsync(user);
    }

    /// <summary>
    /// Ensures the user has a usable LocalEncryptionKey, migrating legacy protected keys if needed.
    /// Never generates a replacement key when the stored value is already valid — doing so
    /// would make existing notes/chats undecryptable.
    /// </summary>
    public async Task<User?> EnsureUserReadyForSignInAsync(User user)
    {
        if (string.IsNullOrEmpty(user.LocalEncryptionKey))
            user.LocalEncryptionKey = LocalEncryptionKeyService.GenerateRawKeyBase64();
        else
        {
            var resolved = LocalEncryptionKeyService.ResolveStoredKey(user.LocalEncryptionKey, _keyProtector, out var migrate);
            if (resolved == null)
            {
                Console.WriteLine($"[Auth] User {user.Email} has a corrupted LocalEncryptionKey; login blocked until fixed.");
                return null;
            }

            if (migrate)
                user.LocalEncryptionKey = resolved;
        }

        if (!string.Equals(user.Email, EmailNormalizer.Normalize(user.Email), StringComparison.Ordinal))
            user.Email = EmailNormalizer.Normalize(user.Email);

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
