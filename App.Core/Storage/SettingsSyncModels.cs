using System.Text.Json;

namespace App.Core.Storage;

/// <summary>
/// One settings category blob transferred over the WebRTC data channel.
/// </summary>
public sealed record SettingsSyncPayload(
    string Category,
    long UpdatedTicks,
    string DataJson)
{
    private static readonly JsonSerializerOptions JsonOpts = SyncJson.Options;

    public static string Serialize(string category, long updatedTicks, string dataJson) =>
        JsonSerializer.Serialize(new SettingsSyncPayload(category, updatedTicks, dataJson), JsonOpts);

    public static SettingsSyncPayload? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<SettingsSyncPayload>(json, JsonOpts);
            return string.IsNullOrWhiteSpace(payload?.Category) ? null : payload;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Export/import contract for settings categories (KeyStore-backed + appearance).
/// </summary>
public interface ISettingsSyncStore
{
    Task<IReadOnlyList<SyncManifestEntry>> LoadManifestEntriesAsync(
        IEnumerable<string>? categories = null,
        CancellationToken ct = default);

    Task<SettingsSyncPayload?> ExportAsync(string category, CancellationToken ct = default);

    /// <summary>True when remote is newer or local has no value.</summary>
    Task<bool> ShouldAcceptIncomingAsync(SettingsSyncPayload payload, CancellationToken ct = default);

    Task ApplyAsync(SettingsSyncPayload payload, CancellationToken ct = default);

    /// <summary>Call after a local settings save so timestamps + auto-sync can run.</summary>
    Task TouchCategoryAsync(string category, CancellationToken ct = default);

    event Action? OnSettingsChanged;
}
