using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatfishApp.Contracts;
using ChatfishApp.Core.Storage;

namespace ChatfishApp.Core.Ollama;

/// <summary>
/// Fetches model metadata via Ollama <c>/api/show</c>, with fallbacks for Lemonade
/// (or any OpenAI-compatible server) via <c>/v1/models</c> when Ollama show is unavailable.
/// </summary>
public static partial class OllamaCapabilitiesResolver
{
    private const int DefaultContextSize = 8192;

    public sealed record OllamaLiveMetadata(bool SupportsTools, bool SupportsVision, int ContextSize);

    public static async Task<OllamaLiveMetadata> FetchLiveMetadataAsync(
        HttpClient http,
        string baseUrl,
        string modelName,
        CancellationToken ct = default)
    {
        var origin = baseUrl.TrimEnd('/');

        // 1) Native Ollama show
        try
        {
            var url = origin + "/api/show";
            using var resp = await http.PostAsJsonAsync(url, new { name = modelName }, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var live = ParseShowResponse(doc.RootElement, modelName);
                if (live.ContextSize > 0)
                    return live;

                // Show worked but no context — try OpenAI models list (Lemonade-compatible).
                var fromList = await TryContextFromOpenAiModelsAsync(http, origin, modelName, ct);
                if (fromList > 0)
                    return live with { ContextSize = fromList };
                return live with { ContextSize = DefaultContextSize };
            }
        }
        catch
        {
            // fall through
        }

        // 2) Lemonade / OpenAI-compatible model catalog (when Ollama URL points at Lemonade)
        try
        {
            var fromOpenAi = await TryMetadataFromOpenAiModelsAsync(http, origin, modelName, ct);
            if (fromOpenAi != null)
                return fromOpenAi;
        }
        catch
        {
            // fall through
        }

        return FallbackFromPattern(modelName);
    }

    public static OllamaLiveMetadata ParseShowResponse(JsonElement root, string modelName)
    {
        var caps = root.TryGetProperty("capabilities", out var capabilities)
            ? capabilities.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new OllamaLiveMetadata(
            SupportsTools: caps.Contains("tools"),
            SupportsVision: caps.Contains("vision"),
            ContextSize: ParseContextSize(root));
    }

    public static OllamaModelSettings ResolveSettings(
        string modelName,
        OllamaLiveMetadata live,
        OllamaModelSettings? existing = null)
    {
        var (patternMatched, patternTools, patternVision) = ProviderCatalog.GetLocalPatternCapabilities(modelName);

        bool supportsTools = existing?.UserOverrideTools == true
            ? existing.SupportsTools
            : patternMatched
                ? patternTools
                : live.SupportsTools;

        bool supportsVision = existing?.UserOverrideVision == true
            ? existing.SupportsVision
            : patternMatched
                ? patternVision
                : live.SupportsVision;

        int contextSize = existing?.UserOverrideContext == true
            ? existing.ContextSize
            : live.ContextSize > 0
                ? live.ContextSize
                : DefaultContextSize;

        return new OllamaModelSettings
        {
            Name = modelName,
            Label = string.IsNullOrWhiteSpace(existing?.Label) ? modelName : existing.Label,
            SupportsTools = supportsTools,
            SupportsVision = supportsVision,
            ContextSize = contextSize,
            UserOverrideTools = existing?.UserOverrideTools ?? false,
            UserOverrideVision = existing?.UserOverrideVision ?? false,
            UserOverrideContext = existing?.UserOverrideContext ?? false
        };
    }

    public static OllamaModelSettings CreateDefaultSettings(string modelName, OllamaModelSettings? existing = null)
    {
        var live = FallbackFromPattern(modelName);
        return ResolveSettings(modelName, live, existing);
    }

