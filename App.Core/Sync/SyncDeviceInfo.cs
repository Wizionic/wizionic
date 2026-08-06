namespace App.Core.Sync;

/// <summary>
/// Lightweight device shape for sync UI (matches SyncHub broadcasts).
/// </summary>
public record SyncDeviceInfo(
    string DeviceId,
    string Name,
    DateTime LastActiveUtc,
    bool IsOnline,
    bool CanRelayAi = false,
    int AiModelCount = 0,
    bool SupportsBrowserSync = false);
