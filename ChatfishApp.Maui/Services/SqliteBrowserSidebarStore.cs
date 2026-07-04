using System.Text.Json;
using ChatfishApp.Core.Browser;

namespace ChatfishApp.Maui.Services;

public sealed class SqliteBrowserSidebarStore : IBrowserSidebarStore
{
    private const string AppsKey = "wasm-browser-sidebar-apps";
    private const string SideWidthKey = "wasm-browser-side-panel-width";
    private const string LastTargetKey = "wasm-browser-sidebar-last-targets";
    private const double DefaultSideWidth = 320;
    private const double MinSideWidth = 240;
    private const double MaxSideWidth = 560;

    private readonly SqliteSettingsDatabase _db;
    private List<SidebarApp> _apps = [];
    private double _sidePanelWidth = DefaultSideWidth;
    private Dictionary<string, OpenTarget> _lastTargets = new(StringComparer.OrdinalIgnoreCase);

    public SqliteBrowserSidebarStore(SqliteSettingsDatabase db) => _db = db;

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var appsJson = await _db.GetStringAsync(AppsKey, ct);
        if (!string.IsNullOrEmpty(appsJson))
        {
            var loaded = JsonSerializer.Deserialize<List<SidebarApp>>(appsJson);
            if (loaded != null)
                _apps = loaded;
        }

        var widthStr = await _db.GetStringAsync(SideWidthKey, ct);
        if (double.TryParse(widthStr, out var width))
            _sidePanelWidth = Math.Clamp(width, MinSideWidth, MaxSideWidth);

        var targetsJson = await _db.GetStringAsync(LastTargetKey, ct);
        if (!string.IsNullOrEmpty(targetsJson))
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, OpenTarget>>(targetsJson);
            if (loaded != null)
                _lastTargets = loaded;
        }
    }

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

        var withOrder = app with { SortOrder = _apps.Count };
        _apps.Add(withOrder);
        await SaveAppsAsync(ct);
        Changed?.Invoke();
        return withOrder;
    }

    public async Task UpdateAppAsync(SidebarApp app, CancellationToken ct = default)
    {
        var index = _apps.FindIndex(a => a.Id == app.Id);
        if (index < 0)
            return;

        _apps[index] = app;
        await SaveAppsAsync(ct);
        Changed?.Invoke();
    }

    public async Task UnpinAppAsync(string id, CancellationToken ct = default)
    {
        if (_apps.RemoveAll(a => a.Id == id) > 0)
        {
            _lastTargets.Remove(id);
            await SaveAppsAsync(ct);
            await SaveLastTargetsAsync(ct);
            Changed?.Invoke();
        }
    }

    public async Task ReorderAppsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            var index = _apps.FindIndex(a => a.Id == id);
            if (index < 0)
                continue;

            var app = _apps[index];
            _apps[index] = app with { SortOrder = i };
        }

        await SaveAppsAsync(ct);
        Changed?.Invoke();
    }

    public double GetSidePanelWidthPx() => _sidePanelWidth;

    public async Task SetSidePanelWidthPxAsync(double widthPx, CancellationToken ct = default)
    {
        _sidePanelWidth = Math.Clamp(widthPx, MinSideWidth, MaxSideWidth);
        await _db.SetStringAsync(SideWidthKey, _sidePanelWidth.ToString("F0"), ct);
        Changed?.Invoke();
    }

    public OpenTarget GetLastOpenTarget(string appId) =>
        _lastTargets.TryGetValue(appId, out var target) ? target : OpenTarget.SidePanel;

    public async Task SetLastOpenTargetAsync(string appId, OpenTarget target, CancellationToken ct = default)
    {
        _lastTargets[appId] = target;
        await SaveLastTargetsAsync(ct);
    }

    private async Task SaveAppsAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_apps);
        await _db.SetStringAsync(AppsKey, json, ct);
    }

    private async Task SaveLastTargetsAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_lastTargets);
        await _db.SetStringAsync(LastTargetKey, json, ct);
    }
}