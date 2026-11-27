using System.Net.Http.Json;
using System.Text.Json;
using App.Core.ModelsDev;

namespace App.Services;

/// <summary>
/// Server-side cache for models.dev provider/model metadata.
/// Keeps the multi-megabyte upstream catalog off client devices and refreshes once a day.
/// </summary>
public sealed class ModelsDevCatalogService
{
    private const string UpstreamProvidersUrl = "https://models.dev/api.json?type=all";
    private const string UpstreamModelsUrl = "https://models.dev/models.json?type=all";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelsDevCatalogService> _logger;
    private readonly SemaphoreSlim _providersGate = new(1, 1);
    private readonly SemaphoreSlim _modelsGate = new(1, 1);
    private readonly SemaphoreSlim _aliasesGate = new(1, 1);

    private Dictionary<string, ModelsDevProvider>? _providers;
    private DateTimeOffset _providersAt;
    private Dictionary<string, ModelsDevModel>? _models;
    private DateTimeOffset _modelsAt;
    private IReadOnlyDictionary<string, string>? _aliases;
    private DateTimeOffset _aliasesAt;

    public ModelsDevCatalogService(
        IHttpClientFactory httpClientFactory,
        ILogger<ModelsDevCatalogService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModelsDevProviderSummary>> GetProvidersAsync(CancellationToken ct = default)
    {
        var providers = await EnsureProvidersAsync(ct);
        return providers.Values
            .Select(p => p.ToSummary())
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ModelsDevProvider?> GetProviderAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var providers = await EnsureProvidersAsync(ct);
        return providers.TryGetValue(id.Trim(), out var provider)
            ? provider.CloneWithoutDescription()
            : null;
    }

    public async Task<IReadOnlyDictionary<string, ModelsDevModel>> GetModelsAsync(CancellationToken ct = default)
    {
        if (_models is { Count: > 0 } cached && IsFresh(_modelsAt))
            return cached;

        await _modelsGate.WaitAsync(ct);
        try
        {
            if (_models is { Count: > 0 } again && IsFresh(_modelsAt))
                return again;

            var client = _httpClientFactory.CreateClient("models-dev");
            var loaded = await client.GetFromJsonAsync<Dictionary<string, ModelsDevModel>>(
                UpstreamModelsUrl, JsonOptions, ct);
            if (loaded is { Count: > 0 })
            {
                foreach (var pair in loaded)
                {
                    if (string.IsNullOrWhiteSpace(pair.Value.Id))
                        pair.Value.Id = pair.Key;
                }

                _models = new Dictionary<string, ModelsDevModel>(loaded, StringComparer.OrdinalIgnoreCase);
                _modelsAt = DateTimeOffset.UtcNow;
                return _models;
            }

            if (_models is { Count: > 0 })
                return _models;

            throw new InvalidOperationException("models.dev returned an empty model catalog.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_models is { Count: > 0 })
            {
                _logger.LogWarning(ex, "models.dev model catalog refresh failed; serving cached data.");
                return _models;
            }

            _logger.LogWarning(ex, "models.dev model catalog fetch failed.");
            throw;
        }
        finally
        {
            _modelsGate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAliasesAsync(CancellationToken ct = default)
    {
        if (_aliases is { Count: > 0 } cached && IsFresh(_aliasesAt))
            return cached;

        await _aliasesGate.WaitAsync(ct);
        try
        {
            if (_aliases is { Count: > 0 } again && IsFresh(_aliasesAt))
                return again;

            var providers = await EnsureProvidersAsync(ct);
            _aliases = ModelsDevMatcher.BuildAliases(providers.Values);
            _aliasesAt = DateTimeOffset.UtcNow;
            return _aliases;
        }
        finally
        {
            _aliasesGate.Release();
        }
    }

    private async Task<Dictionary<string, ModelsDevProvider>> EnsureProvidersAsync(CancellationToken ct)
    {
        if (_providers is { Count: > 0 } cached && IsFresh(_providersAt))
            return cached;

        await _providersGate.WaitAsync(ct);
        try
        {
            if (_providers is { Count: > 0 } again && IsFresh(_providersAt))
                return again;

            var client = _httpClientFactory.CreateClient("models-dev");
            var loaded = await client.GetFromJsonAsync<Dictionary<string, ModelsDevProvider>>(
                UpstreamProvidersUrl, JsonOptions, ct);
            if (loaded is { Count: > 0 })
            {
                foreach (var pair in loaded)
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

                _providers = new Dictionary<string, ModelsDevProvider>(loaded, StringComparer.OrdinalIgnoreCase);
                _providersAt = DateTimeOffset.UtcNow;
                return _providers;
            }

            if (_providers is { Count: > 0 })
                return _providers;

            throw new InvalidOperationException("models.dev returned an empty provider catalog.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_providers is { Count: > 0 })
            {
                _logger.LogWarning(ex, "models.dev provider refresh failed; serving cached data.");
                return _providers;
            }

            _logger.LogWarning(ex, "models.dev provider catalog fetch failed.");
            throw;
        }
        finally
        {
            _providersGate.Release();
        }
    }

    private static bool IsFresh(DateTimeOffset loadedAt) =>
        loadedAt != default && DateTimeOffset.UtcNow - loadedAt < CacheTtl;
}
