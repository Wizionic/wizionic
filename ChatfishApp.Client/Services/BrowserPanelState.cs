using ChatfishApp.Core.UI;

namespace ChatfishApp.Client.Services;

public sealed class BrowserPanelState : IBrowserPanelState
{
    private bool _isOpen;
    private double _chatPaneWidthPx;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen != value)
            {
                _isOpen = value;
                OnChanged?.Invoke();
            }
        }
    }

    public double ChatPaneWidthPx
    {
        get => _chatPaneWidthPx;
        set
        {
            if (Math.Abs(_chatPaneWidthPx - value) > 0.5)
            {
                _chatPaneWidthPx = value;
                OnChanged?.Invoke();
            }
        }
    }

    public event Action? OnChanged;

    public void Toggle() => IsOpen = !IsOpen;
}