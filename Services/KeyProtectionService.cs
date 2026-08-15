using Microsoft.AspNetCore.DataProtection;

namespace App.Services;

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
        _protector = provider.CreateProtector("Wizionic.UserData");
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
            return string.Empty;
        }
    }

    /// <summary>
    /// Prefer a protected value; if it was stored raw, return it unchanged.
    /// </summary>
    public string UnprotectOrPlain(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;
        var unprotected = Unprotect(stored);
        return string.IsNullOrEmpty(unprotected) ? stored : unprotected;
    }
}