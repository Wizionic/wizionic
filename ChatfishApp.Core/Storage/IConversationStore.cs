namespace ChatfishApp.Core.Storage;

public interface IConversationStore
{
    Task<List<LocalConvo>> LoadIndexAsync(CancellationToken ct = default);
    Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default);

    Task<List<ChatMessage>> LoadConversationAsync(string id, CancellationToken ct = default);
    Task SaveConversationAsync(string id, List<ChatMessage> messages, CancellationToken ct = default);
    Task<DateTime> DeleteConversationAsync(string id, CancellationToken ct = default);

    Task<string?> GetMetaTitleAsync(string id, CancellationToken ct = default);
    Task<(string Title, bool TitleIsCustom)> GetMetaTitleInfoAsync(string id, CancellationToken ct = default);
    Task SetConversationTitleAsync(string id, string title, CancellationToken ct = default);
    Task UpdateIndexAfterSaveAsync(string id, List<ChatMessage> messages, List<LocalConvo> currentIndex, CancellationToken ct = default);

    Task<string?> GetLastConvoIdAsync(CancellationToken ct = default);
    Task SetLastConvoIdAsync(string id, CancellationToken ct = default);

    Task<bool> ShouldAcceptIncomingContentAsync(string id, List<ChatMessage> messages, CancellationToken ct = default);
    Task<bool> TryApplyRemoteDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default);

    Task SetConversationSyncEnabledAsync(string id, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Marks a chat as password-protected (or clears protection).
    /// Unlocking is done in the UI by verifying the account password; this only stores the flag.
    /// </summary>
    Task SetPasswordProtectedAsync(string id, bool isProtected, CancellationToken ct = default);

    /// <summary>
    /// Sets sidebar order for chats (stable sort; does not change LastUpdated).
    /// </summary>
    Task ReorderConversationsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default);
}