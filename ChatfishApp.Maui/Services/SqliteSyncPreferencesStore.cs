using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Storage;
using ChatfishApp.Core.Sync;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// SQLite settings for sync coordinator preferences (per-user prefix).
/// </summary>
public sealed class SqliteSyncPreferencesStore : ISyncPreferencesStore
{
    private readonly SqliteSettingsDatabase _settings;
    private readonly IAuthService _auth;

    public SqliteSyncPreferencesStore(SqliteSettingsDatabase settings, IAuthService auth)
    {
        _settings = settings;
        _auth = auth;
    }

    private string Prefixed(string key) => StorageNamespace.PrefixedKey(_auth, key);

    public async Task<string?> GetStringAsync(string key, CancellationToken ct = default)
    {
        var nk = Prefixed(key);
        var value = await _settings.GetStringAsync(nk, ct);
        if (value != null)
            return value;

        var legacy = await _settings.GetStringAsync(key, ct);
        if (legacy != null)
        {
            await _settings.SetStringAsync(nk, legacy, ct);
            return legacy;
        }

        return null;
    }

    public Task SetStringAsync(string key, string? value, CancellationToken ct = default) =>
        _settings.SetStringAsync(Prefixed(key), value, ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) =>
        _settings.RemoveAsync(Prefixed(key), ct);
}
