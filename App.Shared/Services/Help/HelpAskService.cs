using System.Text;
using System.Text.Json;
using App.Core.Help;
using App.Core.Storage;
using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace App.Shared.Services.Help;

public sealed class HelpAskService : IHelpAskService
{
    private const int TopK = 6;
    private const string SystemPrompt =
        "You answer questions about the Wizionic app using only the help excerpts below. " +
        "Cite the excerpt titles in plain language. If the excerpts are not enough, say so " +
        "and point at the closest article. Do not invent settings or features.";

    private readonly IHelpCatalog _catalog;
    private readonly IHelpIndex _index;
    private readonly IKeyStore _keys;
    private readonly ChatModelCatalogService _models;
    private readonly HelpEmbeddingClient _embed;
    private readonly object _rebuildGate = new();
    private Task? _rebuildTask;

    public HelpAskService(
        IHelpCatalog catalog,
        IHelpIndex index,
        IKeyStore keys,
        ChatModelCatalogService models,
        HelpEmbeddingClient embed)
    {
        _catalog = catalog;
        _index = index;
        _keys = keys;
        _models = models;
        _embed = embed;
    }

    public Task<HelpIndexStatus> GetIndexStatusAsync(CancellationToken ct = default) =>
        _index.GetStatusAsync(ct);

    public async Task RebuildIndexAsync(CancellationToken ct = default)
    {
        await RebuildCoreAsync(force: true, ct);
    }

    public async Task<HelpAskResult> AskAsync(string question, CancellationToken ct = default)
    {
        var q = (question ?? "").Trim();
        if (q.Length == 0)
            return new HelpAskResult { Error = "Type a question first." };

        var answerModel = _keys.HelpAnswerModelId;
        if (string.IsNullOrWhiteSpace(answerModel))
            return new HelpAskResult { Error = "Pick a help model first (or search the topics on the left)." };

        _ = EnsureIndexAsync(ct);

        var (_, chunks) = await HelpChunker.BuildAsync(_catalog, ct);
        var keywordHits = RankKeyword(q, chunks, TopK);
        var retrieval = "keyword";
        var merged = keywordHits.ToList();

        var status = await _index.GetStatusAsync(ct);
        var embedModel = _keys.HelpEmbedModelId;
        if (status.HasVectors && !string.IsNullOrWhiteSpace(embedModel))
        {
            try
            {
                var qv = await _embed.EmbedAsync(embedModel, q, ct);
                var vectorHits = await _index.SearchVectorAsync(qv, TopK, ct);
                merged = MergeHits(keywordHits, vectorHits, TopK);
                retrieval = "keyword+vector";
            }
            catch
            {
                // Keep keyword hits.
            }
        }

        if (merged.Count == 0)
        {
            return new HelpAskResult
            {
                AnswerMarkdown = "I could not find a matching help article. Try a shorter phrase, or open **Getting started** from the list.",
                Retrieval = retrieval
            };
        }

        try
        {
            var context = string.Join("\n\n---\n\n", merged.Select((h, i) =>
                $"[{i + 1}] {h.Chunk.Title}\n{h.Chunk.Text}"));
            var system = SystemPrompt + "\n\nExcerpts:\n" + context;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));

            string text;
            if (_embed.SupportsDirectChat(answerModel))
            {
                text = await _embed.CompleteAsync(answerModel, system, q, timeout.Token);
            }
            else
            {
                var client = _models.GetChatClientForModel(answerModel);
                var history = new List<AiChatMessage>
                {
                    new(ChatRole.System, system),
                    new(ChatRole.User, q)
                };
                var response = await client.GetResponseAsync(
                    history,
                    new ChatOptions { MaxOutputTokens = 4096 },
                    timeout.Token);
                text = ExtractAnswer(response);
            }

            if (string.IsNullOrWhiteSpace(text))
                text = "The model returned an empty answer. Open the cited article on the left.";

