namespace App.Core.UI;

public interface IBrowserPanelState
{
    bool IsOpen { get; set; }

    /// <summary>Chat column width in pixels when the browser split is open. 0 = use default on next open.</summary>
    double ChatPaneWidthPx { get; set; }

    event Action? OnChanged;
    void Toggle();
}