using App.Core.Browser;

namespace App.Core.UI;

public interface IBrowserSidePanelState
{
    bool IsOpen { get; set; }
    BrowserSidePanelContent Content { get; set; }
    string? ActiveAppId { get; set; }

    event Action? OnChanged;
    void NotifyChanged();
}