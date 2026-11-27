using System.Text;
using System.Text.RegularExpressions;

namespace App.Core.ModelsDev;

/// <summary>
/// Matches Wizionic model ids (provider ids, Ollama tags, Lemonade names) to models.dev
/// entries. Matching is deliberately conservative: a miss is better than a wrong family.
/// </summary>
public static partial class ModelsDevMatcher
{
    private static readonly HashSet<string> IgnoredSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "latest", "instruct", "it", "chat", "gguf", "fp8", "fp16", "bf16", "int4", "int8",
        "awq", "gptq", "q4", "q5", "q8", "q4-k-m", "q5-k-m", "q8-0", "gpt-oss",
        "preview", "experimental", "exp"
    };

    private static readonly (string Needle, string Id)[] KnownAliases =
    {
        ("gemini", "google"),
        ("google", "google"),
        ("alibaba", "alibaba"),
        ("qwen", "alibaba"),
        ("dashscope", "alibaba"),
        ("kimi", "moonshotai"),
        ("moonshot", "moonshotai"),
        ("together", "togetherai"),
        ("fireworks", "fireworks-ai"),
        ("cloudflare", "cloudflare-workers-ai"),
        ("nvidia", "nvidia"),
        ("mistral", "mistral"),
        ("cerebras", "cerebras"),
        ("perplexity", "perplexity"),
        ("groq", "groq"),
        ("openrouter", "openrouter"),
        ("deepseek", "deepseek"),
        ("anthropic", "anthropic"),
        ("claude", "anthropic"),
        ("openai", "openai"),
        ("xai", "xai"),
        ("grok", "xai")
    };

    /// <summary>Infer the models.dev provider id from a saved provider's URL/name.</summary>
    public static string? InferProviderId(
        IEnumerable<ModelsDevProviderSummary> providers,
        string? displayName,
        string? baseUrl)
    {
        var list = providers.ToList();
        if (list.Count == 0)
            return null;

        var host = TryGetHost(baseUrl);
        if (!string.IsNullOrWhiteSpace(host))
        {
            var byHost = list.FirstOrDefault(p =>
                string.Equals(TryGetHost(p.Api), host, StringComparison.OrdinalIgnoreCase));
            if (byHost != null)
                return byHost.Id;
        }

        var name = (displayName ?? "").Trim();
        if (name.Length > 0)
        {
            var exact = list.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact.Id;

            foreach (var alias in KnownAliases)
            {
                if (!ContainsToken(name, alias.Needle))
                    continue;
                var match = list.FirstOrDefault(p =>
                    string.Equals(p.Id, alias.Id, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match.Id;
            }
        }

        var slug = Slug(name);
        if (slug.Length > 0)
        {
            var bySlug = list.FirstOrDefault(p =>
                string.Equals(p.Id, slug, StringComparison.OrdinalIgnoreCase));
            if (bySlug != null)
                return bySlug.Id;
        }

        return null;
    }

    /// <summary>Resolve metadata for a model served by a specific provider.</summary>
    public static ModelCatalogMetadata? Resolve(
        ModelsDevProvider? provider,
        string modelName,
        IReadOnlyDictionary<string, ModelsDevModel>? canonicalModels,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        var providerModel = provider == null ? null : FindProviderModel(provider, modelName);
        ModelsDevModel? canonical = null;

        if (!string.IsNullOrWhiteSpace(providerModel?.CanonicalModelId))
            canonicalModels?.TryGetValue(providerModel.CanonicalModelId!, out canonical);

        canonical ??= FindCanonical(canonicalModels, modelName);
        canonical ??= FindByAlias(aliases, canonicalModels, modelName);

        if (providerModel == null && canonical == null)
            return null;

        return BuildMetadata(providerModel, canonical, modelName);
    }

    /// <summary>Resolve provider-agnostic metadata for a local model name/tag.</summary>
    public static ModelCatalogMetadata? ResolveCanonical(
        IReadOnlyDictionary<string, ModelsDevModel>? canonicalModels,
        string modelName,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        var canonical = FindCanonical(canonicalModels, modelName)
                        ?? FindByAlias(aliases, canonicalModels, modelName);
        return canonical == null ? null : BuildMetadata(null, canonical, modelName);
    }

    private static ModelsDevModel? FindByAlias(
        IReadOnlyDictionary<string, string>? aliases,
        IReadOnlyDictionary<string, ModelsDevModel>? canonicalModels,
        string modelName)
    {
        if (aliases == null || aliases.Count == 0 || canonicalModels == null)
            return null;

        foreach (var key in CandidateKeys(modelName))
        {
            if (aliases.TryGetValue(key, out var canonicalId) &&
                canonicalModels.TryGetValue(canonicalId, out var match))
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Build a reseller-alias map (provider model id -> canonical id) from models.dev provider entries.
    /// This lets providers that are not listed on models.dev (e.g. CometAPI) still match
    /// entries like <c>deepseek-flash</c> to the underlying canonical model.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildAliases(
        IEnumerable<ModelsDevProvider> providers)
    {
        var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            if (provider.Models == null)
                continue;

            foreach (var pair in provider.Models)
            {
                var model = pair.Value;
                if (model == null || string.IsNullOrWhiteSpace(model.CanonicalModelId))
                    continue;

                var id = string.IsNullOrWhiteSpace(model.Id) ? pair.Key : model.Id;
                foreach (var key in CandidateKeys(id))
                {
                    if (key.Length == 0)
                        continue;

                    if (!votes.TryGetValue(key, out var counts))
                    {
                        counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        votes[key] = counts;
                    }

                    counts[model.CanonicalModelId!] =
                        counts.GetValueOrDefault(model.CanonicalModelId!) + 1;
                }
            }
        }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in votes)
        {
            var winner = pair.Value
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key.Length)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .First()
                .Key;
            aliases[pair.Key] = winner;
        }

        return aliases;
    }

    public static ModelsDevModel? FindProviderModel(ModelsDevProvider provider, string modelName)
    {
        var name = (modelName ?? "").Trim();
        if (name.Length == 0 || provider.Models == null || provider.Models.Count == 0)
            return null;

        if (provider.Models.TryGetValue(name, out var direct))
            return direct;

        foreach (var pair in provider.Models)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Value.Id, name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        var keys = CandidateKeys(name);
        foreach (var pair in provider.Models)
        {
            var candidateKeys = CandidateKeys(string.IsNullOrWhiteSpace(pair.Value.Id) ? pair.Key : pair.Value.Id);
            if (candidateKeys.Overlaps(keys))
                return pair.Value;
        }

        return null;
    }

    public static ModelsDevModel? FindCanonical(
        IReadOnlyDictionary<string, ModelsDevModel>? canonicalModels,
        string modelName)
    {
        if (canonicalModels == null || canonicalModels.Count == 0)
            return null;

        var name = (modelName ?? "").Trim();
        if (name.Length == 0)
            return null;

        if (canonicalModels.TryGetValue(name, out var direct))
            return direct;

        var wanted = CandidateKeys(name);
        ModelsDevModel? best = null;
        var bestScore = 0;
        var bestLength = int.MaxValue;

        foreach (var pair in canonicalModels)
        {
            var id = string.IsNullOrWhiteSpace(pair.Value.Id) ? pair.Key : pair.Value.Id;
            var keys = CandidateKeys(id);
            if (!keys.Overlaps(wanted))
                continue;

            var score = keys.Overlaps(wanted) ? 10 : 0;
            if (pair.Value.OpenWeights == true)
                score += 2;
            if (string.Equals(pair.Value.Type, "chat", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(pair.Value.Type))
                score += 1;

            if (score > bestScore || (score == bestScore && id.Length < bestLength))
            {
                best = pair.Value;
                bestScore = score;
                bestLength = id.Length;
            }
        }

        if (best != null)
            return best;

        // Last resort: same family base + parameter size (e.g. ollama "qwen3:8b").
        var wantedFamily = FamilyWithSize(name);
        if (wantedFamily == null)
            return null;

        foreach (var pair in canonicalModels)
        {
            var id = string.IsNullOrWhiteSpace(pair.Value.Id) ? pair.Key : pair.Value.Id;
            if (FamilyWithSize(id) == wantedFamily)
                return pair.Value;
        }

        return null;
    }

    private static ModelCatalogMetadata BuildMetadata(
        ModelsDevModel? providerModel,
        ModelsDevModel? canonical,
        string requestedName)
    {
        var type = FirstNonEmpty(providerModel?.Type, canonical?.Type).ToLowerInvariant();
        var input = providerModel?.Modalities?.Input ?? canonical?.Modalities?.Input ?? new List<string>();
        var output = providerModel?.Modalities?.Output ?? canonical?.Modalities?.Output ?? new List<string>();

        var inputImage = Has(input, "image");
        var inputPdf = Has(input, "pdf");
        var outputImage = Has(output, "image");
        var inputAudio = Has(input, "audio");
        var outputAudio = Has(output, "audio");
        var attachment = providerModel?.Attachment ?? canonical?.Attachment ?? false;

        var isImage = type is "image" || outputImage;
        var isVideo = type is "video";
        var isSpeech = type is "speech";
        var isTranscription = type is "transcription";
        var isEmbedding = type is "embedding";
        var isReranking = type is "reranking";
        var isRealtime = type is "realtime";

        if (isVideo || isRealtime)
        {
            // Wizionic has no video/live-session surface; report it but let callers skip it.
            return new ModelCatalogMetadata(
                ModelsDevId: providerModel?.CanonicalModelId ?? canonical?.Id ?? providerModel?.Id ?? requestedName,
                SupportsTools: false,
                SupportsVision: inputImage || inputPdf,
                IsImage: false,
                IsVideo: isVideo,
                IsRealtime: isRealtime,
                Family: FirstNonEmpty(providerModel?.Family, canonical?.Family),
                InputModalities: input.ToList(),
                OutputModalities: output.ToList());
        }

        var tools = providerModel?.ToolCall ?? canonical?.ToolCall;
        // models.dev "attachment" includes audio; Wizionic vision means images/PDFs.
        var vision = inputImage || inputPdf ||
                     (attachment && !inputAudio && !isTranscription && !isSpeech);
        var reasoning = providerModel?.Reasoning ?? canonical?.Reasoning;

        var context = ToInt(providerModel?.Limit?.Context ?? canonical?.Limit?.Context);
        var outputLimit = ToInt(providerModel?.Limit?.Output ?? canonical?.Limit?.Output);

        var cost = providerModel?.Cost ?? canonical?.Cost;

        return new ModelCatalogMetadata(
            ModelsDevId: providerModel?.CanonicalModelId ?? canonical?.Id ?? providerModel?.Id ?? requestedName,
            ContextSize: context,
            MaxOutputTokens: outputLimit,
            SupportsTools: tools,
            SupportsVision: vision,
            SupportsReasoning: reasoning,
            IsImage: isImage,
            IsEdit: isImage && inputImage,
            IsTts: isSpeech || (outputAudio && !isTranscription),
            IsTranscription: isTranscription || (inputAudio && !outputAudio && !isImage && !isSpeech),
            IsEmbeddings: isEmbedding,
            IsReranking: isReranking,
            IsVideo: isVideo,
            IsRealtime: isRealtime,
            KnowledgeCutoff: FirstNonEmpty(providerModel?.Knowledge, canonical?.Knowledge),
            ReleaseDate: FirstNonEmpty(providerModel?.ReleaseDate, canonical?.ReleaseDate),
            Family: FirstNonEmpty(providerModel?.Family, canonical?.Family),
            CostInputPer1M: NonNegative(cost?.Input),
            CostOutputPer1M: NonNegative(cost?.Output),
            CostCacheReadPer1M: NonNegative(cost?.CacheRead),
            InputModalities: input.ToList(),
            OutputModalities: output.ToList());
    }

    private static HashSet<string> CandidateKeys(string modelName)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = (modelName ?? "").Trim();
        if (raw.Length == 0)
            return keys;

        // Vendor path and Ollama tag: "meta/llama-3.2-3b", "qwen3:8b".
        var lastSegment = raw;
        var slash = lastSegment.LastIndexOf('/');
        if (slash >= 0 && slash < lastSegment.Length - 1)
            lastSegment = lastSegment[(slash + 1)..];
        var withoutTag = lastSegment;
        var colon = withoutTag.IndexOf(':');
        if (colon > 0)
            withoutTag = withoutTag[..colon];

        foreach (var value in new[] { raw, lastSegment, withoutTag })
            AddVariants(keys, value);

        // Snapshot/date suffixes: deepseek-v3-1-250821 -> deepseek-v3-1
        foreach (var key in keys.ToList())
        {
            var stripped = TrimSnapshotSuffix(key);
            if (!string.Equals(stripped, key, StringComparison.OrdinalIgnoreCase))
                AddVariants(keys, stripped);
        }

        // Drop common deployment suffixes ("-instruct", "-Q4_K_M", "-latest", "-preview").
        foreach (var key in keys.ToList())
        {
            var parts = key.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (parts.Count > 1 && IgnoredSuffixes.Contains(parts[^1]))
                parts.RemoveAt(parts.Count - 1);
            var trimmed = string.Join('-', parts);
            if (trimmed.Length > 0)
                AddVariants(keys, trimmed);
        }

        return keys;
    }

    private static void AddVariants(HashSet<string> keys, string value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0)
            return;

        keys.Add(normalized);
        keys.Add(Compact(normalized));
    }

    private static string TrimSnapshotSuffix(string normalized)
    {
        var value = IsoDateSuffixRegex().Replace(normalized, "");
        value = SixDigitDateSuffixRegex().Replace(value, "");
        value = FourDigitDateSuffixRegex().Replace(value, "");
        return value.Trim('-');
    }

    private static string? FamilyWithSize(string modelName)
    {
        var normalized = Normalize(modelName);
        var match = SizeRegex().Match(normalized);
        if (!match.Success)
            return null;

        var size = match.Value;
        var family = normalized[..match.Index].Trim('-');
        if (family.Length == 0)
            return null;

        // Drop trailing quantization/deployment words from the family.
        var parts = family.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && IgnoredSuffixes.Contains(parts[^1]))
            parts.RemoveAt(parts.Count - 1);

        return string.Join('-', parts) + "-" + size;
    }

    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else
                sb.Append('-');
        }

        var text = sb.ToString();
        text = MultiDashRegex().Replace(text, "-").Trim('-');
        return text;
    }

    private static string Compact(string normalized) =>
        normalized.Replace("-", "", StringComparison.Ordinal);

    private static string Slug(string value)
    {
        var normalized = Normalize(value);
        return normalized;
    }

    private static bool ContainsToken(string value, string token)
    {
        var wanted = Normalize(token);
        if (wanted.Length == 0)
            return false;

        return Normalize(value)
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var raw = url.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw.TrimStart('/');

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static int ToInt(long? value)
    {
        if (value is null or <= 0)
            return 0;
        return value.Value > int.MaxValue ? int.MaxValue : (int)value.Value;
    }

    private static double? NonNegative(double? value) =>
        value is >= 0 ? value : null;

    private static bool Has(List<string>? values, string needle) =>
        values?.Any(v => string.Equals(v, needle, StringComparison.OrdinalIgnoreCase)) == true;

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : (second ?? "");

    [GeneratedRegex(@"[-_]+")]
    private static partial Regex MultiDashRegex();

    [GeneratedRegex(@"\d+(?:\.\d+)?b")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"-(?:19|20)\d{2}-\d{2}-\d{2}$")]
    private static partial Regex IsoDateSuffixRegex();

    [GeneratedRegex(@"-(?:19|20)?\d{6}$")]
    private static partial Regex SixDigitDateSuffixRegex();

    [GeneratedRegex(@"-\d{4}$")]
    private static partial Regex FourDigitDateSuffixRegex();
}
