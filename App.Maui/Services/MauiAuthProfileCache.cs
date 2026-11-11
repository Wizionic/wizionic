using System.Text;
using System.Text.Json;
using App.Core.Auth;
#if WINDOWS
using System.Security.Cryptography;
#endif

namespace App.Maui.Services;

/// <summary>
/// Saves the last successful sign-in next to the auth cookie jar so a restart
/// with no route to the login server can still open local notes.
/// On Windows the JSON is DPAPI-wrapped for the current user.
/// </summary>
public sealed class MauiAuthProfileCache : IAuthProfileCache
{
    private const string StorageKey = "app-auth-profile";
    private const string DpapiPrefix = "dpapi:";

    private readonly SqliteSettingsDatabase _db;

    public MauiAuthProfileCache(SqliteSettingsDatabase db) => _db = db;

    public async Task SaveAsync(AuthProfileSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot);
        await _db.SetStringAsync(StorageKey, Protect(json));
    }

    public async Task<AuthProfileSnapshot?> LoadAsync()
    {
        var stored = await _db.GetStringAsync(StorageKey);
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        var json = Unprotect(stored);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<AuthProfileSnapshot>(json);
    }

    public Task ClearAsync() => _db.RemoveAsync(StorageKey);

    private static string Protect(string json)
    {
#if WINDOWS
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(protectedBytes);
#else
        return json;
#endif
    }

    private static string? Unprotect(string stored)
    {
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            return stored;

#if WINDOWS
        try
        {
            var protectedBytes = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
            var raw = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] Could not read saved sign-in: {ex.Message}");
            return null;
        }
#else
        Console.WriteLine("[Auth] Saved sign-in was wrapped for Windows and cannot be read here.");
        return null;
#endif
    }
}
