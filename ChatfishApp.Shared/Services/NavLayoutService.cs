using ChatfishApp.Core.UI;
using Microsoft.JSInterop;

namespace ChatfishApp.Shared.Services;

public sealed class NavLayoutService : INavLayoutState
{
    private NavLayoutMode _mode = NavLayoutMode.Top;
    private bool _initialized;

    public event Action? OnChanged;

    public NavLayoutMode Mode => _mode;

    public async Task InitializeAsync(IJSRuntime js, CancellationToken ct = default)
    {
        if (_initialized)
        {
            await NavLayoutInterop.ApplyAsync(js, _mode, ct);
            return;
        }

        var saved = await NavLayoutInterop.GetSavedModeAsync(js, ct);
        _mode = NavLayoutInterop.ParseMode(saved);
        _initialized = true;
        await NavLayoutInterop.ApplyAsync(js, _mode, ct);
        OnChanged?.Invoke();
    }

    public async Task SetModeAsync(NavLayoutMode mode, IJSRuntime js, CancellationToken ct = default)
    {
        if (_mode == mode)
            return;

        _mode = mode;
        await NavLayoutInterop.SaveModeAsync(js, _mode, ct);
        await NavLayoutInterop.ApplyAsync(js, _mode, ct);
        OnChanged?.Invoke();
    }

    public async Task SyncFromStorageAndApplyAsync(IJSRuntime js, CancellationToken ct = default)
    {
        var saved = await NavLayoutInterop.GetSavedModeAsync(js, ct);
        var normalized = NavLayoutInterop.ParseMode(saved);
        var changed = _mode != normalized;
        _mode = normalized;
        _initialized = true;
        await NavLayoutInterop.ApplyAsync(js, _mode, ct);
        if (changed)
            OnChanged?.Invoke();
    }
}