            return new HelpAskResult
            {
                AnswerMarkdown = text,
                Citations = merged,
                Retrieval = retrieval
            };
        }
        catch (Exception ex)
        {
            return new HelpAskResult
            {
                AnswerMarkdown = "",
                Citations = merged,
                Retrieval = retrieval,
                Error = ex.Message
            };
        }
    }

    private Task EnsureIndexAsync(CancellationToken ct)
    {
        lock (_rebuildGate)
        {
            if (_rebuildTask is { IsCompleted: false })
                return _rebuildTask;
            _rebuildTask = RebuildCoreAsync(force: false, ct);
            return _rebuildTask;
        }
    }

    private async Task RebuildCoreAsync(bool force, CancellationToken ct)
    {
        try
        {
        var (hash, chunks) = await HelpChunker.BuildAsync(_catalog, ct);
        var embedModel = _keys.HelpEmbedModelId;
        var status = await _index.GetStatusAsync(ct);
        var current = !force
            && status.Ready
            && string.Equals(status.CatalogHash, hash, StringComparison.Ordinal)
            && string.Equals(status.EmbedModelId ?? "", embedModel ?? "", StringComparison.Ordinal);

        if (current)
            return;

        IReadOnlyList<float[]>? vectors = null;
        var dim = 0;
        if (!string.IsNullOrWhiteSpace(embedModel))
        {
            var texts = chunks.Select(c => c.Text).ToList();
            vectors = await _embed.EmbedAsync(embedModel, texts, ct);
            if (vectors.Count > 0)
                dim = vectors[0].Length;
        }

        await _index.RebuildAsync(chunks, hash, embedModel, dim, vectors, ct);
        }
        catch
        {
            if (force)
                throw;
        }
    }

    private static List<HelpSearchHit> RankKeyword(string question, List<HelpChunk> chunks, int k)
    {
        var tokens = question.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Distinct()
            .ToArray();
        if (tokens.Length == 0)
            return chunks.Take(k).Select(c => new HelpSearchHit { Chunk = c, Score = 0 }).ToList();

        return chunks
            .Select(c =>
            {
                var hay = (c.Title + " " + c.Text).ToLowerInvariant();
                var score = tokens.Count(t => hay.Contains(t));
                return new HelpSearchHit { Chunk = c, Score = score };
            })
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .Take(k)
            .ToList();
    }

    private static List<HelpSearchHit> MergeHits(
        IReadOnlyList<HelpSearchHit> keyword,
        IReadOnlyList<HelpSearchHit> vector,
        int k)
    {
        var map = new Dictionary<int, HelpSearchHit>();
        foreach (var h in keyword)
            map[h.Chunk.Id] = h;
        foreach (var h in vector)
        {
            if (map.TryGetValue(h.Chunk.Id, out var existing))
                map[h.Chunk.Id] = new HelpSearchHit { Chunk = h.Chunk, Score = existing.Score + h.Score };
            else
                map[h.Chunk.Id] = h;
        }

        return map.Values.OrderByDescending(h => h.Score).Take(k).ToList();
    }

    /// <summary>
    /// Qwen / Lemonade thinking models often leave <see cref="ChatResponse.Text"/> empty
    /// and put the only useful string in reasoning fields. Never treat metadata
    /// (created timestamps, ids) as the answer.
    /// </summary>
    private static string ExtractAnswer(ChatResponse response)
    {
        if (IsUsableAnswer(response.Text))
            return response.Text!.Trim();

        if (response.Messages != null)
        {
            foreach (var msg in response.Messages.Where(m => m.Role == ChatRole.Assistant).Reverse())
            {
                var content = string.Join("\n",
                    msg.Contents.OfType<TextContent>().Select(t => t.Text).Where(IsUsableAnswer));
                if (IsUsableAnswer(content))
                    return content.Trim();

                if (msg.AdditionalProperties != null)
                {
                    foreach (var key in ContentKeys.Concat(ReasoningKeys))
                    {
                        if (msg.AdditionalProperties.TryGetValue(key, out var val) && val != null)
                        {
                            var s = val as string ?? val.ToString();
                            if (IsUsableAnswer(s))
                                return s!.Trim();
                        }
                    }
                }

                var fromMsg = CollectKnownFields(msg.RawRepresentation);
                if (IsUsableAnswer(fromMsg))
                    return fromMsg!.Trim();
            }
        }

        var fromRaw = CollectKnownFields(response.RawRepresentation);
        return IsUsableAnswer(fromRaw) ? fromRaw!.Trim() : "";
    }

    private static readonly string[] ContentKeys = { "content", "output_text", "output" };
    private static readonly string[] ReasoningKeys = { "reasoning", "reasoning_content", "reasoning_text" };

    private static string? CollectKnownFields(object? raw)
    {
        if (raw is null)
            return null;
        try
        {
            JsonElement el;
            if (raw is JsonElement existing)
                el = existing;
            else
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(raw));
                el = doc.RootElement.Clone();
            }

            string? content = null;
            string? reasoning = null;
            WalkKnown(el, 0, ref content, ref reasoning);
            if (IsUsableAnswer(content))
                return content;
            if (IsUsableAnswer(reasoning))
                return reasoning;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void WalkKnown(JsonElement el, int depth, ref string? content, ref string? reasoning)
    {
        if (depth > 10)
            return;

        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString();
                    if (!IsUsableAnswer(s))
                        continue;
                    if (ContentKeys.Contains(prop.Name, StringComparer.OrdinalIgnoreCase) && content == null)
                        content = s;
                    else if (ReasoningKeys.Contains(prop.Name, StringComparer.OrdinalIgnoreCase) && reasoning == null)
                        reasoning = s;
                }
                else
                {
                    WalkKnown(prop.Value, depth + 1, ref content, ref reasoning);
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                WalkKnown(item, depth + 1, ref content, ref reasoning);
        }
    }

    private static bool IsUsableAnswer(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        var t = s.Trim();
        if (t.Length < 8)
            return false;
        if (!t.Any(char.IsLetter))
            return false;
        // Lemonade / OpenAI "created" metadata often serializes as an ISO timestamp.
        if (t.Length <= 40
            && t.Contains('T', StringComparison.Ordinal)
            && DateTimeOffset.TryParse(t, out _))
            return false;
        return true;
    }
}
