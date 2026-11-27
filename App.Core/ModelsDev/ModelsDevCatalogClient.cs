using System.Net.Http.Json;
using System.Text.Json;

namespace App.Core.ModelsDev;

/// <summary>
/// Fetches models.dev metadata through the Wizionic host cache when available,
/// then falls back to the public models.dev JSON endpoints (CORS is enabled upstream).
/// Results are cached per process for a day.
/// </summary>
public static class ModelsDevCatalogClient
{
    private const string HostProvidersPath = "/api/model-catalog/providers";
    private const string HostProviderPath = "/api/model-catalog/provider/";
    private const string HostModelsPath = "/api/model-catalog/models";
    private const string HostAliasesPath = "/api/model-catalog/aliases";
    private const string UpstreamProvidersUrl = "https://models.dev/api.json?type=all";
    private const string UpstreamModelsUrl = "https://models.dev/models.json?type=all";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly SemaphoreSlim ProviderGate = new(1, 1);
    private static readonly SemaphoreSlim FullProviderGate = new(1, 1);
    private static readonly SemaphoreSlim ModelsGate = new(1, 1);
    private static readonly SemaphoreSlim AliasGate = new(1, 1);

    private static IReadOnlyList<ModelsDevProviderSummary>? _summaries;
    private static DateTimeOffset _summariesAt;
    private static Dictionary<string, ModelsDevProvider>? _fullProviders;
    private static DateTimeOffset _fullProvidersAt;
    private static Dictionary<string, ModelsDevModel>? _models;
    private static DateTimeOffset _modelsAt;
    private static IReadOnlyDictionary<string, string>? _aliases;
    private static DateTimeOffset _aliasesAt;

    public static async Task<IReadOnlyList<ModelsDevProviderSummary>> GetProvidersAsync(
        HttpClient http,
        CancellationToken ct = default)
    {
        if (_summaries is { Count: > 0 } cached && IsFresh(_summariesAt))
            return cached;

        await ProviderGate.WaitAsync(ct);
        try
        {
            if (_summaries is { Count: > 0 } again && IsFresh(_summariesAt))
                return again;

            var host = await TryGetAbsoluteAsync<List<ModelsDevProviderSummary>>(
                http, HostProvidersPath, ct);
            if (host is { Count: > 0 })
            {
                _summaries = host;
                _summariesAt = DateTimeOffset.UtcNow;
                return host;
            }

            var full = await LoadFullProvidersAsync(http, ct);
            var summaries = full.Values
                .Select(p => p.ToSummary())
                .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _summaries = summaries;
            _summariesAt = DateTimeOffset.UtcNow;
            return summaries;
        }
        finally
        {
            ProviderGate.Release();
        }
    }

    public static async Task<ModelsDevProvider?> GetProviderAsync(
        HttpClient http,
        string providerId,
        CancellationToken ct = default)
    {
        var id = (providerId ?? "").Trim();
        if (id.Length == 0)
            return null;

        if (_fullProviders != null && IsFresh(_fullProvidersAt) &&
            _fullProviders.TryGetValue(id, out var cached))
            return cached;

        var host = await TryGetAbsoluteAsync<ModelsDevProvider>(
            http, HostProviderPath + Uri.EscapeDataString(id), ct);
        if (host != null && (host.Models?.Count ?? 0) > 0)
            return host;

        var full = await LoadFullProvidersAsync(http, ct);
        return full.TryGetValue(id, out var found) ? found : null;
    }

