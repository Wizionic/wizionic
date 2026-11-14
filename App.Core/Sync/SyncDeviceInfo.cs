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
    bool SupportsBrowserSync = false,
    /// <summary>True for MAUI/native desktop-mobile clients; false for WASM browser clients.</summary>
    bool IsNativeApp = false,
    /// <summary>Skills this device is offering. Null when the peer has not published a card.</summary>
    List<AgentSkillCard>? Skills = null,
    /// <summary>True when this device holds the account's orchestrator lease.</summary>
    bool IsOrchestrator = false,
    /// <summary>A2A protocol version this device speaks, when it has published a card.</summary>
    string? AgentProtocol = null);
