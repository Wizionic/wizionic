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
        OllamaModelSettings? existing = null,
        long sizeBytes = 0)
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
            SizeBytes = sizeBytes > 0 ? sizeBytes : (existing?.SizeBytes ?? 0),
            UserOverrideTools = existing?.UserOverrideTools ?? false,
            UserOverrideVision = existing?.UserOverrideVision ?? false,
            UserOverrideContext = existing?.UserOverrideContext ?? false
        };
    }

    /// <summary>Installed model entry from local Ollama <c>GET /api/tags</c>.</summary>
    public sealed record OllamaInstalledModel(string Name, long SizeBytes, string? Digest = null);

    /// <summary>Library catalog entry (e.g. from ollama.com) available to pull.</summary>
    public sealed record OllamaLibraryModel(string Name, long SizeBytes);

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "—";
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024, tb = gb * 1024;
        if (bytes >= tb)
            return $"{bytes / tb:0.#} TB";
        if (bytes >= gb)
            return $"{bytes / gb:0.#} GB";
        if (bytes >= mb)
            return $"{bytes / mb:0.#} MB";
        if (bytes >= kb)
            return $"{bytes / kb:0.#} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Local models from Ollama <c>/api/tags</c> including on-disk size.
    /// </summary>
    public static async Task<IReadOnlyList<OllamaInstalledModel>> ListInstalledModelsAsync(
        HttpClient http,
        string baseUrl,
        CancellationToken ct = default)
    {
        var origin = baseUrl.TrimEnd('/');
        try
        {
            using var resp = await http.GetAsync(origin + "/api/tags", ct);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<OllamaInstalledModel>();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return Array.Empty<OllamaInstalledModel>();

            var list = new List<OllamaInstalledModel>();
            foreach (var m in models.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                long size = 0;
                if (m.TryGetProperty("size", out var s))
                {
                    if (s.TryGetInt64(out var s64)) size = s64;
                    else if (s.TryGetInt32(out var s32)) size = s32;
                }
                var digest = m.TryGetProperty("digest", out var d) ? d.GetString() : null;
                list.Add(new OllamaInstalledModel(name.Trim(), size, digest));
            }

            return list
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<OllamaInstalledModel>();
        }
    }

    /// <summary>
    /// Curated smaller models suitable for local installs (not the ollama.com/api/tags “featured giants” list).
    /// Sizes are resolved from the public Ollama registry manifests when possible.
    /// </summary>
    public static async Task<IReadOnlyList<OllamaLibraryModel>> ListLibraryModelsAsync(
        HttpClient http,
        CancellationToken ct = default)
    {
        // Family:tag pairs aimed at consumer GPUs / CPU — roughly up to ~12B default quants.
        // ollama.com/api/tags only returns a handful of huge cloud models, so we do not use it.
        string[] curated =
        [
            "smollm:135m",
            "smollm:360m",
            "smollm:1.7b",
            "smollm2:135m",
            "smollm2:360m",
            "smollm2:1.7b",
            "functiongemma:270m",
            "gemma3:270m",
            "gemma3:1b",
            "gemma3:4b",
            "tinyllama:1.1b",
            "tinyllama:latest",
            "qwen2.5:0.5b",
            "qwen2.5:1.5b",
            "qwen2.5:3b",
            "qwen2.5:7b",
            "qwen3:0.6b",
            "qwen3:1.7b",
            "qwen3:4b",
            "qwen3:8b",
            "llama3.2:1b",
            "llama3.2:3b",
            "phi3:mini",
            "phi3:3.8b",
            "phi4-mini:latest",
            "ministral-3:3b",
            "gemma2:2b",
            "gemma2:9b",
            "mistral:7b",
            "llama3.1:8b",
            "deepseek-r1:1.5b",
            "deepseek-r1:7b",
            "deepseek-r1:8b",
            "moondream:latest",
            "moondream:1.8b",
            "llava:7b",
            "minicpm-v:latest",
            "minicpm-v4.6:1b",
            "nomic-embed-text:latest",
            "all-minilm:latest",
            "mxbai-embed-large:latest",
            "llama3.2-vision:11b",
            "qwen2.5-coder:1.5b",
            "qwen2.5-coder:3b",
            "qwen2.5-coder:7b",
            "codellama:7b",
            "stable-code:3b",
            "nemotron-mini:4b",
            "granite3.1-dense:2b",
            "granite3.1-moe:1b",
            "granite3.1-moe:3b"
        ];

        // Resolve sizes in parallel from registry.ollama.ai (sum of layer sizes).
        var tasks = curated.Select(async name =>
        {
            var size = await TryGetRegistryModelSizeAsync(http, name, ct);
            return new OllamaLibraryModel(name, size);
        });

        OllamaLibraryModel[] resolved;
        try
        {
            resolved = await Task.WhenAll(tasks);
        }
        catch
        {
            resolved = curated.Select(n => new OllamaLibraryModel(n, 0)).ToArray();
        }

        // Prefer known smaller first; unknown size at end. Soft-cap hides multi-hundred-GB mistakes.
        const long softMaxBytes = 20L * 1024 * 1024 * 1024; // 20 GB
        return resolved
            .Where(m => m.SizeBytes <= 0 || m.SizeBytes <= softMaxBytes)
            .OrderBy(m => m.SizeBytes <= 0 ? 1 : 0)
            .ThenBy(m => m.SizeBytes)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sum layer sizes from <c>https://registry.ollama.ai/v2/library/{model}/manifests/{tag}</c>.
    /// </summary>
    public static async Task<long> TryGetRegistryModelSizeAsync(
        HttpClient http,
        string modelRef,
        CancellationToken ct = default)
    {
        try
        {
            var (library, tag) = SplitModelRef(modelRef);
            if (string.IsNullOrEmpty(library))
                return 0;

            var url = $"https://registry.ollama.ai/v2/library/{Uri.EscapeDataString(library)}/manifests/{Uri.EscapeDataString(tag)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.docker.distribution.manifest.v2+json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return 0;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                return 0;

            long total = 0;
            foreach (var layer in layers.EnumerateArray())
            {
                if (layer.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) && sz > 0)
                    total += sz;
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static (string Library, string Tag) SplitModelRef(string modelRef)
    {
        var name = (modelRef ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            return ("", "latest");

        // namespace/model:tag or model:tag
        var colon = name.LastIndexOf(':');
        string library;
        string tag;
        if (colon > 0)
        {
            library = name[..colon];
            tag = name[(colon + 1)..];
        }
        else
        {
            library = name;
            tag = "latest";
        }

        // Registry path is only the model name under library/ (official models).
        // "library/llama3.2" style — strip accidental "library/" prefix.
        if (library.StartsWith("library/", StringComparison.OrdinalIgnoreCase))
            library = library["library/".Length..];

        // User/namespace models use different registry paths; only official library supported here.
        if (library.Contains('/'))
            return ("", "latest");

        return (library, string.IsNullOrWhiteSpace(tag) ? "latest" : tag);
    }

    /// <summary>Delete a model from the local Ollama store (<c>DELETE /api/delete</c>).</summary>
    public static async Task DeleteModelAsync(
        HttpClient http,
        string baseUrl,
        string modelName,
        CancellationToken ct = default)
    {
        modelName = (modelName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name is required.", nameof(modelName));

        var origin = baseUrl.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Delete, origin + "/api/delete")
        {
            Content = JsonContent.Create(new { name = modelName, model = modelName })
        };
        using var resp = await http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
            return;

        // Some builds accept POST
        using var post = await http.PostAsJsonAsync(origin + "/api/delete", new { name = modelName, model = modelName }, ct);
        if (!post.IsSuccessStatusCode)
        {
            var body = await post.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Ollama delete failed ({(int)post.StatusCode}): {Truncate(body, 200)}");
        }
    }

    /// <summary>
    /// Pull a model via <c>POST /api/pull</c> with streaming disabled so the call completes when the pull finishes.
    /// </summary>
    public static async Task PullModelAsync(
        HttpClient http,
        string baseUrl,
        string modelName,
        CancellationToken ct = default)
    {
        modelName = (modelName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name is required.", nameof(modelName));

        var origin = baseUrl.TrimEnd('/');
        using var resp = await http.PostAsJsonAsync(
            origin + "/api/pull",
            new { name = modelName, model = modelName, stream = false },
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Ollama pull failed ({(int)resp.StatusCode}): {Truncate(body, 300)}");
        }

        // Consume body (status JSON) so the connection closes cleanly.
        _ = await resp.Content.ReadAsStringAsync(ct);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

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
