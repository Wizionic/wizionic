using ChatfishApp.Core.Browser;

namespace ChatfishApp.Core.UI;

public interface IBrowserSidePanelState
{
    bool IsOpen { get; set; }
    BrowserSidePanelContent Content { get; set; }
    string? ActiveAppId { get; set; }

    event Action? OnChanged;
    void NotifyChanged();
}