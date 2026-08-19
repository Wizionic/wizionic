using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Storage;

namespace App.Core.Cloud;

/// <summary>
/// Discovers models on an OpenAI-compatible cloud endpoint.
/// Always calls <c>GET /models</c>; optional xAI lists refine modalities.
/// </summary>
public static class CloudModelCatalogResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Normalize a user-entered base URL to the OpenAI-compatible root
    /// (e.g. <c>https://api.x.ai/v1</c>). Host-only URLs get <c>/v1</c> appended.
    /// </summary>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var url = (baseUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            return "";

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url.TrimStart('/');
        }

        url = url.TrimEnd('/');

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return url;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
            return url + "/v1";

        return url;
    }

    public static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? apiKey)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
        return req;
    }

    public static string MakeProviderId(string displayName, IEnumerable<string> existingIds)
    {
        var slug = Regex.Replace((displayName ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
        if (string.IsNullOrEmpty(slug))
            slug = "provider";

        var taken = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        var id = slug;
        var n = 2;
        while (taken.Contains(id))
            id = slug + "-" + n++;
        return id;
    }

    public static async Task<CloudDiscoveryResult> DiscoverAsync(
        HttpClient http,
        string baseUrl,
        string? apiKey,
        IReadOnlyList<CloudModelSettings>? existing,
        CancellationToken ct = default)
    {
        var origin = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(origin))
            throw new InvalidOperationException("Base URL is required.");

        var existingMap = (existing ?? Array.Empty<CloudModelSettings>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);

        var byName = new Dictionary<string, CloudModelSettings>(StringComparer.OrdinalIgnoreCase);

        await FetchOpenAiModelsAsync(http, origin, apiKey, byName, ct);

        var languageOk = await TryFetchLanguageModelsAsync(http, origin, apiKey, byName, ct);
        var imageOk = await TryFetchImageGenerationModelsAsync(http, origin, apiKey, byName, ct);
        var (voices, ttsVoicesOk) = await TryFetchTtsVoicesAsync(http, origin, apiKey, ct);

        var merged = new List<CloudModelSettings>();
        foreach (var discovered in byName.Values)
        {
            existingMap.TryGetValue(discovered.Name, out var prev);
            merged.Add(ResolveSettings(discovered, prev));
        }

        var hasTtsModel = merged.Any(m => m.IsTts);
        var hasSttModel = merged.Any(m => m.IsTranscription);

        return new CloudDiscoveryResult(
            Models: merged,
            Voices: voices,
            HasOpenAiAudio: hasTtsModel || hasSttModel,
            HasXaiTts: ttsVoicesOk,
            HasXaiStt: languageOk || ttsVoicesOk,
            HasXaiImageApi: imageOk);
    }

    public static CloudModelSettings ResolveSettings(CloudModelSettings discovered, CloudModelSettings? existing)
    {
        if (existing == null)
            return discovered.Clone();

        return new CloudModelSettings
        {
            Name = discovered.Name,
            Label = string.IsNullOrWhiteSpace(existing.Label) || existing.Label == existing.Name
                ? discovered.Label
                : existing.Label,
            SupportsTools = existing.UserOverrideTools ? existing.SupportsTools : discovered.SupportsTools,
            SupportsVision = existing.UserOverrideVision ? existing.SupportsVision : discovered.SupportsVision,
            ContextSize = existing.UserOverrideContext && existing.ContextSize > 0
                ? existing.ContextSize
                : discovered.ContextSize,
            IsImage = existing.UserOverrideImage ? existing.IsImage : discovered.IsImage,
            IsEdit = existing.UserOverrideEdit ? existing.IsEdit : discovered.IsEdit,
            IsTts = discovered.IsTts,
            IsTranscription = discovered.IsTranscription,
            IsEmbeddings = discovered.IsEmbeddings,
            IsReranking = discovered.IsReranking,
            UserOverrideTools = existing.UserOverrideTools,
            UserOverrideVision = existing.UserOverrideVision,
            UserOverrideContext = existing.UserOverrideContext,
            UserOverrideImage = existing.UserOverrideImage,
            UserOverrideEdit = existing.UserOverrideEdit
        };
    }

    public static string? PickDefault(string? current, IReadOnlyList<CloudModelSettings> models, Func<CloudModelSettings, bool> pred)
    {
        if (!string.IsNullOrWhiteSpace(current) &&
            models.Any(m => pred(m) && m.Name.Equals(current, StringComparison.OrdinalIgnoreCase)))
            return current;
        return models.FirstOrDefault(pred)?.Name;
    }

    private static async Task FetchOpenAiModelsAsync(
        HttpClient http,
        string origin,
        string? apiKey,
        Dictionary<string, CloudModelSettings> byName,
        CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, origin + "/models", apiKey);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(FormatHttpError(resp.StatusCode, body));

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var settings = ClassifyFromOpenAiModel(id, item);
            if (settings == null)
                continue;

            byName[settings.Name] = settings;
        }
    }

    private static async Task<bool> TryFetchLanguageModelsAsync(
        HttpClient http,
        string origin,
        string? apiKey,
        Dictionary<string, CloudModelSettings> byName,
        CancellationToken ct)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, origin + "/language-models", apiKey);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            var payload = await resp.Content.ReadFromJsonAsync<XaiLanguageModelsResponse>(JsonOptions, ct);
            if (payload?.Models == null)
                return resp.IsSuccessStatusCode;

            foreach (var m in payload.Models)
            {
                if (string.IsNullOrWhiteSpace(m.Id))
                    continue;

                byName.TryGetValue(m.Id, out var existing);
                var next = existing?.Clone() ?? new CloudModelSettings { Name = m.Id, Label = m.Id };

                var inputs = m.InputModalities ?? new List<string>();
                var outputs = m.OutputModalities ?? new List<string>();
                next.SupportsVision = inputs.Any(x => x.Equals("image", StringComparison.OrdinalIgnoreCase));
                if (outputs.Any(x => x.Equals("image", StringComparison.OrdinalIgnoreCase)))
                    next.IsImage = true;
                if (m.Aliases is { Count: > 0 } && string.Equals(next.Label, next.Name, StringComparison.Ordinal))
                    next.Label = m.Aliases[0];

                byName[next.Name] = next;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryFetchImageGenerationModelsAsync(
        HttpClient http,
        string origin,
        string? apiKey,
        Dictionary<string, CloudModelSettings> byName,
        CancellationToken ct)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, origin + "/image-generation-models", apiKey);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            var payload = await resp.Content.ReadFromJsonAsync<XaiImageModelsResponse>(JsonOptions, ct);
            if (payload?.Models == null)
                return true;

            foreach (var m in payload.Models)
            {
                if (string.IsNullOrWhiteSpace(m.Id))
                    continue;

                byName.TryGetValue(m.Id, out var existing);
                var next = existing?.Clone() ?? new CloudModelSettings { Name = m.Id, Label = m.Id };
                next.IsImage = true;
                next.SupportsTools = false;
                var inputs = m.InputModalities ?? new List<string>();
                next.IsEdit = inputs.Any(x => x.Equals("image", StringComparison.OrdinalIgnoreCase));
                byName[next.Name] = next;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(List<CloudTtsVoice> Voices, bool Ok)> TryFetchTtsVoicesAsync(
        HttpClient http,
        string origin,
        string? apiKey,
        CancellationToken ct)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, origin + "/tts/voices", apiKey);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return (new List<CloudTtsVoice>(), false);

            var payload = await resp.Content.ReadFromJsonAsync<XaiVoicesResponse>(JsonOptions, ct);
            var list = payload?.Voices?
                .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
                .Select(v => new CloudTtsVoice
                {
                    VoiceId = v.VoiceId!,
                    Name = string.IsNullOrWhiteSpace(v.Name) ? v.VoiceId! : v.Name!
                })
                .ToList() ?? new List<CloudTtsVoice>();
            return (list, true);
        }
        catch
        {
            return (new List<CloudTtsVoice>(), false);
        }
    }

    private static CloudModelSettings? ClassifyFromOpenAiModel(string id, JsonElement item)
    {
        var lower = id.ToLowerInvariant();

        if (LooksLikeVoiceSession(lower) || ContainsAny(lower, "video"))
            return null;

        var context = 0;
        if (item.TryGetProperty("context_length", out var ctx) && ctx.TryGetInt32(out var ctxVal))
            context = ctxVal;

        var hasImagePrice = item.TryGetProperty("image_price", out var ip) &&
                            ip.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var promptImagePrice = item.TryGetProperty("prompt_image_token_price", out var pip) &&
                               pip.ValueKind == JsonValueKind.Number &&
                               pip.TryGetInt64(out var pipVal) &&
                               pipVal > 0;

        var isEmbeddings = ContainsAny(lower, "embed", "embedding");
        var isRerank = ContainsAny(lower, "rerank");
        var isTts = ContainsAny(lower, "tts") || lower.Contains("kokoro", StringComparison.Ordinal);
        var isStt = ContainsAny(lower, "whisper", "transcri");
        var isImage = hasImagePrice ||
                      ContainsAny(lower, "imagine", "dall-e", "dalle", "gpt-image", "flux", "stable-diffusion", "image-gen");

        var vision = promptImagePrice ||
                     ContainsAny(lower, "vision", "gpt-4o", "gpt-4.1", "claude", "gemini", "grok-4", "grok-2-vision");

        // "latest" and most grok chat models accept images; language-models will refine.
        if (lower is "latest" or "grok-4" or "grok-4.6" || lower.StartsWith("grok-4.", StringComparison.Ordinal))
            vision = true;

        return new CloudModelSettings
        {
            Name = id,
            Label = id,
            SupportsTools = !isImage && !isTts && !isStt && !isEmbeddings && !isRerank,
            SupportsVision = vision && !isImage && !isTts && !isStt,
            ContextSize = context,
            IsImage = isImage,
            IsEdit = isImage && (ContainsAny(lower, "imagine", "gpt-image", "flux")),
            IsTts = isTts,
            IsTranscription = isStt,
            IsEmbeddings = isEmbeddings,
            IsReranking = isRerank
        };
    }

    private static bool LooksLikeVoiceSession(string lower) =>
        ContainsAny(lower, "grok-voice", "realtime") && !ContainsAny(lower, "whisper");

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    public static string FormatHttpError(System.Net.HttpStatusCode status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    return $"Provider error ({(int)status}): {msg.GetString()}";
                if (err.ValueKind == JsonValueKind.String)
                    return $"Provider error ({(int)status}): {err.GetString()}";
            }
            if (root.TryGetProperty("message", out var m))
                return $"Provider error ({(int)status}): {m.GetString()}";
        }
        catch
        {
            // fall through
        }

        var snippet = string.IsNullOrWhiteSpace(body)
            ? status.ToString()
            : body.Length > 240 ? body[..240] + "…" : body;
        return $"Provider error ({(int)status}): {snippet}";
    }

    private sealed class XaiLanguageModelsResponse
    {
        public List<XaiLanguageModel>? Models { get; set; }
    }

    private sealed class XaiLanguageModel
    {
        public string? Id { get; set; }
        public List<string>? InputModalities { get; set; }
        public List<string>? OutputModalities { get; set; }
        public List<string>? Aliases { get; set; }
    }

    private sealed class XaiImageModelsResponse
    {
        public List<XaiImageModel>? Models { get; set; }
    }

    private sealed class XaiImageModel
    {
        public string? Id { get; set; }
        public List<string>? InputModalities { get; set; }
    }

    private sealed class XaiVoicesResponse
    {
        public List<XaiVoice>? Voices { get; set; }
    }

    private sealed class XaiVoice
    {
        public string? VoiceId { get; set; }
        public string? Name { get; set; }
    }
}

public sealed record CloudDiscoveryResult(
    IReadOnlyList<CloudModelSettings> Models,
    IReadOnlyList<CloudTtsVoice> Voices,
    bool HasOpenAiAudio,
    bool HasXaiTts,
    bool HasXaiStt,
    bool HasXaiImageApi);
