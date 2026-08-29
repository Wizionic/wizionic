using App.Core.Storage;

namespace App.Shared.Services;

public sealed class NullNoteAudioStore : INoteAudioStore
{
    public static readonly NullNoteAudioStore Instance = new();

    private NullNoteAudioStore() { }

    public bool IsSupported => false;

    public Task<NoteAudioRef> SaveAsync(
        string notebookId,
        string entryId,
        string clipId,
        string contentType,
        byte[] bytes,
        long durationMs,
        IReadOnlyList<NoteAudioCue>? cues = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Lecture audio is available in the desktop app only.");

    public Task<NoteAudioRef?> GetMetaAsync(string clipId, CancellationToken ct = default) =>
        Task.FromResult<NoteAudioRef?>(null);

    public Task<bool> ExistsAsync(string clipId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<byte[]?> LoadBytesAsync(string clipId, CancellationToken ct = default) =>
        Task.FromResult<byte[]?>(null);

    public Task<string?> CreateDisplayUrlAsync(string clipId, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task RevokeDisplayUrlAsync(string url, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteAsync(string clipId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<int> DeleteByNotebookAsync(string notebookId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<long> SumStoredBytesAsync(CancellationToken ct = default) =>
        Task.FromResult(0L);
}
