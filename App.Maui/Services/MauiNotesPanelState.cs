using App.Core.UI;

namespace App.Maui.Services;

public sealed class MauiNotesPanelState : INotesPanelState
{
    private bool _isOpen;

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