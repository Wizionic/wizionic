using System.Collections.Concurrent;

namespace ChatfishApp.Services;

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

    public record DeviceInfo(string DeviceId, string Name, DateTime LastActiveUtc, bool IsOnline);

    private class DeviceEntry
    {
        public string Name { get; set; } = "Browser";
        public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
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

            // If this was the last connection for the device, the device is now "offline"
            // but we keep the record so the UI can still show it with a last-active time.
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
                IsOnline: isOnline
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
}
