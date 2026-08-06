namespace App.Core.UI;

public interface INotesPanelState
{
    bool IsOpen { get; set; }

    event Action? OnChanged;
    void Toggle();
}