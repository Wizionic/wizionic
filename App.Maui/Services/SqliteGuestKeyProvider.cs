using System.Security.Cryptography;
using App.Core.Auth;

namespace App.Maui.Services;

public class SqliteGuestKeyProvider : IGuestKeyProvider
{
    private const string GuestKeySetting = "guest-encryption-key";
    private readonly SqliteSettingsDatabase _db;

    public SqliteGuestKeyProvider(SqliteSettingsDatabase db) => _db = db;

    public async Task<string> GetOrCreateGuestKeyAsync()
    {
        var existing = await _db.GetStringAsync(GuestKeySetting);
        if (!string.IsNullOrEmpty(existing))
            return existing;

        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(bytes);
        await _db.SetStringAsync(GuestKeySetting, b64);
        return b64;
    }
}