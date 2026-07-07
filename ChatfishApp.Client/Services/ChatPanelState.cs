using ChatfishApp.Core.UI;

namespace ChatfishApp.Client.Services;

public sealed class ChatPanelState : IChatPanelState
{
    private bool _isOpen = true;

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

    public event Action? OnChanged;

    public void Toggle() => IsOpen = !IsOpen;
}