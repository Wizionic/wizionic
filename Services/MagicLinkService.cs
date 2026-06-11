using ChatfishApp.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace ChatfishApp.Services;

public class MagicLinkService
{
    private readonly ChatfishDbContext _db;
    private readonly KeyProtectionService _keyProtector;

    public MagicLinkService(ChatfishDbContext db, KeyProtectionService keyProtector)
    {
        _db = db;
        _keyProtector = keyProtector;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Generates a fresh random key (base64) that WASM clients will use for AES-GCM
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

        // Ensure the per-user encryption key for WASM live sync + local blob encryption exists.
        // This key is what authenticated WASM clients fetch from the DB so that multiple
        // simultaneously-open devices for the same email can securely transfer (and locally
        // store) history without the server ever seeing or persisting the plaintext content.
        if (string.IsNullOrEmpty(user.LocalEncryptionKey))
        {
            var rawKey = GenerateEncryptionKey();
            user.LocalEncryptionKey = _keyProtector.Protect(rawKey);
        }

        user.MagicLinkToken = GenerateToken();
        user.MagicLinkExpiresAt = DateTime.UtcNow.AddMinutes(15);

        await _db.SaveChangesAsync();

        return user.MagicLinkToken;
    }

    public async Task<User?> ValidateMagicLinkAsync(string token)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.MagicLinkToken == token);

        if (user == null)
            return null;

        if (user.MagicLinkExpiresAt == null || user.MagicLinkExpiresAt < DateTime.UtcNow)
            return null;

        user.MagicLinkToken = null;
        user.MagicLinkExpiresAt = null;

        // Ensure we have a usable (protectable under the *current* DataProtection key ring) per-user encryption key.
        // This covers first-time users, users created before the field, *and* users whose previous protected value
        // can no longer be unprotected (DP ring change, transition to DB storage, dev machine clean, etc.).
        // If we have to overwrite, the client will get a fresh key on the subsequent /api/user/encryption-key call.
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

}
