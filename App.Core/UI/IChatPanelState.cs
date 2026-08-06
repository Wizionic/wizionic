namespace App.Core.UI;

public interface IChatPanelState
{
    bool IsOpen { get; set; }

    event Action? OnChanged;
    void Toggle();
}