    /// <summary>
    /// Lists chat-capable model ids. Prefers Ollama <c>/api/tags</c>; falls back to
    /// OpenAI-compatible <c>/v1/models</c> (Lemonade and similar) when tags is unavailable.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListModelNamesAsync(
        HttpClient http,
        string baseUrl,
        CancellationToken ct = default)
    {
        var origin = baseUrl.TrimEnd('/');

        try
        {
            using var resp = await http.GetAsync(origin + "/api/tags", ct);
            if (resp.IsSuccessStatusCode)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("models", out var models) &&
                    models.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (var m in models.EnumerateArray())
                    {
                        var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name))
                            names.Add(name);
                    }

                    if (names.Count > 0)
                        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
        }
        catch
        {
            // try OpenAI-compatible list
        }

        var fromOpenAi = await ListModelNamesFromOpenAiAsync(http, origin, ct);
        if (fromOpenAi.Count > 0)
            return fromOpenAi;

        return Array.Empty<string>();
    }

    private static async Task<IReadOnlyList<string>> ListModelNamesFromOpenAiAsync(
        HttpClient http, string origin, CancellationToken ct)
    {
        foreach (var path in new[] { "/v1/models", "/api/v1/models" })
        {
            try
            {
                using var resp = await http.GetAsync(origin + path, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    continue;

                var names = new List<string>();
                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    // Skip pure modality models when listing for chat (image/tts/stt)
                    if (m.TryGetProperty("labels", out var labs) && labs.ValueKind == JsonValueKind.Array)
                    {
                        var labels = labs.EnumerateArray()
                            .Select(l => l.GetString() ?? "")
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (labels.Contains("image") || labels.Contains("tts") ||
                            labels.Contains("transcription") || labels.Contains("embedding"))
                            continue;
                    }

                    names.Add(id);
                }

                if (names.Count > 0)
                    return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch
            {
                // try next path
            }
        }

        return Array.Empty<string>();
    }

    private static async Task<int> TryContextFromOpenAiModelsAsync(
        HttpClient http, string origin, string modelName, CancellationToken ct)
    {
        var meta = await TryMetadataFromOpenAiModelsAsync(http, origin, modelName, ct);
        return meta?.ContextSize ?? 0;
    }

    private static async Task<OllamaLiveMetadata?> TryMetadataFromOpenAiModelsAsync(
        HttpClient http, string origin, string modelName, CancellationToken ct)
    {
        foreach (var path in new[] { "/v1/models", "/api/v1/models" })
        {
            try
            {
                using var resp = await http.GetAsync(origin + path, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    continue;

                JsonElement? match = null;
                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    if (id.Equals(modelName, StringComparison.OrdinalIgnoreCase) ||
                        id.EndsWith("/" + modelName, StringComparison.OrdinalIgnoreCase) ||
                        modelName.Equals(id, StringComparison.OrdinalIgnoreCase))
                    {
                        match = m;
                        break;
                    }
                }

                // Fuzzy: Ollama tags sometimes include :latest suffix
                if (match == null)
                {
                    var bare = modelName.Split(':')[0];
                    foreach (var m in data.EnumerateArray())
                    {
                        var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        if (id.StartsWith(bare, StringComparison.OrdinalIgnoreCase) ||
                            bare.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                        {
                            match = m;
                            break;
                        }
                    }
                }

                if (match == null)
                    continue;

                var el = match.Value;
                int ctx = 0;
                if (el.TryGetProperty("max_context_window", out var mcw))
                {
                    if (mcw.TryGetInt32(out var c) && c > 0) ctx = c;
                    else if (mcw.TryGetInt64(out var c64) && c64 > 0) ctx = (int)Math.Min(c64, int.MaxValue);
                }

                var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (el.TryGetProperty("labels", out var labs) && labs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in labs.EnumerateArray())
                    {
                        var s = l.GetString();
                        if (!string.IsNullOrEmpty(s)) labels.Add(s);
                    }
                }

                bool tools = labels.Contains("tool-calling") || labels.Contains("tools") || !labels.Contains("image");
                bool vision = labels.Contains("vision");

                // Pure modality models are not chat — keep defaults
                if (labels.Contains("image") || labels.Contains("tts") || labels.Contains("transcription"))
                {
                    tools = false;
                    vision = labels.Contains("vision");
                }

                return new OllamaLiveMetadata(
                    SupportsTools: tools,
                    SupportsVision: vision,
                    ContextSize: ctx > 0 ? ctx : DefaultContextSize);
            }
            catch
            {
                // try next path
            }
        }

        return null;
    }

    private static OllamaLiveMetadata FallbackFromPattern(string modelName)
    {
        var (patternMatched, patternTools, patternVision) = ProviderCatalog.GetLocalPatternCapabilities(modelName);
        return new OllamaLiveMetadata(
            SupportsTools: patternMatched ? patternTools : true,
            SupportsVision: patternMatched ? patternVision : false,
            ContextSize: DefaultContextSize);
    }

    private static int ParseContextSize(JsonElement root)
    {
        if (root.TryGetProperty("model_info", out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in modelInfo.EnumerateObject())
            {
                if (!prop.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase) &&
                    !prop.Name.Contains("context_length", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (prop.Value.TryGetInt32(out int ctx) && ctx > 0)
                    return ctx;

                if (prop.Value.TryGetInt64(out long ctx64) && ctx64 > 0)
                    return (int)Math.Min(ctx64, int.MaxValue);
            }
        }

        // Some servers put details.context_length
        if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            if (details.TryGetProperty("context_length", out var cl) && cl.TryGetInt32(out int dctx) && dctx > 0)
                return dctx;
        }

        if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.String)
        {
            var text = parameters.GetString() ?? "";
            var match = NumCtxRegex().Match(text);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed) && parsed > 0)
                return parsed;
        }

        // 0 = unknown — caller can try /v1/models or fall back to DefaultContextSize.
        return 0;
    }

    [GeneratedRegex(@"num_ctx\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NumCtxRegex();
}
