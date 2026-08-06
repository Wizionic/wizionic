using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using App.Core.Storage;

namespace App.Core.Lemonade;

/// <summary>
/// Fetches and maps Lemonade <c>GET /v1/models</c> entries into <see cref="LemonadeModelSettings"/>.
/// </summary>
public static class LemonadeModelCatalogResolver
{
    private const int DefaultContextSize = 8192;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var url = (baseUrl ?? "http://localhost:13305").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
            return "http://localhost:13305";

        // Scheme-less values (e.g. "localhost:13305") must not stay relative — in the browser
        // they become same-origin paths like https://wizionic.com/localhost:13305 → NotFound.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url.TrimStart('/');
        }

        // Accept users pasting /v1 or /api/v1; store the server origin only.
        if (url.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/api/v1".Length].TrimEnd('/');
        else if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/v1".Length].TrimEnd('/');

        return string.IsNullOrWhiteSpace(url) ? "http://localhost:13305" : url;
    }

    public static string OpenAiV1Base(string baseUrl) =>
        NormalizeBaseUrl(baseUrl).TrimEnd('/') + "/v1/";

    public static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? apiKey)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
        return req;
    }

    public static async Task<LemonadeHealthInfo?> FetchHealthAsync(
        HttpClient http,
        string baseUrl,
        string? apiKey = null,
        CancellationToken ct = default)
    {
        var origin = NormalizeBaseUrl(baseUrl);
        using var req = CreateRequest(HttpMethod.Get, origin + "/v1/health", apiKey);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        var modelLoaded = root.TryGetProperty("model_loaded", out var m) ? m.GetString() : null;
        return new LemonadeHealthInfo(status ?? "ok", version, modelLoaded);
    }

    public static async Task<IReadOnlyList<LemonadeModelSettings>> FetchModelsAsync(
        HttpClient http,
        string baseUrl,
        string? apiKey = null,
        bool showAll = false,
        CancellationToken ct = default)
    {
        var origin = NormalizeBaseUrl(baseUrl);
        var url = origin + "/v1/models" + (showAll ? "?show_all=true" : "");
        using var req = CreateRequest(HttpMethod.Get, url, apiKey);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<LemonadeModelsResponse>(JsonOptions, ct);
        if (payload?.Data == null)
            return Array.Empty<LemonadeModelSettings>();

        return payload.Data
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => FromApiModel(m))
            .ToList();
    }

    public static LemonadeModelSettings FromApiModel(LemonadeApiModel model)
    {
        var labels = model.Labels ?? new List<string>();
        var labelSet = new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase);
        var recipe = model.Recipe ?? "";
        var isOmni = recipe.Equals("collection.omni", StringComparison.OrdinalIgnoreCase);

        bool has(string l) => labelSet.Contains(l);

        // Deployment labels (mutually exclusive with plain LLM for modality-only models).
        bool isImage = has("image");
        bool isEdit = has("edit");
        bool isTts = has("tts");
        bool isTranscription = has("transcription");
        bool isEmbeddings = has("embeddings");
        bool isReranking = has("reranking");
        bool isVision = has("vision");
        bool isToolCalling = has("tool-calling") || has("tools");

        bool isModalityOnly = isImage || isEdit || isTts || isTranscription || isEmbeddings || isReranking;

        // Chat LLMs and omni collections support tools by default; pure modality models do not.
        bool supportsTools = isOmni || (!isModalityOnly && isToolCalling) || (!isModalityOnly && !isToolCalling);
        // If labels explicitly omit tool-calling on a known tool model set, still default LLM to tools on.
        // User can override later in the editor.
        if (!isModalityOnly && !isOmni)
            supportsTools = true;
        if (isModalityOnly)
            supportsTools = false;

        int context = model.MaxContextWindow is > 0 ? model.MaxContextWindow.Value : DefaultContextSize;

        int? defSteps = model.ImageDefaults?.Steps;
        double? defCfg = model.ImageDefaults?.CfgScale;
        int? defW = model.ImageDefaults?.Width;
        int? defH = model.ImageDefaults?.Height;

        // Fallbacks for known turbo-style models when server omits image_defaults.
        if ((isImage || isEdit) && defSteps is null)
        {
            var id = model.Id ?? "";
            if (id.Contains("Z-Image", StringComparison.OrdinalIgnoreCase))
            {
                defSteps = 9;
                defCfg ??= 1.0;
                defW ??= 1024;
                defH ??= 1024;
            }
            else if (id.Contains("Turbo", StringComparison.OrdinalIgnoreCase))
            {
                defSteps = 4;
                defCfg ??= 1.0;
                defW ??= 512;
                defH ??= 512;
            }
            else
            {
                defSteps = 20;
                defCfg ??= 7.0;
                defW ??= 512;
                defH ??= 512;
            }
        }

        return new LemonadeModelSettings
        {
            Name = model.Id!.Trim(),
            Label = model.Id.Trim(),
            SupportsTools = supportsTools,
            SupportsVision = isVision || isOmni,
            IsVisionProxy = false,
            ContextSize = context,
            IsImage = isImage,
            IsEdit = isEdit,
            IsTts = isTts,
            IsTranscription = isTranscription,
            IsEmbeddings = isEmbeddings,
            IsReranking = isReranking,
            IsOmniCollection = isOmni,
            Recipe = string.IsNullOrWhiteSpace(recipe) ? null : recipe,
            Labels = labels.ToList(),
            SizeGb = model.Size,
            DefaultSteps = defSteps,
            DefaultCfgScale = defCfg,
            DefaultWidth = defW,
            DefaultHeight = defH
        };
    }

    public static LemonadeModelSettings ResolveSettings(
        LemonadeModelSettings discovered,
        LemonadeModelSettings? existing = null)
    {
        if (existing == null)
            return discovered;

        // Preserve user label and overrides; refresh modality flags from server.
        return new LemonadeModelSettings
        {
            Name = discovered.Name,
            Label = string.IsNullOrWhiteSpace(existing.Label) ? discovered.Label : existing.Label,
            SupportsTools = existing.UserOverrideTools ? existing.SupportsTools : discovered.SupportsTools,
            SupportsVision = existing.UserOverrideVision ? existing.SupportsVision : discovered.SupportsVision,
            IsVisionProxy = existing.IsVisionProxy &&
                            (existing.UserOverrideVision ? existing.SupportsVision : discovered.SupportsVision),
            ContextSize = existing.UserOverrideContext ? existing.ContextSize : discovered.ContextSize,
            IsImage = discovered.IsImage,
            IsEdit = discovered.IsEdit,
            IsTts = discovered.IsTts,
            IsTranscription = discovered.IsTranscription,
            IsEmbeddings = discovered.IsEmbeddings,
            IsReranking = discovered.IsReranking,
            IsOmniCollection = discovered.IsOmniCollection,
            Recipe = discovered.Recipe,
            Labels = discovered.Labels?.ToList() ?? new List<string>(),
            SizeGb = discovered.SizeGb,
            DefaultSteps = discovered.DefaultSteps,
            DefaultCfgScale = discovered.DefaultCfgScale,
            DefaultWidth = discovered.DefaultWidth,
            DefaultHeight = discovered.DefaultHeight,
            UserOverrideTools = existing.UserOverrideTools,
            UserOverrideVision = existing.UserOverrideVision,
            UserOverrideContext = existing.UserOverrideContext
        };
    }

    public static LemonadeModelSettings CreateDefaultSettings(string modelName, LemonadeModelSettings? existing = null)
    {
        var discovered = new LemonadeModelSettings
        {
            Name = modelName.Trim(),
            Label = existing?.Label is { Length: > 0 } ? existing.Label : modelName.Trim(),
            SupportsTools = true,
            SupportsVision = false,
            ContextSize = DefaultContextSize
        };
        return existing == null ? discovered : ResolveSettings(discovered, existing);
    }

    /// <summary>
    /// Pick first matching modality model name, preferring an existing default if still valid.
    /// </summary>
    public static string? PickDefault(
        string? current,
        IEnumerable<LemonadeModelSettings> models,
        Func<LemonadeModelSettings, bool> predicate)
    {
        var list = models.Where(predicate).Select(m => m.Name).ToList();
        if (list.Count == 0)
            return null;
        if (!string.IsNullOrWhiteSpace(current) &&
            list.Any(n => n.Equals(current, StringComparison.OrdinalIgnoreCase)))
            return current;
        return list[0];
    }
}

public sealed record LemonadeHealthInfo(string Status, string? Version, string? ModelLoaded);

public sealed class LemonadeModelsResponse
{
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("data")]
    public List<LemonadeApiModel>? Data { get; set; }
}

public sealed class LemonadeApiModel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("recipe")]
    public string? Recipe { get; set; }

    [JsonPropertyName("size")]
    public double? Size { get; set; }

    [JsonPropertyName("max_context_window")]
    public int? MaxContextWindow { get; set; }

    [JsonPropertyName("downloaded")]
    public bool? Downloaded { get; set; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; set; }

    [JsonPropertyName("checkpoint")]
    public string? Checkpoint { get; set; }

    [JsonPropertyName("image_defaults")]
    public LemonadeImageDefaultsDto? ImageDefaults { get; set; }
}

public sealed class LemonadeImageDefaultsDto
{
    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    [JsonPropertyName("cfg_scale")]
    public double? CfgScale { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}
