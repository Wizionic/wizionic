using ChatfishApp.Core.Browser;
using ChatfishApp.Core.UI;

namespace ChatfishApp.Maui.Services;

public sealed class MauiBrowserSidePanelState : IBrowserSidePanelState
{
    public bool IsOpen { get; set; }
    public BrowserSidePanelContent Content { get; set; } = BrowserSidePanelContent.None;
    public string? ActiveAppId { get; set; }

    public event Action? OnChanged;

    public void NotifyChanged() => OnChanged?.Invoke();
}