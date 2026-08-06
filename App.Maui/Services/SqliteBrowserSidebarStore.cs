using System.Text.Json;
using App.Core.Auth;
using App.Core.Browser;
using App.Core.Storage;

namespace App.Maui.Services;

public sealed class SqliteBrowserSidebarStore : IBrowserSidebarStore
{
    private const string AppsKey = "wasm-browser-sidebar-apps";
    private const string SideWidthKey = "wasm-browser-side-panel-width";
    private const string LastTargetKey = "wasm-browser-sidebar-last-targets";
    private const string AppMetaKey = "wasm-browser-sidebar-app-meta";
    private const double DefaultSideWidth = 320;
    private const double MinSideWidth = 240;
    private const double MaxSideWidth = 560;

    private readonly SqliteSettingsDatabase _db;
    private readonly IAuthService _auth;
    private List<SidebarApp> _apps = [];
    private double _sidePanelWidth = DefaultSideWidth;
    private Dictionary<string, OpenTarget> _lastTargets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SyncMeta> _appMeta = new(StringComparer.Ordinal);

    private sealed record SyncMeta(string ContentFingerprint, long LastUpdatedTicks, long? DeletedAtTicks = null);

    public SqliteBrowserSidebarStore(SqliteSettingsDatabase db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
        _auth.OnChanged += () => _ = LoadAsync();
    }

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _apps = [];
        _sidePanelWidth = DefaultSideWidth;
        _lastTargets = new(StringComparer.OrdinalIgnoreCase);
        _appMeta = new(StringComparer.Ordinal);

        var appsJson = await GetItemAsync(AppsKey, ct);
        if (!string.IsNullOrEmpty(appsJson))
        {
            var loaded = JsonSerializer.Deserialize<List<SidebarApp>>(appsJson);
            if (loaded != null)
            {
                // Heal Linux Uri bug: root-relative start_url ("/") was stored as file:///.
                var healed = loaded.Select(PwaManifestHelper.HealPinnedApp).ToList();
                _apps = healed;
                if (!healed.SequenceEqual(loaded))
                    await SaveAppsAsync(ct);
            }
        }

        var widthStr = await GetItemAsync(SideWidthKey, ct);
        if (double.TryParse(widthStr, out var width))
            _sidePanelWidth = Math.Clamp(width, MinSideWidth, MaxSideWidth);

