using App.Core.Storage;

namespace App.Shared.Services;

/// <summary>Thread-safe one-shot handoff of a note into Chat for AI edit.</summary>
public sealed class NotesChatHandoff : INotesChatHandoff
{
    private readonly object _gate = new();
    private NotesChatHandoffPayload? _pending;

    public void SetEditWithAiRequest(NotesChatHandoffPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(payload.NotebookId))
            throw new ArgumentException("Notebook id is required.", nameof(payload));

        lock (_gate)
            _pending = payload;
    }

    public bool TryTakeEditWithAiRequest(out NotesChatHandoffPayload? payload)
    {
        lock (_gate)
        {
            payload = _pending;
            _pending = null;
            return payload != null && !string.IsNullOrWhiteSpace(payload.NotebookId);
        }
    }
}
