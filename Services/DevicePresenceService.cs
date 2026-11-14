using System.Collections.Concurrent;
using App.Core.Agents;
using App.Core.Sync;

namespace App.Services;

/// <summary>
/// In-memory presence tracking for WASM clients (authenticated users only).
/// Used by SyncHub to provide a live "Devices" list with online status (green dot)
/// and Last Active timestamps.
///
/// This is intentionally ephemeral (no DB persistence). Per the roadmap, live sync
/// only works while both devices are open ("both-devices-open" / Brave model).
/// The server acts purely as auth + signaling coordinator; it never sees chat content.
///
/// Each browser profile generates a stable DeviceId (stored in localStorage / IDB settings).
/// Multiple tabs in the same browser profile share the same DeviceId and are coalesced
/// into a single logical device entry (IsOnline = true if any connection is active).
///
/// Future Phase 2 will use this same hub/connection for WebRTC signaling offers/answers/ICE
/// (server relays only the small signaling messages; actual encrypted history blobs flow P2P).
/// </summary>
public class DevicePresenceService
{
    // userKey -> (deviceId -> entry)
    // userKey is the stable User.Id (GUID string) when available, falling back to a hashed email.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DeviceEntry>> _users = new();

    // connectionId -> (userKey, deviceId) for fast removal on disconnect
    private readonly ConcurrentDictionary<string, (string UserKey, string DeviceId)> _connections = new();

    public record DeviceInfo(
        string DeviceId,
        string Name,
        DateTime LastActiveUtc,
        bool IsOnline,
        bool CanRelayAi = false,
        int AiModelCount = 0,
        bool SupportsBrowserSync = false,
        bool IsNativeApp = false,
        List<AgentSkillCard>? Skills = null,
        bool IsOrchestrator = false,
        string? AgentProtocol = null);

    private class DeviceEntry
    {
        public string Name { get; set; } = "Browser";
        public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
        public bool CanRelayAi { get; set; }
        public int AiModelCount { get; set; }
        public bool SupportsBrowserSync { get; set; }
        public bool IsNativeApp { get; set; }
        public List<AgentSkillCard> Skills { get; set; } = new();
        public bool IsOrchestrator { get; set; }
        public string? AgentProtocol { get; set; }
        public HashSet<string> ConnectionIds { get; } = new(StringComparer.Ordinal);
    }

    private static string GetUserKey(string? userId, string? email)
    {
        if (!string.IsNullOrWhiteSpace(userId))
            return $"u:{userId}";
        if (!string.IsNullOrWhiteSpace(email))
            return $"e:{email.ToLowerInvariant()}";
        return "anon";
    }

    /// <summary>
    /// Called when a client connects/registers. Creates or updates the device entry,
    /// associates the SignalR connectionId, and returns the current snapshot for the user.
    /// </summary>
    public IReadOnlyList<DeviceInfo> Register(string? userId, string? email, string deviceId, string deviceName, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            deviceId = Guid.NewGuid().ToString("N");

        var userKey = GetUserKey(userId, email);
        var bucket = _users.GetOrAdd(userKey, _ => new ConcurrentDictionary<string, DeviceEntry>(StringComparer.OrdinalIgnoreCase));

        var entry = bucket.GetOrAdd(deviceId, _ => new DeviceEntry());
        entry.Name = string.IsNullOrWhiteSpace(deviceName) ? entry.Name : deviceName;
        entry.LastActiveUtc = DateTime.UtcNow;
        entry.ConnectionIds.Add(connectionId);

        _connections[connectionId] = (userKey, deviceId);

        return GetDevicesSnapshot(userKey);
    }

    /// <summary>
    /// Update AI relay capability for a device (Ollama / API keys available on that browser).
    /// </summary>
    public IReadOnlyList<DeviceInfo> UpdateAiCapabilities(string? userId, string? email, string deviceId, int modelCount)
    {
        var userKey = GetUserKey(userId, email);
        if (_users.TryGetValue(userKey, out var bucket) &&
            bucket.TryGetValue(deviceId, out var entry))
        {
            entry.AiModelCount = Math.Max(0, modelCount);
            entry.CanRelayAi = entry.AiModelCount > 0;
            entry.LastActiveUtc = DateTime.UtcNow;
        }
        return GetDevicesSnapshot(userKey);
    }

