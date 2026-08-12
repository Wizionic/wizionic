using System.Text.Json;
using App.Core.Connectors;
using App.Core.Storage;
using App.Core.Sync;
using App.Core.UI;
using Microsoft.JSInterop;

namespace App.Shared.Services;

/// <summary>
/// Exports/imports settings categories for WebRTC sync.
/// Appearance uses ThemeService + NavLayout; other categories use <see cref="IKeyStore"/>.
/// </summary>
public sealed class SettingsSyncStore : ISettingsSyncStore
{
    private const string TsPrefix = "app-settings-ts-";
    private const string AppearanceBlobKey = "app-settings-appearance";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IKeyStore _keys;
    private readonly ISyncPreferencesStore _prefs;
    private readonly ThemeService? _theme;
    private readonly INavLayoutState? _navLayout;
    private readonly IJSRuntime? _js;

    public event Action? OnSettingsChanged;

    public SettingsSyncStore(
        IKeyStore keys,
        ISyncPreferencesStore prefs,
        ThemeService? theme = null,
        INavLayoutState? navLayout = null,
        IJSRuntime? js = null)
    {
        _keys = keys;
        _prefs = prefs;
        _theme = theme;
        _navLayout = navLayout;
        _js = js;
    }

    public async Task TouchCategoryAsync(string category, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(category))
            return;

        var ticks = DateTime.UtcNow.Ticks;
        await _prefs.SetStringAsync(TsPrefix + category, ticks.ToString(), ct);

