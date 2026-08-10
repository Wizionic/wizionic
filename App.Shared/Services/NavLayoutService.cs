using App.Core.UI;
using Microsoft.JSInterop;

namespace App.Shared.Services;

public sealed class NavLayoutService : INavLayoutState
{
    private NavLayoutMode _mode;
    private bool _showBrowser = true;
    private bool _showNotes = true;
    private bool _showGallery = true;
    private bool _showCalendar = true;
    private bool _secondaryExpanded = true;
    private bool _initialized;

    public NavLayoutService()
    {
        // MAUI default: left rail. Web/WASM: top bar (left layout not used).
        _mode = AppEnvironment.IsMaui ? NavLayoutMode.Left : NavLayoutMode.Top;
    }

    public event Action? OnChanged;

    public NavLayoutMode Mode => _mode;
    public bool ShowBrowser => _showBrowser;
    public bool ShowNotes => _showNotes;
    public bool ShowGallery => _showGallery;
    public bool ShowCalendar => _showCalendar;
    public bool SecondaryExpanded => _secondaryExpanded;

    private NavLayoutMode DefaultMode =>
        AppEnvironment.IsMaui ? NavLayoutMode.Left : NavLayoutMode.Top;

    public async Task InitializeAsync(IJSRuntime js, CancellationToken ct = default)
    {
        if (_initialized)
        {
            await NavLayoutInterop.ApplyAsync(js, _mode, ct);
            return;
        }

        await LoadFromStorageAsync(js, ct);
        _initialized = true;
        await NavLayoutInterop.ApplyAsync(js, _mode, ct);
        OnChanged?.Invoke();
    }

    public async Task SetModeAsync(NavLayoutMode mode, IJSRuntime js, CancellationToken ct = default)
    {
        if (_mode == mode && _initialized)
            return;

        _mode = mode;
        await PersistAsync(js, ct);
        await NavLayoutInterop.ApplyAsync(js, _mode, ct);
        OnChanged?.Invoke();
    }

    public async Task SetPrimaryIconVisibleAsync(NavPrimaryIcon icon, bool visible, IJSRuntime js, CancellationToken ct = default)
    {
        var changed = icon switch
        {
            NavPrimaryIcon.Browser => Set(ref _showBrowser, visible),
            NavPrimaryIcon.Notes => Set(ref _showNotes, visible),
            NavPrimaryIcon.Gallery => Set(ref _showGallery, visible),
            NavPrimaryIcon.Calendar => Set(ref _showCalendar, visible),
            _ => false
        };

        if (!changed && _initialized)
            return;

        await PersistAsync(js, ct);
        OnChanged?.Invoke();
    }

    public async Task SetSecondaryExpandedAsync(bool expanded, IJSRuntime js, CancellationToken ct = default)
    {
        if (_secondaryExpanded == expanded && _initialized)
            return;

        _secondaryExpanded = expanded;
        await PersistAsync(js, ct);
        OnChanged?.Invoke();
    }

    public async Task SyncFromStorageAndApplyAsync(IJSRuntime js, CancellationToken ct = default)
    {
        var before = Snapshot();
        await LoadFromStorageAsync(js, ct);
        _initialized = true;
        await NavLayoutInterop.ApplyAsync(js, _mode, ct);
        if (before != Snapshot())
            OnChanged?.Invoke();
    }

    private async Task LoadFromStorageAsync(IJSRuntime js, CancellationToken ct)
    {
        try
        {
            var prefs = await NavLayoutInterop.GetPrefsAsync(js, ct);
            ApplyDto(prefs, DefaultMode);
        }
        catch
        {
            _mode = DefaultMode;
            _showBrowser = _showNotes = _showGallery = _showCalendar = true;
            _secondaryExpanded = true;
        }
    }

    private void ApplyDto(NavLayoutInterop.NavLayoutPrefsDto? prefs, NavLayoutMode defaultMode)
    {
        if (prefs is null)
        {
            _mode = defaultMode;
            _showBrowser = _showNotes = _showGallery = _showCalendar = true;
            _secondaryExpanded = true;
            return;
        }

        _mode = NavLayoutInterop.ParseModeFromPrefs(prefs, defaultMode);
        _showBrowser = prefs.ShowBrowser;
        _showNotes = prefs.ShowNotes;
        _showGallery = prefs.ShowGallery;
        _showCalendar = prefs.ShowCalendar;
        _secondaryExpanded = prefs.SecondaryExpanded;
    }

    private async Task PersistAsync(IJSRuntime js, CancellationToken ct)
    {
        var dto = new NavLayoutInterop.NavLayoutPrefsDto
        {
            Mode = NavLayoutInterop.ToStorageMode(_mode),
            ShowBrowser = _showBrowser,
            ShowNotes = _showNotes,
            ShowGallery = _showGallery,
            ShowCalendar = _showCalendar,
            SecondaryExpanded = _secondaryExpanded
        };
        await NavLayoutInterop.SavePrefsAsync(js, dto, ct);
    }

    private static bool Set(ref bool field, bool value)
    {
        if (field == value)
            return false;
        field = value;
        return true;
    }

    private (NavLayoutMode, bool, bool, bool, bool, bool) Snapshot() =>
        (_mode, _showBrowser, _showNotes, _showGallery, _showCalendar, _secondaryExpanded);
}
