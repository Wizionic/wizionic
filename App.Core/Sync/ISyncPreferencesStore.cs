namespace App.Core.Sync;

/// <summary>
/// Key-value persistence for sync metadata (ack state, manifest timestamps, etc.).
/// Browser uses localStorage; MAUI uses SQLite settings.
/// </summary>
public interface ISyncPreferencesStore
{
    Task<string?> GetStringAsync(string key, CancellationToken ct = default);
    Task SetStringAsync(string key, string? value, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}