using ChatfishApp.Contracts;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Fetches Ollama model metadata via /api/show and merges it with <see cref="ProviderCatalog.LocalModelPatterns"/>.
/// Pattern matches override Ollama-reported vision/tools; context length always comes from Ollama metadata.
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
        try
        {
            var url = baseUrl.TrimEnd('/') + "/api/show";
            using var resp = await http.PostAsJsonAsync(url, new { name = modelName }, ct);
            if (!resp.IsSuccessStatusCode)
                return FallbackFromPattern(modelName);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return ParseShowResponse(doc.RootElement, modelName);
        }
        catch
        {
            return FallbackFromPattern(modelName);
        }
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

    /// <summary>
    /// Merges live Ollama metadata with catalog patterns and any existing user overrides.
    /// </summary>
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
                if (!prop.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (prop.Value.TryGetInt32(out int ctx) && ctx > 0)
                    return ctx;

                if (prop.Value.TryGetInt64(out long ctx64) && ctx64 > 0)
                    return (int)Math.Min(ctx64, int.MaxValue);
            }
        }

        if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.String)
        {
            var text = parameters.GetString() ?? "";
            var match = NumCtxRegex().Match(text);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed) && parsed > 0)
                return parsed;
        }

        return DefaultContextSize;
    }

    [GeneratedRegex(@"num_ctx\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NumCtxRegex();
}