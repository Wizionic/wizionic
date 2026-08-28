namespace App.Core.Storage;

public record StorageQuotaSettings(
    /// <summary>Percent of available capacity (10–90). Default 50.</summary>
    double PercentOfAvailable = 50,
    /// <summary>Optional hard ceiling in bytes (null = none).</summary>
    long? HardCapBytes = null);

public record StorageQuotaSnapshot(
    long AppUsageBytes,
    /// <summary>Browser origin quota (WASM) or free disk on data volume (MAUI).</summary>
    long AvailableCapacityBytes,
    long EffectiveLimitBytes,
    /// <summary>"browser-quota" | "disk-free" | "unknown"</summary>
    string CapacitySource,
    long ChatHistoryBytes = 0,
    long NotesBytes = 0,
    long GalleryBytes = 0,
    long OtherBytes = 0,
    long NoteAudioBytes = 0);

public interface IStorageQuotaService
{
    Task<StorageQuotaSettings> GetSettingsAsync(CancellationToken ct = default);
    Task SetSettingsAsync(StorageQuotaSettings settings, CancellationToken ct = default);
    Task<StorageQuotaSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task<bool> CanAcceptBytesAsync(long additionalBytes, CancellationToken ct = default);
    Task<long> MeasureAppUsageBytesAsync(CancellationToken ct = default);
    /// <summary>Reclaim disk after large deletes (SQLite VACUUM on MAUI; no-op or IDB compact attempt on WASM).</summary>
    Task CompactAsync(CancellationToken ct = default);
}
