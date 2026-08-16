using App.Core.Help;

namespace App.Shared.Services.Help;

/// <summary>In-process help index for WASM and as a desktop fallback when sqlite-vec is unavailable.</summary>
public sealed class MemoryHelpIndex : IHelpIndex
{
    private readonly object _gate = new();
    private List<HelpChunk> _chunks = new();
    private List<float[]> _vectors = new();
    private HelpIndexStatus _status = new();

    public Task<HelpIndexStatus> GetStatusAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_status);
    }

    public Task RebuildAsync(
        IReadOnlyList<HelpChunk> chunks,
        string catalogHash,
        string? embedModelId,
        int dimensions,
        IReadOnlyList<float[]>? embeddings,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            _chunks = chunks.ToList();
            _vectors = embeddings != null && embeddings.Count == chunks.Count
                ? embeddings.Select(v => v.ToArray()).ToList()
                : new List<float[]>();
            _status = new HelpIndexStatus
            {
                Ready = _chunks.Count > 0,
                ChunkCount = _chunks.Count,
                HasVectors = _vectors.Count == _chunks.Count && _chunks.Count > 0,
                CatalogHash = catalogHash,
                EmbedModelId = embedModelId,
                Dimensions = dimensions,
                BuiltAtUtc = DateTimeOffset.UtcNow
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HelpSearchHit>> SearchVectorAsync(float[] query, int k, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_vectors.Count == 0 || _vectors.Count != _chunks.Count || query.Length == 0)
                return Task.FromResult<IReadOnlyList<HelpSearchHit>>(Array.Empty<HelpSearchHit>());

            var hits = new List<HelpSearchHit>(_chunks.Count);
            for (var i = 0; i < _chunks.Count; i++)
            {
                if (_vectors[i].Length != query.Length)
                    continue;
                hits.Add(new HelpSearchHit { Chunk = _chunks[i], Score = Cosine(query, _vectors[i]) });
            }

            return Task.FromResult<IReadOnlyList<HelpSearchHit>>(
                hits.OrderByDescending(h => h.Score).Take(Math.Max(1, k)).ToList());
        }
    }

    public Task<IReadOnlyList<HelpChunk>> GetChunksAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<HelpChunk>>(_chunks.ToList());
    }

    public static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na <= 0 || nb <= 0)
            return 0;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
