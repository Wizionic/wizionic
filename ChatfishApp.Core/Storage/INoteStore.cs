namespace ChatfishApp.Core.Storage;

public interface INoteStore
{
    Task<List<LocalNote>> LoadIndexAsync(CancellationToken ct = default);
    Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default);

    Task<List<ChatMessage>> LoadNoteAsync(string id, CancellationToken ct = default);
    Task SaveNoteAsync(string id, List<ChatMessage> entries, CancellationToken ct = default);
    Task<DateTime> DeleteNoteAsync(string id, CancellationToken ct = default);

    Task<string?> GetMetaTitleAsync(string id, CancellationToken ct = default);
    Task UpdateIndexAfterSaveAsync(string id, string title, List<ChatMessage>? entriesForFingerprint = null, CancellationToken ct = default);

    Task<bool> ShouldAcceptIncomingContentAsync(string id, List<ChatMessage> entries, CancellationToken ct = default);
    Task<bool> TryApplyRemoteDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default);
}