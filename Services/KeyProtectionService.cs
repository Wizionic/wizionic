using Microsoft.AspNetCore.DataProtection;

namespace ChatfishApp.Services;

/// <summary>
/// Centralized helper for protecting (encrypting at rest) and unprotecting
/// sensitive per-user values such as LocalEncryptionKey and UserProviderKey.Key.
/// Uses the standard ASP.NET Core IDataProtector.
/// </summary>
public class KeyProtectionService
{
    private readonly IDataProtector _protector;

    public KeyProtectionService(IDataProtectionProvider provider)
    {
        // Purpose string isolates these values from other uses of DataProtection in the app.
        _protector = provider.CreateProtector("Chatfish.UserData");
    }

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;
        return _protector.Protect(plaintext);
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return protectedValue;
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch
        {
            // In production you might log and/or treat as invalid (force re-entry of key).
            // For now return empty so callers can treat as "no key configured".
            return string.Empty;
        }
    }
}