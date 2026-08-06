using App.Core.UI;
using Microsoft.JSInterop;

namespace App.Maui.Services;

public class MauiSidebarState : ISidebarState
{
    private bool _isCollapsed;

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed != value)
            {
                _isCollapsed = value;
                OnChanged?.Invoke();
            }
        }
    }

    public event Action? OnChanged;

    public void Toggle() => IsCollapsed = !IsCollapsed;

    public void SetCollapsedSilently(bool collapsed) => _isCollapsed = collapsed;

    public async Task SetCollapsedAsync(bool collapsed, IJSRuntime? js = null, CancellationToken ct = default)
    {
        if (IsCollapsed != collapsed)
            IsCollapsed = collapsed;

        if (js is null)
            return;

        try
        {
            // Prefix is set by ThemeBootstrap / NavLayoutBootstrap for the current auth user.
            await js.InvokeVoidAsync("toggleWasmSidebar", ct, collapsed);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiSidebarState] SetCollapsedAsync failed: {ex.Message}");
        }
    }

    public Task CollapseIfMobileAsync(IJSRuntime? js = null, CancellationToken ct = default) => Task.CompletedTask;
}