        var targetsJson = await GetItemAsync(LastTargetKey, ct);
        if (!string.IsNullOrEmpty(targetsJson))
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, OpenTarget>>(targetsJson);
            if (loaded != null)
                _lastTargets = loaded;
        }

        _appMeta = await LoadMetaAsync(ct);
        Changed?.Invoke();
    }

    private string Prefixed(string baseKey) => StorageNamespace.PrefixedKey(_auth, baseKey);

    private async Task<string?> GetItemAsync(string baseKey, CancellationToken ct = default)
    {
        var nk = Prefixed(baseKey);
        var value = await _db.GetStringAsync(nk, ct);
        if (value != null)
            return value;

        var legacy = await _db.GetStringAsync(baseKey, ct);
        if (legacy != null)
        {
            await _db.SetStringAsync(nk, legacy, ct);
            return legacy;
        }

        return null;
    }

    private Task SetItemAsync(string baseKey, string? value, CancellationToken ct = default) =>
        _db.SetStringAsync(Prefixed(baseKey), value, ct);

    public IReadOnlyList<SidebarApp> GetPinnedApps() =>
        _apps.OrderBy(a => a.SortOrder).ThenBy(a => a.PinnedAt).ToList();

    public SidebarApp? FindPinnedByUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();
        return _apps.FirstOrDefault(a => a.StartUrl.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SidebarApp> PinAppAsync(SidebarApp app, CancellationToken ct = default)
    {
        var existing = _apps.FirstOrDefault(a => a.Id == app.Id);
        if (existing != null)
        {
            await UpdateAppAsync(app, ct);
            return app;
        }

        var now = DateTime.UtcNow;
        var withOrder = app with
        {
            SortOrder = _apps.Count,
            UpdatedAtUtc = app.UpdatedAtUtc ?? now
        };
        _apps.Add(withOrder);
        if (!withOrder.IsBuiltIn)
            UpsertLiveAppMeta(withOrder);
        await SaveAppsAsync(ct);
        await SaveAppMetaAsync(ct);
        Changed?.Invoke();
        return withOrder;
    }

    public async Task UpdateAppAsync(SidebarApp app, CancellationToken ct = default)
    {
        var index = _apps.FindIndex(a => a.Id == app.Id);
        if (index < 0)
            return;

        var updated = app with { UpdatedAtUtc = app.UpdatedAtUtc ?? DateTime.UtcNow };
        _apps[index] = updated;
        if (!updated.IsBuiltIn)
            UpsertLiveAppMeta(updated);
        await SaveAppsAsync(ct);
        await SaveAppMetaAsync(ct);
        Changed?.Invoke();
    }

    public async Task UnpinAppAsync(string id, CancellationToken ct = default)
    {
        await TombstoneSidebarAppAsync(id, ct);
    }

    public async Task ReorderAppsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            var index = _apps.FindIndex(a => a.Id == id);
            if (index < 0)
                continue;

            var updated = _apps[index] with { SortOrder = i, UpdatedAtUtc = now };
            _apps[index] = updated;
            if (!updated.IsBuiltIn)
                UpsertLiveAppMeta(updated);
        }

        await SaveAppsAsync(ct);
        await SaveAppMetaAsync(ct);
        Changed?.Invoke();
    }

    public double GetSidePanelWidthPx() => _sidePanelWidth;

    public async Task SetSidePanelWidthPxAsync(double widthPx, CancellationToken ct = default)
    {
        _sidePanelWidth = Math.Clamp(widthPx, MinSideWidth, MaxSideWidth);
        await SetItemAsync(SideWidthKey, _sidePanelWidth.ToString("F0"), ct);
        Changed?.Invoke();
    }

    public OpenTarget GetLastOpenTarget(string appId) =>
        _lastTargets.TryGetValue(appId, out var target) ? target : OpenTarget.SidePanel;

    public async Task SetLastOpenTargetAsync(string appId, OpenTarget target, CancellationToken ct = default)
    {
        _lastTargets[appId] = target;
        await SaveLastTargetsAsync(ct);
    }

    // --- Sync ---

    public Task<List<SyncManifestEntry>> LoadSidebarAppManifestEntriesAsync(
        bool backfillMissingFingerprints = false,
        CancellationToken ct = default)
    {
        var entries = new List<SyncManifestEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var app in _apps.Where(a => !a.IsBuiltIn))
        {
            seen.Add(app.Id);
            var fingerprint = SyncFingerprint.ForSidebarApp(app);
            var ticks = app.EffectiveUpdatedAtUtc.Ticks;
            if (backfillMissingFingerprints
                || !_appMeta.TryGetValue(app.Id, out var meta)
                || meta.DeletedAtTicks.HasValue
                || meta.ContentFingerprint != fingerprint)
            {
                _appMeta[app.Id] = new SyncMeta(fingerprint, ticks);
            }

            entries.Add(new SyncManifestEntry(
                app.Id,
                string.IsNullOrWhiteSpace(app.Name) ? app.StartUrl : app.Name,
                ticks,
                fingerprint));
        }

        foreach (var (id, meta) in _appMeta)
        {
            if (seen.Contains(id) || !meta.DeletedAtTicks.HasValue)
                continue;

            entries.Add(new SyncManifestEntry(
                id,
                "(deleted)",
                meta.LastUpdatedTicks,
                DeleteSyncPayload.AckValue(meta.DeletedAtTicks.Value),
                meta.DeletedAtTicks));
        }

        return Task.FromResult(entries);
    }

    public Task<SidebarApp?> GetAppByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_apps.FirstOrDefault(a => a.Id == id && !a.IsBuiltIn));

    public async Task ApplySidebarAppPayloadAsync(SidebarApp app, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(app.Id) || app.IsBuiltIn)
            return;

        var index = _apps.FindIndex(a => a.Id == app.Id);
        if (index >= 0)
            _apps[index] = app;
        else
            _apps.Add(app);

        _appMeta.Remove(app.Id);
        UpsertLiveAppMeta(app);
        await SaveAppsAsync(ct);
        await SaveAppMetaAsync(ct);
        Changed?.Invoke();
    }

    public Task<bool> ShouldAcceptIncomingSidebarAppAsync(SidebarApp app, CancellationToken ct = default)
    {
        if (app.IsBuiltIn)
            return Task.FromResult(false);

        if (_appMeta.TryGetValue(app.Id, out var meta) && meta.DeletedAtTicks.HasValue)
            return Task.FromResult(app.EffectiveUpdatedAtUtc.Ticks > meta.DeletedAtTicks.Value);

        var local = _apps.FirstOrDefault(a => a.Id == app.Id);
        if (local == null)
            return Task.FromResult(true);

        return Task.FromResult(app.EffectiveUpdatedAtUtc.Ticks >= local.EffectiveUpdatedAtUtc.Ticks);
    }

    public async Task<DateTime> TombstoneSidebarAppAsync(string id, CancellationToken ct = default)
    {
        var deletedAt = DateTime.UtcNow;
        var app = _apps.FirstOrDefault(a => a.Id == id);
        if (app?.IsBuiltIn == true)
            return deletedAt;

        var removed = _apps.RemoveAll(a => a.Id == id) > 0;
        if (!removed && _appMeta.TryGetValue(id, out var existing) && existing.DeletedAtTicks.HasValue)
            return new DateTime(existing.DeletedAtTicks.Value, DateTimeKind.Utc);

        _lastTargets.Remove(id);
        _appMeta[id] = new SyncMeta(
            DeleteSyncPayload.AckValue(deletedAt.Ticks),
            deletedAt.Ticks,
            deletedAt.Ticks);

        if (removed)
        {
            await SaveAppsAsync(ct);
            await SaveLastTargetsAsync(ct);
        }

        await SaveAppMetaAsync(ct);
        if (removed)
            Changed?.Invoke();
        return deletedAt;
    }

    public async Task<bool> TryApplyRemoteSidebarAppDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default)
    {
        var local = _apps.FirstOrDefault(a => a.Id == id);
        if (local?.IsBuiltIn == true)
            return false;

        if (_appMeta.TryGetValue(id, out var meta) && meta.DeletedAtTicks.HasValue)
        {
            if (meta.DeletedAtTicks.Value >= deletedAtTicks)
                return false;
        }
        else
        {
            if (local != null && local.EffectiveUpdatedAtUtc.Ticks > deletedAtTicks)
                return false;
            if (local == null && meta is null)
                return false;
        }

        _apps.RemoveAll(a => a.Id == id);
        _lastTargets.Remove(id);
        _appMeta[id] = new SyncMeta(
            DeleteSyncPayload.AckValue(deletedAtTicks),
            deletedAtTicks,
            deletedAtTicks);
        await SaveAppsAsync(ct);
        await SaveLastTargetsAsync(ct);
        await SaveAppMetaAsync(ct);
        Changed?.Invoke();
        return true;
    }

    private void UpsertLiveAppMeta(SidebarApp app)
    {
        _appMeta[app.Id] = new SyncMeta(
            SyncFingerprint.ForSidebarApp(app),
            app.EffectiveUpdatedAtUtc.Ticks);
    }

    private async Task SaveAppsAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_apps);
        await SetItemAsync(AppsKey, json, ct);
    }

    private async Task SaveLastTargetsAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_lastTargets);
        await SetItemAsync(LastTargetKey, json, ct);
    }

    private async Task SaveAppMetaAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_appMeta);
        await SetItemAsync(AppMetaKey, json, ct);
    }

    private async Task<Dictionary<string, SyncMeta>> LoadMetaAsync(CancellationToken ct)
    {
        var json = await GetItemAsync(AppMetaKey, ct);
        if (string.IsNullOrEmpty(json))
            return new Dictionary<string, SyncMeta>(StringComparer.Ordinal);

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, SyncMeta>>(json);
            return loaded != null
                ? new Dictionary<string, SyncMeta>(loaded, StringComparer.Ordinal)
                : new Dictionary<string, SyncMeta>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, SyncMeta>(StringComparer.Ordinal);
        }
    }
}
