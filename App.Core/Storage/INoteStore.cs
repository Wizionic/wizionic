namespace App.Core.Storage;

public interface INoteStore
{
    Task<List<LocalNote>> LoadIndexAsync(CancellationToken ct = default);
    Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default);

    Task<List<ChatMessage>> LoadNoteAsync(string id, CancellationToken ct = default);
    Task<NoteCommit> CommitAsync(string id, NoteMutation mutation, CancellationToken ct = default);
    Task<NoteSyncPayload?> ReadPayloadAsync(string id, CancellationToken ct = default);
    Task<List<NotebookDraft>> LoadDraftsAsync(string id, CancellationToken ct = default);
    Task WriteDraftAsync(string id, NotebookDraft draft, CancellationToken ct = default);
    Task ClearDraftAsync(string id, string session, CancellationToken ct = default);
    Task<DateTime> DeleteNoteAsync(string id, CancellationToken ct = default);

    Task<string?> GetMetaTitleAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Marks a notebook as password-protected (or clears protection).
    /// Unlocking is done in the UI by verifying the account password; this only stores the flag.
    /// When <paramref name="protectionChangedTicks"/> is null, stamps UtcNow (local user action).
    /// Sync should pass the remote peer's ticks so LWW converges correctly.
    /// </summary>
    Task SetPasswordProtectedAsync(string id, bool isProtected, long? protectionChangedTicks = null, CancellationToken ct = default);

    /// <summary>
    /// Sets sidebar order for notebooks (stable sort; does not change LastUpdated).
    /// </summary>
    Task ReorderNotesAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default);

    Task<bool> TryApplyRemoteDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default);
}
