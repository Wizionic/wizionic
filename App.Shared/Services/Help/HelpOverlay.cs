using App.Core.Help;

namespace App.Shared.Services.Help;

public sealed class HelpOverlay : IHelpOverlay
{
    public bool IsOpen { get; private set; }
    public string? TopicId { get; private set; }

    public event Action? Changed;

    public void Open(string? topicId = null)
    {
        IsOpen = true;
        if (!string.IsNullOrWhiteSpace(topicId))
            TopicId = topicId.Trim();
        Changed?.Invoke();
    }

    public void Close()
    {
        IsOpen = false;
        Changed?.Invoke();
    }
}
