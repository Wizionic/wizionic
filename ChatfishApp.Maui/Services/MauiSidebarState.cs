using ChatfishApp.Core.UI;
using Microsoft.JSInterop;

namespace ChatfishApp.Maui.Services;

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

    public Task CollapseIfMobileAsync(IJSRuntime? js = null, CancellationToken ct = default) => Task.CompletedTask;
}