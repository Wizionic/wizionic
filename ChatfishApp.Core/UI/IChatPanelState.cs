namespace ChatfishApp.Core.UI;

public interface IChatPanelState
{
    bool IsOpen { get; set; }

    event Action? OnChanged;
    void Toggle();
}