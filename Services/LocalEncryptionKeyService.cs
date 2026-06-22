using System.Security.Cryptography;

namespace ChatfishApp.Services;

/// <summary>
/// Per-user AES key for WASM/MAUI IndexedDB encryption and live sync.
/// Stored as plaintext base64 (32 bytes) in Users.LocalEncryptionKey.
/// Not wrapped with ASP.NET Data Protection — client crypto depends only on this stable value.
/// </summary>
public static class LocalEncryptionKeyService
{
    public static string GenerateRawKeyBase64()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    public static bool IsValidRawKeyBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 32;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the usable key. Legacy rows may still hold a DataProtection-wrapped value;
    /// those are unwrapped once and should be rewritten as plaintext by the caller.
    /// </summary>
    public static string? ResolveStoredKey(string? stored, KeyProtectionService? legacyProtector, out bool migrateLegacyProtectedValue)
    {
        migrateLegacyProtectedValue = false;
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        if (IsValidRawKeyBase64(stored))
            return stored;

        if (legacyProtector == null)
            return null;

        var unprotected = legacyProtector.Unprotect(stored);
        if (!IsValidRawKeyBase64(unprotected))
            return null;

        migrateLegacyProtectedValue = true;
        return unprotected;
    }
}