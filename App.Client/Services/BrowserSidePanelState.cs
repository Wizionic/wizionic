using App.Core.Browser;
using App.Core.UI;

namespace App.Client.Services;

public sealed class BrowserSidePanelState : IBrowserSidePanelState
{
    public bool IsOpen { get; set; }
    public BrowserSidePanelContent Content { get; set; } = BrowserSidePanelContent.None;
    public string? ActiveAppId { get; set; }

    public event Action? OnChanged;

    public void NotifyChanged() => OnChanged?.Invoke();
}