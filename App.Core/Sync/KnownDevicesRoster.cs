namespace App.Core.Sync;

/// <summary>
/// Locally remembered peer device (per signed-in user). Server presence is ephemeral;
/// the client keeps a roster so devices still appear offline after restarts / absences.
/// </summary>
public sealed class KnownDeviceRecord
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "Device";
    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
    public bool CanRelayAi { get; set; }
    public int AiModelCount { get; set; }
    public bool SupportsBrowserSync { get; set; }
    public bool IsNativeApp { get; set; }
}

/// <summary>
/// Merges live hub presence with the local known-devices roster (and optional AI-server stub).
/// </summary>
public static class DeviceListMerger
{
    public static IReadOnlyList<SyncDeviceInfo> Merge(
        IEnumerable<SyncDeviceInfo>? fromServer,
        IEnumerable<KnownDeviceRecord>? known,
        string? ensureDeviceId = null,
        string? ensureDeviceName = null)
    {
        var map = new Dictionary<string, SyncDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        if (fromServer != null)
        {
            foreach (var d in fromServer)
            {
                if (string.IsNullOrWhiteSpace(d.DeviceId))
                    continue;
                map[d.DeviceId] = d;
            }
        }

        if (known != null)
        {
            foreach (var k in known)
            {
                if (string.IsNullOrWhiteSpace(k.DeviceId) || map.ContainsKey(k.DeviceId))
                    continue;

                map[k.DeviceId] = new SyncDeviceInfo(
                    k.DeviceId,
                    string.IsNullOrWhiteSpace(k.Name) ? "Device" : k.Name,
                    k.LastActiveUtc == default ? DateTime.UtcNow : k.LastActiveUtc,
                    IsOnline: false,
                    k.CanRelayAi,
                    k.AiModelCount,
                    k.SupportsBrowserSync,
                    k.IsNativeApp);
            }
        }

        // Ensure the selected AI server (or any other sticky id) still appears so the user can uncheck it.
        if (!string.IsNullOrWhiteSpace(ensureDeviceId) && !map.ContainsKey(ensureDeviceId))
        {
            map[ensureDeviceId] = new SyncDeviceInfo(
                ensureDeviceId,
                string.IsNullOrWhiteSpace(ensureDeviceName) ? "Previously used device" : ensureDeviceName.Trim(),
                DateTime.UtcNow,
                IsOnline: false);
        }

        return map.Values
            .OrderByDescending(d => d.IsOnline)
            .ThenByDescending(d => d.LastActiveUtc)
            .ToList();
    }

    /// <summary>
    /// Upserts live devices into the known roster (names/capabilities from the server win when online).
    /// </summary>
    public static List<KnownDeviceRecord> UpsertFromServer(
        IEnumerable<KnownDeviceRecord>? existing,
        IEnumerable<SyncDeviceInfo>? fromServer)
    {
        var map = new Dictionary<string, KnownDeviceRecord>(StringComparer.OrdinalIgnoreCase);

        if (existing != null)
        {
            foreach (var k in existing)
            {
                if (string.IsNullOrWhiteSpace(k.DeviceId))
                    continue;
                map[k.DeviceId] = k;
            }
        }

        if (fromServer != null)
        {
            foreach (var d in fromServer)
            {
                if (string.IsNullOrWhiteSpace(d.DeviceId))
                    continue;

                if (map.TryGetValue(d.DeviceId, out var prev))
                {
                    map[d.DeviceId] = new KnownDeviceRecord
                    {
                        DeviceId = d.DeviceId,
                        Name = string.IsNullOrWhiteSpace(d.Name) ? prev.Name : d.Name,
                        LastActiveUtc = d.LastActiveUtc > prev.LastActiveUtc ? d.LastActiveUtc : prev.LastActiveUtc,
                        CanRelayAi = d.IsOnline ? d.CanRelayAi : (d.CanRelayAi || prev.CanRelayAi),
                        AiModelCount = d.IsOnline
                            ? d.AiModelCount
                            : Math.Max(d.AiModelCount, prev.AiModelCount),
                        SupportsBrowserSync = d.IsOnline
                            ? d.SupportsBrowserSync
                            : (d.SupportsBrowserSync || prev.SupportsBrowserSync),
                        IsNativeApp = d.IsOnline
                            ? d.IsNativeApp
                            : (d.IsNativeApp || prev.IsNativeApp)
                    };
                }
                else
                {
                    map[d.DeviceId] = new KnownDeviceRecord
                    {
                        DeviceId = d.DeviceId,
                        Name = string.IsNullOrWhiteSpace(d.Name) ? "Device" : d.Name,
                        LastActiveUtc = d.LastActiveUtc,
                        CanRelayAi = d.CanRelayAi,
                        AiModelCount = d.AiModelCount,
                        SupportsBrowserSync = d.SupportsBrowserSync,
                        IsNativeApp = d.IsNativeApp
                    };
                }
            }
        }

        return map.Values.ToList();
    }

    public static List<KnownDeviceRecord> Remember(
        IEnumerable<KnownDeviceRecord>? existing,
        string deviceId,
        string? name)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return existing?.ToList() ?? new List<KnownDeviceRecord>();

        var list = UpsertFromServer(existing, Array.Empty<SyncDeviceInfo>());
        var idx = list.FindIndex(k => string.Equals(k.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            if (!string.IsNullOrWhiteSpace(name))
                list[idx].Name = name.Trim();
        }
        else
        {
            list.Add(new KnownDeviceRecord
            {
                DeviceId = deviceId,
                Name = string.IsNullOrWhiteSpace(name) ? "Previously used device" : name.Trim(),
                LastActiveUtc = DateTime.UtcNow
            });
        }

        return list;
    }
}
