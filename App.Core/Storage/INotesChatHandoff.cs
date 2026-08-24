namespace App.Core.Storage;

/// <summary>
/// Cross-page handoff: Notes → Chat for "Edit with AI".
/// Singleton so the payload survives navigation between /notes and /chat.
/// </summary>
public interface INotesChatHandoff
{
    void SetEditWithAiRequest(NotesChatHandoffPayload payload);

    bool TryTakeEditWithAiRequest(out NotesChatHandoffPayload? payload);
}

public sealed record NotesChatHandoffPayload(
    string NotebookId,
    string NotebookTitle,
    string? EntryId,
    string Html);
