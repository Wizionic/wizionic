namespace App.Core.Help;

/// <summary>
/// Disposable local index of help chunks (and optional embeddings).
/// Safe to delete and rebuild; never store this in the chat/notes database.
/// </summary>
public interface IHelpIndex
{
    Task<HelpIndexStatus> GetStatusAsync(CancellationToken ct = default);

    Task RebuildAsync(
        IReadOnlyList<HelpChunk> chunks,
        string catalogHash,
        string? embedModelId,
        int dimensions,
        IReadOnlyList<float[]>? embeddings,
        CancellationToken ct = default);

    Task<IReadOnlyList<HelpSearchHit>> SearchVectorAsync(float[] query, int k, CancellationToken ct = default);

    Task<IReadOnlyList<HelpChunk>> GetChunksAsync(CancellationToken ct = default);
}