    public static async Task<IReadOnlyDictionary<string, ModelsDevModel>> GetModelsIndexAsync(
        HttpClient http,
        CancellationToken ct = default)
    {
        if (_models is { Count: > 0 } cached && IsFresh(_modelsAt))
            return cached;

        await ModelsGate.WaitAsync(ct);
        try
        {
            if (_models is { Count: > 0 } again && IsFresh(_modelsAt))
                return again;

            var host = await TryGetAbsoluteAsync<Dictionary<string, ModelsDevModel>>(
                http, HostModelsPath, ct);
            if (host is { Count: > 0 })
            {
                _models = new Dictionary<string, ModelsDevModel>(host, StringComparer.OrdinalIgnoreCase);
                _modelsAt = DateTimeOffset.UtcNow;
                return _models;
            }

            var upstream = await TryGetAbsoluteAsync<Dictionary<string, ModelsDevModel>>(
                http, UpstreamModelsUrl, ct);
            _models = upstream is { Count: > 0 }
                ? new Dictionary<string, ModelsDevModel>(upstream, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ModelsDevModel>(StringComparer.OrdinalIgnoreCase);
            _modelsAt = DateTimeOffset.UtcNow;
            return _models;
        }
        finally
        {
            ModelsGate.Release();
        }
    }

    public static async Task<IReadOnlyDictionary<string, string>?> GetModelAliasesAsync(
        HttpClient http,
        CancellationToken ct = default)
    {
        if (_aliases is { Count: > 0 } cached && IsFresh(_aliasesAt))
            return cached;

        await AliasGate.WaitAsync(ct);
        try
        {
            if (_aliases is { Count: > 0 } again && IsFresh(_aliasesAt))
                return again;

            var host = await TryGetAbsoluteAsync<Dictionary<string, string>>(
                http, HostAliasesPath, ct);
            if (host is { Count: > 0 })
            {
                _aliases = new Dictionary<string, string>(host, StringComparer.OrdinalIgnoreCase);
                _aliasesAt = DateTimeOffset.UtcNow;
                return _aliases;
            }

            var full = await LoadFullProvidersAsync(http, ct);
            var built = ModelsDevMatcher.BuildAliases(full.Values);
            _aliases = built;
            _aliasesAt = DateTimeOffset.UtcNow;
            return _aliases;
        }
        catch
        {
            return _aliases;
        }
        finally
        {
            AliasGate.Release();
        }
    }

    /// <summary>Canonical metadata lookup for a local Ollama/Lemonade model name.</summary>
    public static async Task<ModelCatalogMetadata?> GetCanonicalMetadataAsync(
        HttpClient http,
        string modelName,
        CancellationToken ct = default,
        TimeSpan? maxWait = null)
    {
        try
        {
            var index = maxWait is { } wait
                ? await TryGetModelsIndexAsync(http, wait, ct)
                : await GetModelsIndexAsync(http, ct);
            return index == null ? null : ModelsDevMatcher.ResolveCanonical(index, modelName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Load the canonical model index but give up after <paramref name="maxWait"/>.
    /// A slow catalog must never delay a local Ollama/Lemonade refresh.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ModelsDevModel>?> TryGetModelsIndexAsync(
        HttpClient http,
        TimeSpan maxWait,
        CancellationToken ct = default)
    {
        var load = SafeLoadModelsIndexAsync(http, ct);
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(maxWait, delayCts.Token);
        var done = await Task.WhenAny(load, delay);
        if (done != load)
            return null;

        delayCts.Cancel();
        return await load;
    }

    private static async Task<IReadOnlyDictionary<string, ModelsDevModel>?> SafeLoadModelsIndexAsync(
        HttpClient http,
        CancellationToken ct)
    {
        try
        {
            return await GetModelsIndexAsync(http, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve the provider entry and canonical index for a saved cloud provider.
    /// Never throws: a catalog outage must not block the vendor model refresh.
    /// </summary>
    public static async Task<(
        ModelsDevProvider? Provider,
        IReadOnlyDictionary<string, ModelsDevModel>? Models,
        IReadOnlyDictionary<string, string>? Aliases)> GetCloudEnrichmentAsync(
            HttpClient http,
            string? modelsDevProviderId,
            string? displayName,
            string? baseUrl,
            CancellationToken ct = default)
    {
        try
        {
            var id = (modelsDevProviderId ?? "").Trim();
            if (id.Length == 0)
            {
                var summaries = await GetProvidersAsync(http, ct);
                id = ModelsDevMatcher.InferProviderId(summaries, displayName, baseUrl) ?? "";
            }

            // Always load the canonical index and alias map: providers not listed on models.dev
            // (e.g. CometAPI) can still match individual model names.
            var modelsTask = GetModelsIndexAsync(http, ct);
            var aliasesTask = GetModelAliasesAsync(http, ct);

            ModelsDevProvider? provider = null;
            if (id.Length > 0)
                provider = await GetProviderAsync(http, id, ct);

            await Task.WhenAll(modelsTask, aliasesTask);
            return (provider, await modelsTask, await aliasesTask);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static async Task<Dictionary<string, ModelsDevProvider>> LoadFullProvidersAsync(
        HttpClient http,
        CancellationToken ct)
    {
        if (_fullProviders is { Count: > 0 } cached && IsFresh(_fullProvidersAt))
            return cached;

        await FullProviderGate.WaitAsync(ct);
        try
        {
            if (_fullProviders is { Count: > 0 } again && IsFresh(_fullProvidersAt))
                return again;

            var upstream = await TryGetAbsoluteAsync<Dictionary<string, ModelsDevProvider>>(
                http, UpstreamProvidersUrl, ct);
            var providers = upstream ?? new Dictionary<string, ModelsDevProvider>(StringComparer.OrdinalIgnoreCase);

            // Ensure every provider knows its id/key even when the JSON omitted it.
            foreach (var pair in providers)
            {
                if (string.IsNullOrWhiteSpace(pair.Value.Id))
                    pair.Value.Id = pair.Key;
                pair.Value.Models ??= new Dictionary<string, ModelsDevModel>(StringComparer.OrdinalIgnoreCase);
                foreach (var model in pair.Value.Models)
                {
                    if (string.IsNullOrWhiteSpace(model.Value.Id))
                        model.Value.Id = model.Key;
                }
            }

            _fullProviders = new Dictionary<string, ModelsDevProvider>(providers, StringComparer.OrdinalIgnoreCase);
            _fullProvidersAt = DateTimeOffset.UtcNow;
            return providers;
        }
        finally
        {
            FullProviderGate.Release();
        }
    }

    private static async Task<T?> TryGetAbsoluteAsync<T>(
        HttpClient http,
        string url,
        CancellationToken ct)
    {
        try
        {
            var absolute = http.BaseAddress == null
                ? new Uri(url, UriKind.Absolute)
                : new Uri(http.BaseAddress, url.TrimStart('/'));

            var timeoutSeconds = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? 30 : 15;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await http.GetAsync(absolute, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return default;

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, timeout.Token);
        }
        catch
        {
            return default;
        }
    }

    private static bool IsFresh(DateTimeOffset loadedAt) =>
        loadedAt != default && DateTimeOffset.UtcNow - loadedAt < CacheTtl;
}
