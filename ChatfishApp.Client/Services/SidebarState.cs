using System;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Simple state service for sidebar collapsed state (used by WasmChat.razor for the toggle button + .page class binding).
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
}
