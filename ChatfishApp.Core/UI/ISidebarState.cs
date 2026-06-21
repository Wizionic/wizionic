using Microsoft.JSInterop;

namespace ChatfishApp.Core.UI;

public interface ISidebarState
{
    bool IsCollapsed { get; set; }
    event Action? OnChanged;
    void Toggle();
    Task CollapseIfMobileAsync(IJSRuntime? js = null, CancellationToken ct = default);
}