    /// <summary>
    /// Clients report client kind and browser bookmark/PWA sync support.
    /// <paramref name="supportsBrowserSync"/> is true for MAUI (native browser features).
    /// <paramref name="isNativeApp"/> is true for MAUI/native, false for WASM browsers.
    /// </summary>
    public IReadOnlyList<DeviceInfo> UpdateBrowserCapabilities(
        string? userId,
        string? email,
        string deviceId,
        bool supportsBrowserSync,
        bool isNativeApp = false)
    {
        var userKey = GetUserKey(userId, email);
        if (_users.TryGetValue(userKey, out var bucket) &&
            bucket.TryGetValue(deviceId, out var entry))
        {
            entry.SupportsBrowserSync = supportsBrowserSync;
            entry.IsNativeApp = isNativeApp;
            entry.LastActiveUtc = DateTime.UtcNow;
        }
        return GetDevicesSnapshot(userKey);
    }

    /// <summary>
    /// Publish the device's agent card and optionally claim or release the orchestrator lease.
    /// Skill updates do not change the lease. A claim is refused while another online device holds it.
    /// An offline holder is not a holder: the flag is cleared when that device has no connections.
    /// </summary>
    public (IReadOnlyList<DeviceInfo> Devices, bool Accepted) UpdateAgentCard(
        string? userId,
        string? email,
        string deviceId,
        IReadOnlyList<AgentSkillCard>? skills,
        bool claimOrchestrator,
        bool releaseOrchestrator)
    {
        var userKey = GetUserKey(userId, email);
        var accepted = true;
        if (_users.TryGetValue(userKey, out var bucket) &&
            bucket.TryGetValue(deviceId, out var entry))
        {
            foreach (var other in bucket.Values)
            {
                if (other.ConnectionIds.Count == 0)
                    other.IsOrchestrator = false;
            }

            if (skills is not null)
                entry.Skills = CapSkills(skills);

            entry.AgentProtocol = A2AChannel.ProtocolVersion;
            entry.LastActiveUtc = DateTime.UtcNow;

            if (releaseOrchestrator)
                entry.IsOrchestrator = false;

            if (claimOrchestrator)
            {
                var held = bucket.Values.FirstOrDefault(o =>
                    !ReferenceEquals(o, entry) && o.IsOrchestrator && o.ConnectionIds.Count > 0);
                if (held is not null)
                    accepted = false;
                else
                    entry.IsOrchestrator = true;
            }
        }
        else if (claimOrchestrator)
        {
            accepted = false;
        }

        return (GetDevicesSnapshot(userKey), accepted);
    }

    /// <summary>
    /// Update just the friendly name for a device (user-initiated rename on this client).
    /// </summary>
    public IReadOnlyList<DeviceInfo> UpdateName(string? userId, string? email, string deviceId, string newName)
    {
        var userKey = GetUserKey(userId, email);
        if (_users.TryGetValue(userKey, out var bucket) &&
            bucket.TryGetValue(deviceId, out var entry))
        {
            if (!string.IsNullOrWhiteSpace(newName))
            {
                entry.Name = newName.Trim();
                entry.LastActiveUtc = DateTime.UtcNow;
            }
        }
        return GetDevicesSnapshot(userKey);
    }

