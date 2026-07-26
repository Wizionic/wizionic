using ChatfishApp.Core.UI;
using Microsoft.JSInterop;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Sidebar collapsed state for WASM chat/notes pages.
/// Persistence key prefix is applied by <c>WasmSidebarSync</c> / <c>UiStorageNamespace</c>
/// before read/write so collapse state stays per-user.
/// </summary>
public class SidebarState : ISidebarState
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
            // Prefix is set by WasmSidebarSync / ThemeBootstrap for the current auth user.
            await js.InvokeVoidAsync("chatfishSidebar.setCollapsed", ct, collapsed, new { source = "SidebarState.SetCollapsedAsync", skipNotify = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SidebarState] SetCollapsedAsync failed: {ex.Message}");
        }
    }

    public async Task CollapseIfMobileAsync(IJSRuntime? js = null, CancellationToken ct = default)
    {
        if (js is null)
            return;

        try
        {
            var isMobile = await js.InvokeAsync<bool>("eval", ct, "window.isMobileViewport()");
            if (!isMobile || IsCollapsed)
                return;

            await SetCollapsedAsync(true, js, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SidebarState] CollapseIfMobile failed: {ex.Message}");
        }
    }
}
