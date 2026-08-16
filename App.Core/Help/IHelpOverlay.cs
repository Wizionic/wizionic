namespace App.Core.Help;

/// <summary>
/// In-app help slide-over. Opened by ? icons without leaving the current page.
/// </summary>
public interface IHelpOverlay
{
    bool IsOpen { get; }
    string? TopicId { get; }
    event Action? Changed;
    void Open(string? topicId = null);
    void Close();
}
