namespace App.Core.Storage;

/// <summary>
/// Cross-page handoff: Gallery → Chat for "Edit with AI".
/// Singleton so the payload survives navigation between /gallery and /chat.
/// </summary>
public interface IGalleryChatHandoff
{
    /// <summary>Queue an image for the next Chat page open (consumed once).</summary>
    void SetEditWithAiRequest(Attachment image);

    /// <summary>Take the pending edit request, if any (clears it).</summary>
    bool TryTakeEditWithAiRequest(out Attachment? image);
}