    /// <summary>
    /// Lightweight heartbeat from client to keep LastActive fresh while the tab is open.
    /// </summary>
    public void Heartbeat(string? userId, string? email, string deviceId, string connectionId)
    {
        var userKey = GetUserKey(userId, email);
        if (_users.TryGetValue(userKey, out var bucket) &&
            bucket.TryGetValue(deviceId, out var entry))
        {
            entry.LastActiveUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(connectionId))
                entry.ConnectionIds.Add(connectionId); // idempotent
        }
    }

    /// <summary>
    /// Called from hub OnDisconnectedAsync. Removes the connection; if no connections remain
    /// for the device we leave the entry (with IsOnline=false) so "Last Active" history is useful.
    /// </summary>
    public IReadOnlyList<DeviceInfo>? OnDisconnected(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var info))
            return null;

        var (userKey, deviceId) = info;

        if (_users.TryGetValue(userKey, out var bucket) &&
            bucket.TryGetValue(deviceId, out var entry))
        {
            entry.ConnectionIds.Remove(connectionId);
            entry.LastActiveUtc = DateTime.UtcNow; // last-seen moment

            // Offline devices do not keep the orchestrator lease.
            if (entry.ConnectionIds.Count == 0)
                entry.IsOrchestrator = false;
        }

        return GetDevicesSnapshot(userKey);
    }

    public IReadOnlyList<DeviceInfo> GetDevices(string? userId, string? email)
    {
        var userKey = GetUserKey(userId, email);
        return GetDevicesSnapshot(userKey);
    }

    /// <summary>
    /// Returns the current active SignalR connection IDs for a specific device.
    /// Used by the hub to route signaling / sync payloads to the correct peer(s).
    /// </summary>
    public IReadOnlyList<string> GetActiveConnectionIds(string? userId, string? email, string deviceId)
    {
        var userKey = GetUserKey(userId, email);
        if (_users.TryGetValue(userKey, out var bucket) &&
            bucket.TryGetValue(deviceId, out var entry))
        {
            return entry.ConnectionIds.ToList();
        }
        return Array.Empty<string>();
    }

    private IReadOnlyList<DeviceInfo> GetDevicesSnapshot(string userKey)
    {
        if (!_users.TryGetValue(userKey, out var bucket) || bucket.IsEmpty)
            return Array.Empty<DeviceInfo>();

        var now = DateTime.UtcNow;
        var list = new List<DeviceInfo>(bucket.Count);

        foreach (var (deviceId, entry) in bucket)
        {
            bool isOnline = entry.ConnectionIds.Count > 0;
            list.Add(new DeviceInfo(
                DeviceId: deviceId,
                Name: entry.Name,
                LastActiveUtc: entry.LastActiveUtc,
                IsOnline: isOnline,
                CanRelayAi: entry.CanRelayAi,
                AiModelCount: entry.AiModelCount,
                SupportsBrowserSync: entry.SupportsBrowserSync,
                IsNativeApp: entry.IsNativeApp,
                Skills: entry.Skills.Count == 0 ? null : entry.Skills.Select(CloneCard).ToList(),
                IsOrchestrator: isOnline && entry.IsOrchestrator,
                AgentProtocol: entry.AgentProtocol
            ));
        }

        // Sort: online first, then by most recently active
        return list
            .OrderByDescending(d => d.IsOnline)
            .ThenByDescending(d => d.LastActiveUtc)
            .ToList();
    }

    /// <summary>
    /// For diagnostics / future admin UI.
    /// </summary>
    public int GetOnlineConnectionCount() => _connections.Count;

    private static List<AgentSkillCard> CapSkills(IReadOnlyList<AgentSkillCard> skills)
    {
        var list = new List<AgentSkillCard>();
        foreach (var skill in skills)
        {
            if (list.Count >= 40)
                break;
            if (string.IsNullOrWhiteSpace(skill.Id) && string.IsNullOrWhiteSpace(skill.Name))
                continue;
            list.Add(new AgentSkillCard
            {
                Id = Trim(skill.Id, 64),
                Name = Trim(skill.Name, 80),
                Description = Trim(skill.Description, 180)
            });
        }
        return list;
    }

    private static AgentSkillCard CloneCard(AgentSkillCard skill) => new()
    {
        Id = skill.Id,
        Name = skill.Name,
        Description = skill.Description
    };

    private static string Trim(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length <= max ? text : text[..max];
    }
}