        if (string.Equals(category, SettingsSyncCategory.Appearance, StringComparison.Ordinal))
            await PersistAppearanceBlobAsync(ct);
    }

    public async Task<IReadOnlyList<SyncManifestEntry>> LoadManifestEntriesAsync(
        IEnumerable<string>? categories = null,
        CancellationToken ct = default)
    {
        var list = new List<SyncManifestEntry>();
        var cats = categories?.ToArray() ?? SettingsSyncCategory.All;
        foreach (var cat in cats)
        {
            var payload = await ExportAsync(cat, ct);
            if (payload == null)
                continue;

            list.Add(new SyncManifestEntry(
                payload.Category,
                SettingsSyncCategory.DisplayName(payload.Category),
                payload.UpdatedTicks,
                SyncFingerprint.Compute(payload.DataJson)));
        }

        return list;
    }

    public async Task<SettingsSyncPayload?> ExportAsync(string category, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(category))
            return null;

        await _keys.LoadAsync(ct);
        var dataJson = category switch
        {
            SettingsSyncCategory.LocalAi => await ExportLocalAiAsync(ct),
            SettingsSyncCategory.Lemonade => await ExportLemonadeAsync(ct),
            SettingsSyncCategory.CloudProviders => ExportCloudProviders(),
            SettingsSyncCategory.HomeAssistant => ExportHomeAssistant(),
            SettingsSyncCategory.Tools => ExportTools(),
            SettingsSyncCategory.SystemPrompt => ExportSystemPrompt(),
            SettingsSyncCategory.Profile => ExportProfile(),
            SettingsSyncCategory.Memories => ExportMemories(),
            SettingsSyncCategory.Appearance => await ExportAppearanceAsync(ct),
            _ => null
        };

        if (dataJson == null)
            return null;

        var ticks = await GetTimestampAsync(category, ct);
        if (ticks <= 0)
            ticks = 1; // present but never touched — still offer for first sync

        return new SettingsSyncPayload(category, ticks, dataJson);
    }

    public async Task<bool> ShouldAcceptIncomingAsync(SettingsSyncPayload payload, CancellationToken ct = default)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.Category))
            return false;

        var localTicks = await GetTimestampAsync(payload.Category, ct);
        if (localTicks <= 0)
            return true;

        if (payload.UpdatedTicks > localTicks)
            return true;

        if (payload.UpdatedTicks < localTicks)
            return false;

        // Same clock: accept if content differs (fingerprint mismatch).
        var local = await ExportAsync(payload.Category, ct);
        if (local == null)
            return true;

        return !string.Equals(local.DataJson, payload.DataJson, StringComparison.Ordinal);
    }

    public async Task ApplyAsync(SettingsSyncPayload payload, CancellationToken ct = default)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.Category))
            return;

        await _keys.LoadAsync(ct);

        switch (payload.Category)
        {
            case SettingsSyncCategory.LocalAi:
                await ApplyLocalAiAsync(payload.DataJson, ct);
                break;
            case SettingsSyncCategory.Lemonade:
                await ApplyLemonadeAsync(payload.DataJson, ct);
                break;
            case SettingsSyncCategory.CloudProviders:
                await ApplyCloudProvidersAsync(payload.DataJson, ct);
                break;
            case SettingsSyncCategory.HomeAssistant:
                await ApplyHomeAssistantAsync(payload.DataJson, ct);
                break;
            case SettingsSyncCategory.Tools:
                await ApplyToolsAsync(payload.DataJson, ct);
                break;
            case SettingsSyncCategory.SystemPrompt:
                await ApplySystemPromptAsync(payload.DataJson, ct);
                break;
            case SettingsSyncCategory.Profile:
                await ApplyProfileAsync(payload.DataJson, ct);
                break;
            case SettingsSyncCategory.Memories:
                await ApplyMemoriesAsync(payload.DataJson, ct);
                break;
            case SettingsSyncCategory.Appearance:
                await ApplyAppearanceAsync(payload.DataJson, ct);
                break;
            default:
                return;
        }

        await _prefs.SetStringAsync(TsPrefix + payload.Category, payload.UpdatedTicks.ToString(), ct);
        OnSettingsChanged?.Invoke();
    }

    private async Task<long> GetTimestampAsync(string category, CancellationToken ct)
    {
        var raw = await _prefs.GetStringAsync(TsPrefix + category, ct);
        return long.TryParse(raw, out var ticks) ? ticks : 0;
    }

    // --- Export helpers ---

    private Task<string> ExportLocalAiAsync(CancellationToken ct)
    {
        var dto = new LocalAiSyncDto(
            _keys.OllamaBaseUrl,
            _keys.OllamaModelSettingsList.ToList());
        return Task.FromResult(JsonSerializer.Serialize(dto, JsonOpts));
    }

    private Task<string> ExportLemonadeAsync(CancellationToken ct)
    {
        var dto = new LemonadeSyncDto(
            _keys.LemonadeBaseUrl,
            _keys.LemonadeApiKey,
            _keys.LemonadeModelSettingsList.ToList(),
            _keys.LemonadeDefaultImageModel,
            _keys.LemonadeDefaultEditModel,
            _keys.LemonadeDefaultTtsModel,
            _keys.LemonadeDefaultSttModel,
            _keys.LemonadeDefaultVoice);
        return Task.FromResult(JsonSerializer.Serialize(dto, JsonOpts));
    }

    private string ExportCloudProviders()
    {
        var dto = new CloudProvidersSyncDto(
            _keys.GetKey("groq"),
            _keys.GetKey("gemini"),
            _keys.GetKey("openrouter"));
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    private string ExportHomeAssistant()
    {
        var dto = new HomeAssistantSyncDto(
            _keys.HomeAssistantBaseUrl,
            _keys.HomeAssistantToken,
            _keys.HomeAssistantAssistantName);
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    private string ExportTools()
    {
        var oauth = _keys.GetOAuthConnectors()
            .Select(c => new OAuthConnectorSyncDto(
                c.ConnectorId,
                c.Enabled,
                c.ConnectedAtUtc,
                c.AccountLabel ?? c.Tokens?.AccountLabel,
                c.Tokens is null
                    ? null
                    : new OAuthTokenSyncDto(
                        c.Tokens.AccessToken,
                        c.Tokens.RefreshToken,
                        c.Tokens.ExpiresAtUtc,
                        c.Tokens.TokenType,
                        c.Tokens.Scope,
                        c.Tokens.AccountLabel)))
            .ToList();

        var dto = new ToolsSyncDto(
            _keys.EnabledMcpServerNames.ToList(),
            _keys.GetAllMcpTokens().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            _keys.GetCustomConnectors().ToList(),
            oauth);
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    private string ExportSystemPrompt()
    {
        var dto = new SystemPromptSyncDto(
            _keys.IsSystemPromptCustomized,
            _keys.IsSystemPromptCustomized ? _keys.GetSystemPrompt() : null);
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    private string ExportProfile()
    {
        var p = _keys.GetUserProfile();
        return JsonSerializer.Serialize(p, JsonOpts);
    }

    private string ExportMemories()
    {
        return JsonSerializer.Serialize(_keys.GetMemories().ToList(), JsonOpts);
    }

    private async Task<string> ExportAppearanceAsync(CancellationToken ct)
    {
        var blob = await _prefs.GetStringAsync(AppearanceBlobKey, ct);
        if (!string.IsNullOrWhiteSpace(blob))
            return blob;

        var theme = _theme?.Theme ?? ThemeService.DefaultThemeId;
        var nav = _navLayout?.Mode.ToString().ToLowerInvariant() ?? "top";
        var dto = new AppearanceSyncDto(theme, nav);
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    private async Task PersistAppearanceBlobAsync(CancellationToken ct)
    {
        var theme = _theme?.Theme ?? ThemeService.DefaultThemeId;
        var nav = _navLayout?.Mode.ToString().ToLowerInvariant() ?? "top";
        var json = JsonSerializer.Serialize(new AppearanceSyncDto(theme, nav), JsonOpts);
        await _prefs.SetStringAsync(AppearanceBlobKey, json, ct);
    }

    // --- Apply helpers ---

    private async Task ApplyLocalAiAsync(string dataJson, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<LocalAiSyncDto>(dataJson, JsonOpts);
        if (dto == null) return;

        if (!string.IsNullOrWhiteSpace(dto.BaseUrl))
            await _keys.SetOllamaBaseUrlAsync(dto.BaseUrl, ct);

        if (dto.Models != null)
        {
            foreach (var m in dto.Models)
            {
                if (string.IsNullOrWhiteSpace(m.Name)) continue;
                await _keys.SaveOllamaModelSettingsAsync(m, ct);
            }
        }
    }

    private async Task ApplyLemonadeAsync(string dataJson, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<LemonadeSyncDto>(dataJson, JsonOpts);
        if (dto == null) return;

        if (!string.IsNullOrWhiteSpace(dto.BaseUrl))
            await _keys.SetLemonadeBaseUrlAsync(dto.BaseUrl, ct);

        await _keys.SetLemonadeApiKeyAsync(dto.ApiKey, ct);
        await _keys.SetLemonadeModalityDefaultsAsync(
            dto.DefaultImageModel,
            dto.DefaultEditModel,
            dto.DefaultTtsModel,
            dto.DefaultSttModel,
            dto.DefaultVoice,
            ct);

        if (dto.Models != null)
        {
            foreach (var m in dto.Models)
            {
                if (string.IsNullOrWhiteSpace(m.Name)) continue;
                await _keys.SaveLemonadeModelSettingsAsync(m, ct);
            }
        }
    }

    private async Task ApplyCloudProvidersAsync(string dataJson, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<CloudProvidersSyncDto>(dataJson, JsonOpts);
        if (dto == null) return;
        await _keys.SaveAllKeysAsync(dto.Groq ?? "", dto.Gemini ?? "", dto.OpenRouter ?? "", ct);
    }

    private async Task ApplyHomeAssistantAsync(string dataJson, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<HomeAssistantSyncDto>(dataJson, JsonOpts);
        if (dto == null) return;
        await _keys.SetHomeAssistantConfigAsync(
            dto.BaseUrl ?? "",
            dto.Token ?? "",
            dto.AssistantName ?? "",
            ct);
    }

    private async Task ApplyToolsAsync(string dataJson, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<ToolsSyncDto>(dataJson, JsonOpts);
        if (dto == null) return;

        if (dto.EnabledServers != null)
            await _keys.SetEnabledMcpServersAsync(dto.EnabledServers, ct);

        if (dto.Tokens != null)
        {
            foreach (var (name, token) in dto.Tokens)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                await _keys.SetMcpTokenAsync(name, token ?? "", ct);
            }
        }

        // Custom connectors: add any missing (do not remove local-only without full replace semantics).
        if (dto.CustomConnectors != null)
        {
            var existing = _keys.GetCustomConnectors()
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in dto.CustomConnectors)
            {
                if (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.ServerUrl))
                    continue;
                if (existing.Contains(c.Name))
                    continue;
                await _keys.AddCustomConnectorAsync(c.Name, c.ServerUrl, ct);
            }
        }

        // OAuth connectors: full replace when remote provides a list (LWW category apply).
        if (dto.OAuthConnectors != null)
        {
            var installs = dto.OAuthConnectors
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.ConnectorId))
                .Select(c =>
                {
                    OAuthTokenSet? tokens = null;
                    if (c.Tokens is not null && !string.IsNullOrWhiteSpace(c.Tokens.AccessToken))
                    {
                        tokens = new OAuthTokenSet(
                            c.Tokens.AccessToken,
                            c.Tokens.RefreshToken,
                            c.Tokens.ExpiresAtUtc,
                            c.Tokens.TokenType,
                            c.Tokens.Scope,
                            c.Tokens.AccountLabel ?? c.AccountLabel);
                    }

                    return new OAuthConnectorInstall(
                        c.ConnectorId!.Trim(),
                        c.Enabled,
                        tokens,
                        c.ConnectedAtUtc,
                        c.AccountLabel ?? tokens?.AccountLabel);
                })
                .ToList();

            await _keys.ReplaceOAuthConnectorsAsync(installs, ct);
        }
    }

    private async Task ApplySystemPromptAsync(string dataJson, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<SystemPromptSyncDto>(dataJson, JsonOpts);
        if (dto == null) return;

        if (dto.IsCustomized)
            await _keys.SetSystemPromptAsync(dto.Prompt ?? "", ct);
        else
            await _keys.ResetSystemPromptAsync(ct);
    }

    private async Task ApplyProfileAsync(string dataJson, CancellationToken ct)
    {
        var profile = JsonSerializer.Deserialize<UserProfileSettings>(dataJson, JsonOpts);
        if (profile == null) return;
        await _keys.SetUserProfileAsync(profile, ct);
    }

    private async Task ApplyMemoriesAsync(string dataJson, CancellationToken ct)
    {
        var remote = JsonSerializer.Deserialize<List<UserMemory>>(dataJson, JsonOpts);
        if (remote == null) return;

        // Full replace of memory list to match remote LWW snapshot.
        await _keys.ClearMemoriesAsync(ct);
        foreach (var m in remote.OrderBy(x => x.CreatedAtUtc))
        {
            if (string.IsNullOrWhiteSpace(m.Text)) continue;
            await _keys.AddMemoryAsync(m.Text, ct);
        }
    }

    private async Task ApplyAppearanceAsync(string dataJson, CancellationToken ct)
    {
        await _prefs.SetStringAsync(AppearanceBlobKey, dataJson, ct);
        var dto = JsonSerializer.Deserialize<AppearanceSyncDto>(dataJson, JsonOpts);
        if (dto == null || _js == null)
            return;

        if (_theme != null && !string.IsNullOrWhiteSpace(dto.Theme))
            await _theme.SetThemeAsync(dto.Theme, _js, ct);

        if (_navLayout != null && !string.IsNullOrWhiteSpace(dto.NavLayout))
        {
            var mode = Enum.TryParse<NavLayoutMode>(dto.NavLayout, ignoreCase: true, out var parsed)
                ? parsed
                : NavLayoutMode.Top;
            await _navLayout.SetModeAsync(mode, _js, ct);
        }
    }

    // --- DTOs ---

    private sealed record LocalAiSyncDto(
        string? BaseUrl,
        List<OllamaModelSettings>? Models);

    private sealed record LemonadeSyncDto(
        string? BaseUrl,
        string? ApiKey,
        List<LemonadeModelSettings>? Models,
        string? DefaultImageModel,
        string? DefaultEditModel,
        string? DefaultTtsModel,
        string? DefaultSttModel,
        string? DefaultVoice);

    private sealed record CloudProvidersSyncDto(string? Groq, string? Gemini, string? OpenRouter);

    private sealed record HomeAssistantSyncDto(string? BaseUrl, string? Token, string? AssistantName);

    private sealed record ToolsSyncDto(
        List<string>? EnabledServers,
        Dictionary<string, string>? Tokens,
        List<CustomMcpConnector>? CustomConnectors,
        List<OAuthConnectorSyncDto>? OAuthConnectors = null);

    private sealed record OAuthConnectorSyncDto(
        string? ConnectorId,
        bool Enabled,
        DateTimeOffset? ConnectedAtUtc,
        string? AccountLabel,
        OAuthTokenSyncDto? Tokens);

    private sealed record OAuthTokenSyncDto(
        string? AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAtUtc,
        string? TokenType,
        string? Scope,
        string? AccountLabel);

    private sealed record SystemPromptSyncDto(bool IsCustomized, string? Prompt);

    private sealed record AppearanceSyncDto(string Theme, string NavLayout);
}
