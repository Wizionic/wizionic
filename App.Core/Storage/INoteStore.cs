namespace App.Core.Storage;

public interface INoteStore
{
    Task<List<LocalNote>> LoadIndexAsync(CancellationToken ct = default);
    Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default);

    Task<List<ChatMessage>> LoadNoteAsync(string id, CancellationToken ct = default);
    Task SaveNoteAsync(string id, List<ChatMessage> entries, CancellationToken ct = default);
    Task<DateTime> DeleteNoteAsync(string id, CancellationToken ct = default);

    Task<string?> GetMetaTitleAsync(string id, CancellationToken ct = default);
    /// <param name="titleChangedTicks">
    /// When set (local rename or remote LWW apply), persists that clock with the title.
    /// Null keeps the existing rename clock (content saves must pass null).
    /// </param>
    Task UpdateIndexAfterSaveAsync(
        string id,
        string title,
        List<ChatMessage>? entriesForFingerprint = null,
        long? titleChangedTicks = null,
        CancellationToken ct = default);

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

    Task<bool> ShouldAcceptIncomingContentAsync(string id, List<ChatMessage> entries, CancellationToken ct = default);
    Task<bool> TryApplyRemoteDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default);
}