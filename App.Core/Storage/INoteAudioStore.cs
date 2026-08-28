namespace App.Core.Storage;

/// <summary>
/// Encrypted lecture/dictation audio blobs, separate from note JSON.
/// MAUI implements this; WASM is a no-op (dictation text only).
/// Audio bytes are device-local and are not synced in v1.
/// </summary>
public interface INoteAudioStore
{
    bool IsSupported { get; }

    Task<NoteAudioRef> SaveAsync(
        string notebookId,
        string entryId,
        string clipId,
        string contentType,
        byte[] bytes,
        long durationMs,
        IReadOnlyList<NoteAudioCue>? cues = null,
        CancellationToken ct = default);

    Task<NoteAudioRef?> GetMetaAsync(string clipId, CancellationToken ct = default);
    Task<bool> ExistsAsync(string clipId, CancellationToken ct = default);
    Task<byte[]?> LoadBytesAsync(string clipId, CancellationToken ct = default);
    Task<string?> CreateDisplayUrlAsync(string clipId, CancellationToken ct = default);
    Task RevokeDisplayUrlAsync(string url, CancellationToken ct = default);
    Task DeleteAsync(string clipId, CancellationToken ct = default);
    /// <summary>Deletes every clip stored for a notebook. Returns how many clips were removed.</summary>
    Task<int> DeleteByNotebookAsync(string notebookId, CancellationToken ct = default);
    Task<long> SumStoredBytesAsync(CancellationToken ct = default);
}

public sealed record NoteAudioRef(
    string Id,
    long DurationMs,
    string ContentType,
    long Size,
    DateTime CreatedAt,
    List<NoteAudioCue>? Cues = null);

public sealed record NoteAudioCue(double StartSeconds, string Text);
