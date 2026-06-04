using System;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Simple state service to share sidebar collapsed state between WasmTopBar (in layout) and WasmChat page.
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
