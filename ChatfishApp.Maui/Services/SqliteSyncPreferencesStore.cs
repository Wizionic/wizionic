using ChatfishApp.Core.Sync;

namespace ChatfishApp.Maui.Services;

/// <summary>Wraps <see cref="SqliteSettingsDatabase"/> for sync coordinator preferences.</summary>
public sealed class SqliteSyncPreferencesStore : ISyncPreferencesStore
{
    private readonly SqliteSettingsDatabase _settings;

    public SqliteSyncPreferencesStore(SqliteSettingsDatabase settings) => _settings = settings;

    public Task<string?> GetStringAsync(string key, CancellationToken ct = default) =>
        _settings.GetStringAsync(key, ct);

    public Task SetStringAsync(string key, string? value, CancellationToken ct = default) =>
        _settings.SetStringAsync(key, value, ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) =>
        _settings.RemoveAsync(key, ct);
}