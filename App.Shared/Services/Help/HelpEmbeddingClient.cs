using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Storage;

namespace App.Shared.Services.Help;

/// <summary>OpenAI-compatible embeddings against local Ollama or Lemonade.</summary>
public sealed class HelpEmbeddingClient
{
    private readonly IKeyStore _keys;
    private readonly HttpClient _http;

    public HelpEmbeddingClient(IKeyStore keys, HttpClient http)
    {
        _keys = keys;
        _http = http;
    }

    public async Task<float[]> EmbedAsync(string modelId, string text, CancellationToken ct = default)
    {
        var list = await EmbedAsync(modelId, new[] { text }, ct);
        return list.Count > 0 ? list[0] : Array.Empty<float>();
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        string modelId,
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        var (baseUrl, apiKey, model) = Resolve(modelId);
        var url = baseUrl.TrimEnd('/') + "/embeddings";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        req.Content = JsonContent.Create(new { model, input = texts.Count == 1 ? texts[0] : (object)texts });

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Embeddings failed ({(int)resp.StatusCode}): {Trim(body)}");

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Embeddings response had no data array.");

        var vectors = new List<float[]>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("embedding", out var emb) || emb.ValueKind != JsonValueKind.Array)
                continue;
            var vec = new float[emb.GetArrayLength()];
            var i = 0;
            foreach (var n in emb.EnumerateArray())
                vec[i++] = n.GetSingle();
            vectors.Add(vec);
        }

        return vectors;
    }

    public bool SupportsDirectChat(string modelId) =>
        modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith("lemonade/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Help-only completion. Do not reuse this for Chat.
    /// Chat must keep thinking/streaming via ChatCompletionService (ME.AI).
    /// This path exists so Ask-in-Help can read Lemonade/Ollama reasoning fields the OpenAI SDK
    /// drops, send max_tokens (not only max_completion_tokens), and turn Qwen thinking off
    /// for short factual answers. Forcing thinking off in Chat hid useful reasoning.
    /// </summary>
    public async Task<string> CompleteAsync(
        string modelId,
        string system,
        string user,
        CancellationToken ct = default)
    {
        var (baseUrl, apiKey, model) = Resolve(modelId);
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        var messages = new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user }
        };
        var fullBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = 4096,
            ["max_completion_tokens"] = 4096,
            ["temperature"] = 0.3,
            ["enable_thinking"] = false,
            ["chat_template_kwargs"] = new Dictionary<string, object?> { ["enable_thinking"] = false }
        };
        var simpleBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = 4096,
            ["temperature"] = 0.3
        };

        var (ok, body) = await PostJsonAsync(url, apiKey, fullBody, ct);
        if (!ok && LooksLikeUnknownField(body))
            (ok, body) = await PostJsonAsync(url, apiKey, simpleBody, ct);
        if (!ok)
            throw new InvalidOperationException($"Help completion failed: {Trim(body)}");

        var text = ParseCompletionText(body);
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        var finish = TryReadFinishReason(body);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(finish)
                ? "The model returned an empty answer."
                : $"The model returned an empty answer (finish_reason={finish}).");
    }

    internal static string ParseCompletionText(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string? content = null;
        string? reasoning = null;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message))
                    ReadMessage(message, ref content, ref reasoning);
                if (choice.TryGetProperty("delta", out var delta))
                    ReadMessage(delta, ref content, ref reasoning);
                if (choice.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    content ??= textEl.GetString();
            }
        }

        var cleaned = StripThink(content);
        if (!string.IsNullOrWhiteSpace(cleaned))
            return cleaned.Trim();

        var fromReason = StripThink(reasoning);
        return string.IsNullOrWhiteSpace(fromReason) ? "" : fromReason.Trim();
    }

    private static void ReadMessage(JsonElement message, ref string? content, ref string? reasoning)
    {
        AppendField(message, "content", ref content);
        AppendField(message, "text", ref content);
        AppendField(message, "reasoning", ref reasoning);
        AppendField(message, "reasoning_content", ref reasoning);
        AppendField(message, "reasoning_text", ref reasoning);
    }

    private static void AppendField(JsonElement obj, string name, ref string? dest)
    {
        if (!obj.TryGetProperty(name, out var el))
            return;

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                dest = string.IsNullOrEmpty(dest) ? s : dest + "\n" + s;
            return;
        }

        if (el.ValueKind != JsonValueKind.Array)
            return;

        foreach (var part in el.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                var s = part.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    dest = string.IsNullOrEmpty(dest) ? s : dest + "\n" + s;
            }
            else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    dest = string.IsNullOrEmpty(dest) ? s : dest + "\n" + s;
            }
        }
    }

    private static string? StripThink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        var s = text.Trim();
        const string open = "<think>";
        const string close = "</think>";
        var start = s.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        var end = s.IndexOf(close, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            var after = s[(end + close.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(after))
                return after;
            var inner = s[(start + open.Length)..end].Trim();
            return string.IsNullOrWhiteSpace(inner) ? "" : inner;
        }

        return s;
    }

    private static string? TryReadFinishReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("finish_reason", out var fr)
                && fr.ValueKind == JsonValueKind.String)
                return fr.GetString();
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private async Task<(bool Ok, string Body)> PostJsonAsync(
        string url,
        string? apiKey,
        object payload,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        req.Content = JsonContent.Create(payload);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return (resp.IsSuccessStatusCode, body);
    }

    private static bool LooksLikeUnknownField(string body) =>
        body.Contains("unknown", StringComparison.OrdinalIgnoreCase)
        || body.Contains("unexpected", StringComparison.OrdinalIgnoreCase)
        || body.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
        || body.Contains("extra fields", StringComparison.OrdinalIgnoreCase);

    private (string BaseUrl, string? ApiKey, string Model) Resolve(string modelId)
    {
        if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            return (_keys.OllamaBaseUrl.TrimEnd('/') + "/v1/", "ollama", modelId.Split('/', 2)[1]);
        if (modelId.StartsWith("lemonade/", StringComparison.OrdinalIgnoreCase))
        {
            var key = string.IsNullOrWhiteSpace(_keys.LemonadeApiKey) ? "lemonade" : _keys.LemonadeApiKey;
            return (_keys.LemonadeBaseUrl.TrimEnd('/') + "/v1/", key, modelId.Split('/', 2)[1]);
        }

        throw new InvalidOperationException(
            "Help embeddings only talk to Ollama or Lemonade. Pick a local embeddings model.");
    }

    private static string Trim(string s) =>
        s.Length <= 240 ? s : s[..240] + "…";
}
