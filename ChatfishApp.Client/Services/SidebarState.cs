using System;
using Microsoft.JSInterop;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Simple state service for sidebar collapsed state (used by Chat.razor for the toggle button + .page class binding).
/// </summary>
public class SidebarState
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

    /// <summary>
    /// On narrow viewports the sidebar overlays content; collapse it after the user picks
    /// a conversation/note or taps the header plus button so they return to full-width content.
    /// </summary>
    public async Task CollapseIfMobileAsync(IJSRuntime js)
    {
        try
        {
            var isMobile = await js.InvokeAsync<bool>("eval", "window.isMobileViewport()");
            if (!isMobile || IsCollapsed)
                return;

            IsCollapsed = true;
            await js.InvokeVoidAsync("toggleWasmSidebar", true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SidebarState] CollapseIfMobile failed: {ex.Message}");
        }
    }
}
