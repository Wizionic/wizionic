using App.Core.Auth;
using App.Core.Browser;
using App.Core.Chat;
using App.Core.Storage;
using App.Core.Sync;
using App.Shared.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace App.Client.Services;

/// <summary>
/// Client-side service that maintains a SignalR connection to the server's SyncHub.
/// 
/// Responsibilities for Phase 1:
/// - Generate/persist a stable per-browser DeviceId (localStorage).
/// - Choose and persist a friendly DeviceName (user can rename later from the Sync page).
/// - When the user is authenticated (email login), connect to /sync-hub and register.
/// - Maintain a live list of other devices for the same user, with IsOnline + LastActive.
/// - Raise OnChanged so pages (Sync.razor) can re-render.
/// 
/// The same connection will later carry WebRTC signaling messages (Phase 2) without
/// ever sending actual chat history blobs through the server.
/// 
/// Only users who have logged in with email (WasmAuthService.IsAuthenticated) get
/// a meaningful device list. Guest sessions do not participate in cross-device sync.
/// </summary>
public class WasmSyncService : ISyncService, IWebRtcTransportCallbacks
{
    private readonly IJSRuntime _js;
    private readonly IWebRtcTransport _webrtc;
    private readonly HttpClient _http; // only used to resolve the base address for the hub
    private readonly IAuthService _auth;
    private readonly IConversationStore _conversationStore;
    private readonly INoteStore _noteStore;
    private readonly IGalleryStore _galleryStore;
    private readonly IStorageQuotaService _storageQuota;
    private readonly ChatModelCatalogService _modelCatalog;
    private readonly IKeyStore _keyStore;
    private readonly ChatCompletionService _chatCompletion;
    private readonly ISyncPreferencesStore _prefs;
    private readonly ISettingsSyncStore _settingsSync;
    private readonly WebRtcSyncCoordinator _coordinator;

    private HubConnection? _hub;
    private bool _initialized;

    private const string DeviceIdKey = "app-device-id";
    private const string DeviceNameKey = "app-device-name";
    private const string AiServerDeviceIdKey = "app-ai-server-device-id";
    private const string AiServerNameKey = "app-ai-server-name";
    private const string KnownDevicesKey = "app-known-devices";
    private const string SyncTargetDevicesKey = "app-sync-target-devices";
    private const string AutoSyncChatKey = "app-auto-sync-chat";
    private const string AutoSyncNotesKey = "app-auto-sync-notes";
    private const string AutoSyncGalleryKey = "app-auto-sync-gallery";
    private const string AutoSyncCalendarKey = "app-auto-sync-calendar";
    private const string SyncToAllDevicesKey = "app-sync-to-all-devices";
    private const string AutoSyncLocalAiKey = "app-auto-sync-local-ai";
    private const string AutoSyncLemonadeKey = "app-auto-sync-lemonade";
    private const string AutoSyncCloudProvidersKey = "app-auto-sync-cloud-providers";
    private const string AutoSyncModelProfilesKey = "app-auto-sync-model-profiles";
    private const string AutoSyncHomeAssistantKey = "app-auto-sync-home-assistant";
    private const string AutoSyncToolsKey = "app-auto-sync-tools";
    private const string AutoSyncSystemPromptKey = "app-auto-sync-system-prompt";
    private const string AutoSyncProfileKey = "app-auto-sync-profile";
    private const string AutoSyncMemoriesKey = "app-auto-sync-memories";
    private const string AutoSyncAppearanceKey = "app-auto-sync-appearance";
    private const string AutoSyncSkillsKey = "app-auto-sync-skills";
    private const string AiProxyDataChannelLabel = "app-ai-proxy";

    public string? MyDeviceId { get; private set; }
    public string MyDeviceName { get; private set; } = "This browser";

    public IReadOnlyList<SyncDeviceInfo> Devices { get; private set; } = Array.Empty<SyncDeviceInfo>();

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>Device ID of the peer browser that handles AI completions for this client (per-user prefs).</summary>
    public string? AiServerDeviceId { get; private set; }

    /// <summary>Last known friendly name of the selected AI server (per-user; used when the peer is absent from presence).</summary>
    private string? _aiServerName;

    private List<KnownDeviceRecord> _knownDevices = new();

    /// <summary>Models available on the selected AI server device (populated over WebRTC).</summary>
    public IReadOnlyList<SyncModelInfo> RemoteModels { get; private set; } = Array.Empty<SyncModelInfo>();

    public bool IsAiProxyConnected { get; private set; }

    public string? AiProxyError { get; private set; }

    public bool SyncToAllDevices { get; private set; } = true;
    public bool AutoSyncChatHistory { get; private set; } = true;
    public bool AutoSyncNotes { get; private set; } = true;
    public bool AutoSyncGallery { get; private set; } = true;
    public bool AutoSyncCalendar { get; private set; } = true;
    public bool AutoSyncBookmarks => false;
    public bool AutoSyncInstalledApps => false;
    public bool AutoSyncLocalAi { get; private set; } = true;
    public bool AutoSyncLemonade { get; private set; } = true;
    public bool AutoSyncCloudProviders { get; private set; } = true;
    public bool AutoSyncModelProfiles { get; private set; } = true;
    public bool AutoSyncHomeAssistant { get; private set; } = true;
    public bool AutoSyncTools { get; private set; } = true;
    public bool AutoSyncSystemPrompt { get; private set; } = true;
    public bool AutoSyncProfile { get; private set; } = true;
    public bool AutoSyncMemories { get; private set; } = true;
    public bool AutoSyncAppearance { get; private set; } = true;
    public bool AutoSyncSkills { get; private set; } = true;
    public IReadOnlyCollection<string> SyncTargetDeviceIds => _syncTargetDeviceIds;

    public event Action? OnChanged;
    public event Action? OnSettingsChanged;

    /// <summary>
    /// Fired whenever a conversation is added or updated via incoming sync (background or foreground).
    /// Pages like Chat can subscribe to this to refresh their conversation list / sidebar.
    /// </summary>
    public event Action? OnConversationsChanged;

    /// <summary>
    /// Fired when a note is added or updated via incoming sync (background or foreground).
    /// </summary>
    public event Action? OnNotesChanged;
    public event Action? OnGalleryChanged;
    public event Action? OnCalendarsChanged;

    public event Action<string, string, string>? OnSyncPayloadReceived;
    public event Action<string, string, string>? OnAlbumSyncPayloadReceived;
    public event Action<string, string>? OnAlbumSyncAckReceived;

    private readonly HashSet<string> _syncTargetDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _devicesSnapshotInitialized;

    private readonly Dictionary<string, TaskCompletionSource<ChatResponsePayload>> _pendingChatRequests = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<bool>? _modelsListTcs;
    private bool _aiProxyConnecting;
    private CancellationTokenSource? _aiProxyReconnectCts;
    private int _lastPublishedModelCount = -1;

    public WasmSyncService(
        IJSRuntime js,
        IWebRtcTransport webrtc,
        HttpClient http,
        IAuthService auth,
        IConversationStore conversationStore,
        INoteStore noteStore,
        IGalleryStore galleryStore,
        ICalendarStore calendarStore,
        ChatModelCatalogService modelCatalog,
        IKeyStore keyStore,
        ChatCompletionService chatCompletion,
        ISyncPreferencesStore prefs,
        ISettingsSyncStore settingsSync)
    {
        _js = js;
        _webrtc = webrtc;
        _http = http;
        _auth = auth;
        _conversationStore = conversationStore;
        _noteStore = noteStore;
        _galleryStore = galleryStore;
        _modelCatalog = modelCatalog;
        _keyStore = keyStore;
        _chatCompletion = chatCompletion;
        _prefs = prefs;
        _settingsSync = settingsSync;

        _coordinator = new WebRtcSyncCoordinator(
            _webrtc,
            _conversationStore,
            _noteStore,
            _prefs,
            async (target, type, payload) =>
            {
                if (_hub?.State == HubConnectionState.Connected)
                    await _hub.InvokeAsync("SendToDevice", target, type, payload);
            },
            () => _hub?.State == HubConnectionState.Connected,
            transportCallbacks: this,
            settingsStore: _settingsSync,
            galleryStore: _galleryStore,
            storageQuota: null,
            calendarStore: calendarStore);

        _coordinator.OnConversationsChanged += () => OnConversationsChanged?.Invoke();
        _coordinator.OnNotesChanged += () => OnNotesChanged?.Invoke();
        _coordinator.OnGalleryChanged += () => OnGalleryChanged?.Invoke();
        _coordinator.OnCalendarsChanged += () => OnCalendarsChanged?.Invoke();
        _coordinator.OnSettingsChanged += () => OnSettingsChanged?.Invoke();
        _coordinator.OnSyncPayloadReceived += (c, j, f) => OnSyncPayloadReceived?.Invoke(c, j, f);
        _coordinator.OnSyncAckReceived += (c, f) => OnSyncAckReceived?.Invoke(c, f);
        _coordinator.OnNoteSyncPayloadReceived += (n, j, f) => OnNoteSyncPayloadReceived?.Invoke(n, j, f);
        _coordinator.OnNoteSyncAckReceived += (n, f) => OnNoteSyncAckReceived?.Invoke(n, f);
        _coordinator.OnAlbumSyncPayloadReceived += (a, j, f) => OnAlbumSyncPayloadReceived?.Invoke(a, j, f);
        _coordinator.OnAlbumSyncAckReceived += (a, f) => OnAlbumSyncAckReceived?.Invoke(a, f);

        _auth.OnChanged += OnAuthChanged;
    }

    private async void OnAuthChanged()
    {
        try
        {
            AiServerDeviceId = await GetUserSettingAsync(AiServerDeviceIdKey);
            if (string.IsNullOrWhiteSpace(AiServerDeviceId))
                AiServerDeviceId = null;
            _aiServerName = await GetUserSettingAsync(AiServerNameKey);
            await LoadSyncPreferencesAsync();
            await LoadKnownDevicesAsync();
            Devices = DeviceListMerger.Merge(
                Array.Empty<SyncDeviceInfo>(),
                _knownDevices,
                AiServerDeviceId,
                _aiServerName);
            await _keyStore.LoadAsync();
            try { await _modelCatalog.RefreshAsync(); } catch { /* optional */ }
            OnChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSync] Auth-changed store reload failed: {ex.Message}");
        }

        // If the user just logged in (or out), react.
        if (_auth.IsAuthenticated)
        {
            try { await EnsureConnectedAndRegisteredAsync(); }
            catch { /* ignore transient */ }
        }
        else
        {
            await StopAsync();
            Devices = Array.Empty<SyncDeviceInfo>();
            _knownDevices = new();
            OnChanged?.Invoke();
        }
    }

    private async Task StopAsync()
    {
        if (_hub != null)
        {
            try
            {
                await _hub.StopAsync();
            }
            catch { /* best effort */ }

            try
            {
                await _hub.DisposeAsync();
            }
            catch { /* best effort */ }

            _hub = null;
        }

        _devicesSnapshotInitialized = false;
        IsConnectedChanged();
    }

    /// <summary>
    /// Must be called early (e.g. from the Sync page OnInitializedAsync).
    /// Loads or creates the local device identity and prepares (but does not yet connect) the hub.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        // Load or create stable device identity (persisted in this browser profile).
        MyDeviceId = await _js.InvokeAsync<string?>("localStorage.getItem", DeviceIdKey);
        if (string.IsNullOrWhiteSpace(MyDeviceId))
        {
            MyDeviceId = Guid.NewGuid().ToString("N");
            await _js.InvokeVoidAsync("localStorage.setItem", DeviceIdKey, MyDeviceId);
        }

        var savedName = await _js.InvokeAsync<string?>("localStorage.getItem", DeviceNameKey);
        if (!string.IsNullOrWhiteSpace(savedName))
        {
            MyDeviceName = RepairMojibakeDeviceName(savedName);
            if (!string.Equals(MyDeviceName, savedName, StringComparison.Ordinal))
                await _js.InvokeVoidAsync("localStorage.setItem", DeviceNameKey, MyDeviceName);
        }
        else
        {
            // First time: try to give a slightly nicer default using navigator info.
            try
            {
                var ua = await _js.InvokeAsync<string>("eval", "navigator.userAgent || ''");
                MyDeviceName = DeriveFriendlyName(ua);
                await _js.InvokeVoidAsync("localStorage.setItem", DeviceNameKey, MyDeviceName);
            }
            catch
            {
                MyDeviceName = "This browser";
            }
        }

        // Restore persisted AI server preference (if any) — per user.
        AiServerDeviceId = await GetUserSettingAsync(AiServerDeviceIdKey);
        if (string.IsNullOrWhiteSpace(AiServerDeviceId))
            AiServerDeviceId = null;
        _aiServerName = await GetUserSettingAsync(AiServerNameKey);

        await LoadSyncPreferencesAsync();
        await LoadKnownDevicesAsync();
        // Show roster immediately (offline peers) before hub connects.
        Devices = DeviceListMerger.Merge(
            Array.Empty<SyncDeviceInfo>(),
            _knownDevices,
            AiServerDeviceId,
            _aiServerName);

        OnChanged?.Invoke();
    }

    private async Task LoadSyncPreferencesAsync()
    {
        try
        {
            _syncTargetDeviceIds.Clear();
            var targetsJson = await GetUserSettingAsync(SyncTargetDevicesKey);
            if (!string.IsNullOrWhiteSpace(targetsJson))
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(targetsJson);
                if (ids != null)
                {
                    foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)))
                        _syncTargetDeviceIds.Add(id);
                }
            }

            SyncToAllDevices = await LoadBoolPrefAsync(SyncToAllDevicesKey, defaultValue: true);
            AutoSyncChatHistory = await LoadBoolPrefAsync(AutoSyncChatKey, defaultValue: true);
            AutoSyncNotes = await LoadBoolPrefAsync(AutoSyncNotesKey, defaultValue: true);
            AutoSyncGallery = await LoadBoolPrefAsync(AutoSyncGalleryKey, defaultValue: true);
            AutoSyncCalendar = await LoadBoolPrefAsync(AutoSyncCalendarKey, defaultValue: true);
            AutoSyncLocalAi = await LoadBoolPrefAsync(AutoSyncLocalAiKey, defaultValue: true);
            AutoSyncLemonade = await LoadBoolPrefAsync(AutoSyncLemonadeKey, defaultValue: true);
            AutoSyncCloudProviders = await LoadBoolPrefAsync(AutoSyncCloudProvidersKey, defaultValue: true);
            AutoSyncModelProfiles = await LoadBoolPrefAsync(AutoSyncModelProfilesKey, defaultValue: true);
            AutoSyncHomeAssistant = await LoadBoolPrefAsync(AutoSyncHomeAssistantKey, defaultValue: true);
            AutoSyncTools = await LoadBoolPrefAsync(AutoSyncToolsKey, defaultValue: true);
            AutoSyncSystemPrompt = await LoadBoolPrefAsync(AutoSyncSystemPromptKey, defaultValue: true);
            AutoSyncProfile = await LoadBoolPrefAsync(AutoSyncProfileKey, defaultValue: true);
            AutoSyncMemories = await LoadBoolPrefAsync(AutoSyncMemoriesKey, defaultValue: true);
            AutoSyncAppearance = await LoadBoolPrefAsync(AutoSyncAppearanceKey, defaultValue: true);
            AutoSyncSkills = await LoadBoolPrefAsync(AutoSyncSkillsKey, defaultValue: true);
        }
        catch
        {
            // Ignore preference load errors.
        }

        EnsureCoordinatorWired();
    }

    private async Task<bool> LoadBoolPrefAsync(string key, bool defaultValue)
    {
        var raw = await GetUserSettingAsync(key);
        if (raw is null)
            return defaultValue;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return defaultValue;
    }

    private async Task LoadKnownDevicesAsync()
    {
        try
        {
            var json = await GetUserSettingAsync(KnownDevicesKey);
            if (string.IsNullOrWhiteSpace(json))
            {
                _knownDevices = new();
                return;
            }

            _knownDevices = System.Text.Json.JsonSerializer.Deserialize<List<KnownDeviceRecord>>(json)
                            ?? new List<KnownDeviceRecord>();
        }
        catch
        {
            _knownDevices = new();
        }
    }

    private async Task SaveKnownDevicesAsync()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_knownDevices);
            await SetUserSettingAsync(KnownDevicesKey, json);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Apply hub presence list merged with the local per-user known-devices roster
    /// so peers still appear offline after server restarts or when not currently connected.
    /// </summary>
    private async Task ApplyDevicesFromServerAsync(IReadOnlyList<SyncDeviceInfo>? serverList)
    {
        _knownDevices = DeviceListMerger.UpsertFromServer(_knownDevices, serverList);
        if (SyncToAllDevices && serverList != null)
        {
            var changed = false;
            foreach (var d in serverList)
            {
                if (IsSelf(d.DeviceId) || string.IsNullOrWhiteSpace(d.DeviceId))
                    continue;
                if (_syncTargetDeviceIds.Add(d.DeviceId))
                    changed = true;
            }
            if (changed)
            {
                await SetUserSettingAsync(
                    SyncTargetDevicesKey,
                    System.Text.Json.JsonSerializer.Serialize(_syncTargetDeviceIds.ToList()));
            }
        }
        await SaveKnownDevicesAsync();

        // Keep AI server name in sync when we still see the peer live.
        if (!string.IsNullOrEmpty(AiServerDeviceId) && serverList != null)
        {
            var live = serverList.FirstOrDefault(d =>
                string.Equals(d.DeviceId, AiServerDeviceId, StringComparison.OrdinalIgnoreCase));
            if (live != null && !string.IsNullOrWhiteSpace(live.Name))
            {
                _aiServerName = live.Name;
                await SetUserSettingAsync(AiServerNameKey, _aiServerName);
            }
        }

        Devices = DeviceListMerger.Merge(serverList, _knownDevices, AiServerDeviceId, _aiServerName);
    }

    private string UserPrefixed(string baseKey) => StorageNamespace.PrefixedKey(_auth, baseKey);

    private async Task<string?> GetUserSettingAsync(string baseKey)
    {
        var nk = UserPrefixed(baseKey);
        var value = await _js.InvokeAsync<string?>("localStorage.getItem", nk);
        if (value != null)
            return value;

        // One-time migrate from pre-multi-user unprefixed keys.
        var legacy = await _js.InvokeAsync<string?>("localStorage.getItem", baseKey);
        if (legacy != null)
        {
            await _js.InvokeVoidAsync("localStorage.setItem", nk, legacy);
            // Drop legacy so clears/updates do not resurrect old values.
            try { await _js.InvokeVoidAsync("localStorage.removeItem", baseKey); } catch { }
            return legacy;
        }

        return null;
    }

    private async Task SetUserSettingAsync(string baseKey, string? value)
    {
        var nk = UserPrefixed(baseKey);
        if (value is null)
        {
            // Clear both namespaced and legacy keys so uncheck survives refresh.
            try { await _js.InvokeVoidAsync("localStorage.removeItem", nk); } catch { }
            try { await _js.InvokeVoidAsync("localStorage.removeItem", baseKey); } catch { }
            return;
        }

        await _js.InvokeVoidAsync("localStorage.setItem", nk, value);
        // Prefer namespaced storage only.
        try { await _js.InvokeVoidAsync("localStorage.removeItem", baseKey); } catch { }
    }

    public async Task SetSyncTargetDevicesAsync(IEnumerable<string> deviceIds)
    {
        _syncTargetDeviceIds.Clear();
        foreach (var id in deviceIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            _syncTargetDeviceIds.Add(id);

        await SetUserSettingAsync(
            SyncTargetDevicesKey,
            System.Text.Json.JsonSerializer.Serialize(_syncTargetDeviceIds.ToList()));
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public async Task SetAutoSyncChatHistoryAsync(bool enabled)
    {
        AutoSyncChatHistory = enabled;
        await SetUserSettingAsync(AutoSyncChatKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public async Task SetAutoSyncNotesAsync(bool enabled)
    {
        AutoSyncNotes = enabled;
        await SetUserSettingAsync(AutoSyncNotesKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public async Task SetAutoSyncGalleryAsync(bool enabled)
    {
        AutoSyncGallery = enabled;
        await SetUserSettingAsync(AutoSyncGalleryKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public async Task SetAutoSyncCalendarAsync(bool enabled)
    {
        AutoSyncCalendar = enabled;
        await SetUserSettingAsync(AutoSyncCalendarKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public Task SetAutoSyncBookmarksAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncInstalledAppsAsync(bool enabled) => Task.CompletedTask;

    public async Task SetSyncToAllDevicesAsync(bool enabled)
    {
        SyncToAllDevices = enabled;
        await SetUserSettingAsync(SyncToAllDevicesKey, enabled ? "true" : "false");
        if (enabled)
        {
            foreach (var d in GetOtherDevices())
                _syncTargetDeviceIds.Add(d.DeviceId);
            await SetUserSettingAsync(
                SyncTargetDevicesKey,
                System.Text.Json.JsonSerializer.Serialize(_syncTargetDeviceIds.ToList()));
        }
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    private async Task SetAutoSyncFlagAsync(string key, Action<bool> assign, bool enabled)
    {
        assign(enabled);
        await SetUserSettingAsync(key, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public Task SetAutoSyncLocalAiAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncLocalAiKey, v => AutoSyncLocalAi = v, enabled);
    public Task SetAutoSyncLemonadeAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncLemonadeKey, v => AutoSyncLemonade = v, enabled);
    public Task SetAutoSyncCloudProvidersAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncCloudProvidersKey, v => AutoSyncCloudProviders = v, enabled);
    public Task SetAutoSyncModelProfilesAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncModelProfilesKey, v => AutoSyncModelProfiles = v, enabled);
    public Task SetAutoSyncHomeAssistantAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncHomeAssistantKey, v => AutoSyncHomeAssistant = v, enabled);
    public Task SetAutoSyncToolsAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncToolsKey, v => AutoSyncTools = v, enabled);
    public Task SetAutoSyncSystemPromptAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncSystemPromptKey, v => AutoSyncSystemPrompt = v, enabled);
    public Task SetAutoSyncProfileAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncProfileKey, v => AutoSyncProfile = v, enabled);
    public Task SetAutoSyncMemoriesAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncMemoriesKey, v => AutoSyncMemories = v, enabled);
    public Task SetAutoSyncAppearanceAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncAppearanceKey, v => AutoSyncAppearance = v, enabled);
    public Task SetAutoSyncSkillsAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncSkillsKey, v => AutoSyncSkills = v, enabled);

    private static string DeriveFriendlyName(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "This browser";

        string os = "Device";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) os = "Windows";
        else if (ua.Contains("Mac", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) os = "Mac/iOS";
        else if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) os = "Android";
        else if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)) os = "Linux";

        string browser = "Browser";
        if (ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase)) browser = "Edge";
        else if (ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) && !ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase)) browser = "Chrome";
        else if (ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase)) browser = "Firefox";
        else if (ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) && !ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) browser = "Safari";

        return $"{browser} \u2022 {os}";
    }

    /// <summary>
    /// Older builds wrote UTF-8 U+2022 (bullet) as Windows-1252 mojibake "â€¢".
    /// </summary>
    private static string RepairMojibakeDeviceName(string name) =>
        name.Replace("\u00E2\u20AC\u00A2", "\u2022", StringComparison.Ordinal);

    /// <summary>
    /// Connects (if not already) and registers this device with the server.
    /// Safe to call multiple times; it is idempotent for an already-registered session.
    /// </summary>
    public async Task EnsureConnectedAndRegisteredAsync()
    {
        await InitializeAsync();

        if (!_auth.IsAuthenticated || string.IsNullOrEmpty(_auth.Email))
        {
            // Guests do not get a live device list.
            return;
        }

        if (_hub == null)
        {
            // Build the connection. We append deviceId as a query string so the hub can see it early if desired.
            var baseUri = _http.BaseAddress?.ToString().TrimEnd('/') ?? "";
            var hubUrl = $"{baseUri}/sync-hub?deviceId={Uri.EscapeDataString(MyDeviceId ?? "")}";

            _hub = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    // Cookies are sent automatically for same-origin; no extra config needed.
                    // If we ever switch to JWT we would add options.AccessTokenProvider here.
                })
                .WithAutomaticReconnect()
                .Build();

            _hub.On<IReadOnlyList<SyncDeviceInfo>>("DevicesUpdated", list =>
            {
                var prevOnline = Devices
                    .Where(d => d.IsOnline)
                    .Select(d => d.DeviceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                _ = ApplyDevicesFromServerAndNotifyAsync(list, prevOnline);
            });

            WireSyncHandlers();

            _hub.Closed += async (ex) =>
            {
                IsConnectedChanged();
                // AutomaticReconnect will try to come back; when it does we will re-register in Reconnected.
            };

            _hub.Reconnected += async (connectionId) =>
            {
                // Re-register so the server knows this connection belongs to our device.
                await SafeRegisterAsync();
                IsConnectedChanged();
            };

            _hub.Reconnecting += _ =>
            {
                IsConnectedChanged();
                return Task.CompletedTask;
            };
        }

        if (_hub.State == HubConnectionState.Disconnected)
        {
            try
            {
                await _hub.StartAsync();
                IsConnectedChanged();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WasmSyncService] Hub connect failed: {ex.Message}");
                IsConnectedChanged();
                return;
            }
        }

        await SafeRegisterAsync();
        await PublishAiCapabilitiesAsync();
    }

    /// <summary>
    /// Reports how many AI models this browser can serve to peers (for the device list UI).
    /// </summary>
    public async Task PublishAiCapabilitiesAsync()
    {
        if (_hub?.State != HubConnectionState.Connected || string.IsNullOrEmpty(MyDeviceId))
            return;

        try
        {
            await _keyStore.LoadAsync();
            await _modelCatalog.RefreshAsync();
            var count = _modelCatalog.GetAvailableModels().Count;
            if (count == _lastPublishedModelCount)
                return;

            _lastPublishedModelCount = count;
            await _hub.InvokeAsync("UpdateAiCapabilities", MyDeviceId, count);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] UpdateAiCapabilities failed: {ex.Message}");
        }
    }

    private async Task SafeRegisterAsync()
    {
        if (_hub == null || _hub.State != HubConnectionState.Connected) return;

        try
        {
            await _hub.InvokeAsync("RegisterDevice", MyDeviceId ?? "", MyDeviceName);
            await PublishClientCapabilitiesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] RegisterDevice failed: {ex.Message}");
        }
    }

    private async Task PublishClientCapabilitiesAsync()
    {
        if (_hub?.State != HubConnectionState.Connected || string.IsNullOrEmpty(MyDeviceId))
            return;

        try
        {
            // WASM: not a native app, no bookmark/PWA browser-sync capability.
            await _hub.InvokeAsync("UpdateBrowserCapabilities", MyDeviceId, false, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] UpdateBrowserCapabilities failed: {ex.Message}");
        }
    }

    private void IsConnectedChanged()
    {
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Rename this device (persisted locally and sent to server).
    /// </summary>
    public async Task SetDeviceNameAsync(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;

        MyDeviceName = newName.Trim();
        await _js.InvokeVoidAsync("localStorage.setItem", DeviceNameKey, MyDeviceName);

        if (_hub?.State == HubConnectionState.Connected && !string.IsNullOrEmpty(MyDeviceId))
        {
            try
            {
                await _hub.InvokeAsync("UpdateDeviceName", MyDeviceId, MyDeviceName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WasmSyncService] UpdateDeviceName RPC failed: {ex.Message}");
            }
        }

        OnChanged?.Invoke();
    }

    /// <summary>
    /// Ask the server for a fresh snapshot (useful after reconnect or manual refresh).
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_hub?.State == HubConnectionState.Connected && !string.IsNullOrEmpty(MyDeviceId))
        {
            await SafeRegisterAsync();
            await PublishAiCapabilitiesAsync();
        }
        else if (_auth.IsAuthenticated)
        {
            await EnsureConnectedAndRegisteredAsync();
        }
    }

    /// <summary>
    /// Legacy relay path (kept for reference / fallback). Real Phase 2 data now uses WebRTC DataChannel
    /// via StartWebRtcSyncAsync (signaling only goes through the hub).
    /// </summary>
    public async Task SendSyncPayloadAsync(string targetDeviceId, string convoId, List<ChatMessage> messages)
    {
        if (_hub?.State != HubConnectionState.Connected || string.IsNullOrEmpty(targetDeviceId))
            return;

        try
        {
            var (title, titleIsCustom) = await _conversationStore.GetMetaTitleInfoAsync(convoId);
            var index = await _conversationStore.LoadIndexAsync();
            var meta = index.FirstOrDefault(c => c.Id == convoId);
            var isProtected = meta?.IsPasswordProtected == true;
            var proticks = meta?.ProtectionChangedTicks ?? 0;
            var json = ConvoSyncPayload.Serialize(convoId, title, messages, titleIsCustom, isProtected, proticks);
            await _hub.InvokeAsync("SendSyncPayload", targetDeviceId, convoId, json, MyDeviceId ?? "");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] SendSyncPayload (relay) failed: {ex.Message}");
        }
    }

    private void EnsureCoordinatorWired()
    {
        _coordinator.AutoSyncChatHistory = AutoSyncChatHistory;
        _coordinator.AutoSyncNotes = AutoSyncNotes;
        _coordinator.AutoSyncGallery = AutoSyncGallery;
        _coordinator.AutoSyncCalendar = AutoSyncCalendar;
        _coordinator.AutoSyncLocalAi = AutoSyncLocalAi;
        _coordinator.AutoSyncLemonade = AutoSyncLemonade;
        _coordinator.AutoSyncCloudProviders = AutoSyncCloudProviders;
        _coordinator.AutoSyncModelProfiles = AutoSyncModelProfiles;
        _coordinator.AutoSyncHomeAssistant = AutoSyncHomeAssistant;
        _coordinator.AutoSyncTools = AutoSyncTools;
        _coordinator.AutoSyncSystemPrompt = AutoSyncSystemPrompt;
        _coordinator.AutoSyncProfile = AutoSyncProfile;
        _coordinator.AutoSyncMemories = AutoSyncMemories;
        _coordinator.AutoSyncAppearance = AutoSyncAppearance;
        _coordinator.AutoSyncSkills = AutoSyncSkills;
        _coordinator.SyncTargetDeviceIds = GetEffectiveSyncTargetDeviceIds();
        _coordinator.IsSelf = IsSelf;
        _coordinator.LocalDeviceId = MyDeviceId;
        _coordinator.IsAuthenticated = () => _auth.IsAuthenticated;
        _coordinator.EnsureConnectedAsync = EnsureConnectedAndRegisteredAsync;
        _coordinator.GetDevices = () => Devices;
    }

    private void WireSyncHandlers()
    {
        if (_hub == null) return;

        _hub.On<string, string, string>("SyncPayloadReceived", async (convoId, json, fromDeviceId) =>
        {
            await _coordinator.HandleIncomingSyncPayloadAsync(convoId, json, fromDeviceId);
        });

        _hub.On<object>("SyncPayloadSent", _ =>
        {
            Console.WriteLine("[WasmSyncService] Sync payload acknowledged as sent to peer.");
        });

        _hub.On<string, string, string>("ReceiveSignaling", async (fromDeviceId, type, payload) =>
        {
            if (type is "webrtc-offer-ai" or "webrtc-answer-ai")
            {
                if (type == "webrtc-offer-ai")
                    await HandleWebRtcOfferAi(fromDeviceId, payload);
                else
                    await HandleWebRtcAnswerAi(fromDeviceId, payload);
                return;
            }

            if (type == "webrtc-ice" && TryUnwrapIcePayload(payload, out var senderPeerKey, out var iceJson)
                && IsAiProxyPeerKey(senderPeerKey))
            {
                var localPeerKey = GetAiProxyPeerKey(fromDeviceId);
                await _webrtc.AddIceCandidateAsync(localPeerKey, iceJson);
                return;
            }

            await _coordinator.HandleReceiveSignalingAsync(fromDeviceId, type, payload);
        });
    }

    private static bool TryUnwrapIcePayload(string payload, out string peerKey, out string iceJson)
    {
        peerKey = "";
        iceJson = payload;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("peerKey", out var pk) &&
                doc.RootElement.TryGetProperty("ice", out var ice))
            {
                peerKey = pk.GetString() ?? "";
                iceJson = ice.GetRawText();
                return !string.IsNullOrEmpty(peerKey);
            }
        }
        catch { }
        return false;
    }

    public Task StartWebRtcSyncAsync(string targetDeviceId, string convoId, List<ChatMessage> messages)
    {
        EnsureCoordinatorWired();
        return _coordinator.StartWebRtcSyncAsync(targetDeviceId, convoId, messages);
    }

    public Task StartWebRtcNoteSyncAsync(string targetDeviceId, string noteId, string title, List<ChatMessage> entries)
    {
        EnsureCoordinatorWired();
        return _coordinator.StartWebRtcNoteSyncAsync(targetDeviceId, noteId, title, entries);
    }

    public Task StartWebRtcAlbumSyncAsync(string targetDeviceId, string albumId, string title)
    {
        EnsureCoordinatorWired();
        return _coordinator.StartWebRtcAlbumSyncAsync(targetDeviceId, albumId, title);
    }

    public Task StartWebRtcAlbumImageSyncAsync(string targetDeviceId, string albumId, string imageId)
    {
        EnsureCoordinatorWired();
        return _coordinator.StartWebRtcAlbumImageSyncAsync(targetDeviceId, albumId, imageId);
    }

    public Task StartWebRtcBookmarkSyncAsync(string targetDeviceId, BrowserBookmark bookmark) =>
        Task.CompletedTask;

    public Task StartWebRtcFolderSyncAsync(string targetDeviceId, BrowserBookmarkFolder folder) =>
        Task.CompletedTask;

    public Task StartWebRtcSidebarAppSyncAsync(string targetDeviceId, SidebarApp app) =>
        Task.CompletedTask;

    public Task<int> SyncAllConversationsToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return _coordinator.SyncAllConversationsToDevicesAsync(targetDeviceIds);
    }

    public Task<int> SyncAllNotesToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return _coordinator.SyncAllNotesToDevicesAsync(targetDeviceIds);
    }

    public Task<int> SyncAllAlbumsToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return _coordinator.SyncAllAlbumsToDevicesAsync(targetDeviceIds);
    }

    public Task<int> SyncAllBookmarksToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncAllInstalledAppsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public void ScheduleAutoSyncConvoAfterLocalSave(string convoId, string? title = null)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncConvoAfterLocalSave(convoId, title);
    }

    public void ScheduleAutoSyncConvoDeleteAfterLocalDelete(string convoId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncConvoDeleteAfterLocalDelete(convoId, deletedAtUtc);
    }

    public void ScheduleAutoSyncNoteAfterLocalSave(string noteId, string title)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncNoteAfterLocalSave(noteId, title);
    }

    public void ScheduleAutoSyncNoteDeleteAfterLocalDelete(string noteId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncNoteDeleteAfterLocalDelete(noteId, deletedAtUtc);
    }

    public void ScheduleAutoSyncAlbumMetaAfterLocalSave(string albumId, string title)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncAlbumMetaAfterLocalSave(albumId, title);
    }

    public void ScheduleAutoSyncAlbumDeleteAfterLocalDelete(string albumId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncAlbumDeleteAfterLocalDelete(albumId, deletedAtUtc);
    }

    public void ScheduleAutoSyncAlbumImageAfterLocalSave(string albumId, string imageId)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncAlbumImageAfterLocalSave(albumId, imageId);
    }

    public void ScheduleAutoSyncAlbumImageDeleteAfterLocalDelete(string albumId, string imageId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncAlbumImageDeleteAfterLocalDelete(albumId, imageId, deletedAtUtc);
    }

    public void ScheduleAutoSyncCalendarAfterLocalSave(string calendarId)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncCalendarAfterLocalSave(calendarId);
    }

    public void ScheduleAutoSyncCalendarDeleteAfterLocalDelete(string calendarId, DateTime deletedAt)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncCalendarDeleteAfterLocalDelete(calendarId, deletedAt);
    }

    public void ScheduleAutoSyncEventAfterLocalSave(string calendarId, string eventId)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncEventAfterLocalSave(calendarId, eventId);
    }

    public void ScheduleAutoSyncEventDeleteAfterLocalDelete(string calendarId, string eventId, DateTime deletedAt)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncEventDeleteAfterLocalDelete(calendarId, eventId, deletedAt);
    }

    public Task StartWebRtcCalendarSyncAsync(string targetDeviceId, string calendarId)
    {
        EnsureCoordinatorWired();
        return _coordinator.StartWebRtcCalendarSyncAsync(targetDeviceId, calendarId);
    }

    public Task StartWebRtcCalendarEventSyncAsync(string targetDeviceId, string calendarId, string eventId)
    {
        EnsureCoordinatorWired();
        return _coordinator.StartWebRtcCalendarEventSyncAsync(targetDeviceId, calendarId, eventId);
    }

    public Task<int> SyncAllCalendarsToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return _coordinator.SyncAllCalendarsToDevicesAsync(targetDeviceIds);
    }

    public void ScheduleAutoSyncBookmarkAfterLocalSave(string bookmarkId) { }
    public void ScheduleAutoSyncBookmarkDeleteAfterLocalDelete(string bookmarkId, DateTime deletedAtUtc) { }
    public void ScheduleAutoSyncFolderAfterLocalSave(string folderId) { }
    public void ScheduleAutoSyncFolderDeleteAfterLocalDelete(string folderId, DateTime deletedAtUtc) { }
    public void ScheduleAutoSyncSidebarAppAfterLocalSave(string appId) { }
    public void ScheduleAutoSyncSidebarAppDeleteAfterLocalDelete(string appId, DateTime deletedAtUtc) { }

    public void ScheduleAutoSyncSettingsAfterLocalSave(string category)
    {
        EnsureCoordinatorWired();
        _coordinator.ScheduleAutoSyncSettingsAfterLocalSave(category);
    }

    public async Task StartWebRtcSettingsSyncAsync(string targetDeviceId, string category)
    {
        EnsureCoordinatorWired();
        await _coordinator.StartWebRtcSettingsSyncAsync(targetDeviceId, category);
    }

    public async Task<int> SyncSettingsCategoryToDevicesAsync(string category, IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return await _coordinator.SyncSettingsCategoryToDevicesAsync(category, targetDeviceIds);
    }

    public event Action? OnBookmarksChanged;
    public event Action? OnInstalledAppsChanged;

    public event Action<string, string>? OnSyncAckReceived;
    public event Action<string, string, string>? OnNoteSyncPayloadReceived;
    public event Action<string, string>? OnNoteSyncAckReceived;

    public Task OnIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default)
    {
        if (IsAiProxyPeerKey(peerId))
        {
            if (_hub is { State: HubConnectionState.Connected })
            {
                var targetDeviceId = GetDeviceIdFromPeerKey(peerId);
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    peerKey = peerId,
                    ice = System.Text.Json.JsonDocument.Parse(candidateJson).RootElement
                });
                return _hub.InvokeAsync("SendToDevice", targetDeviceId, "webrtc-ice", payload);
            }
            return Task.CompletedTask;
        }

        return _coordinator.OnIceCandidateAsync(peerId, candidateJson, ct);
    }

    public Task OnDataChannelOpenAsync(string peerId, CancellationToken ct = default)
    {
        if (IsAiProxyPeerKey(peerId))
        {
            if (!string.IsNullOrEmpty(AiServerDeviceId))
            {
                _aiProxyConnecting = false;
                IsAiProxyConnected = true;
                AiProxyError = null;
                OnChanged?.Invoke();
                _ = RequestRemoteModelsAsync();
            }
            return Task.CompletedTask;
        }

        return _coordinator.OnDataChannelOpenAsync(peerId, ct);
    }

    public void OnConnectionStateChange(string peerId, string state)
    {
        if (!IsAiProxyPeerKey(peerId))
            _coordinator.OnConnectionStateChange(peerId, state);
    }

    public Task OnDataReceivedAsync(string peerId, string data, CancellationToken ct = default)
    {
        if (IsAiProxyPeerKey(peerId))
            return HandleAiProxyDataReceived(peerId, data);
        return _coordinator.OnDataReceivedAsync(peerId, data, ct);
    }

    public void OnDataChannelClose(string peerId)
    {
        if (IsAiProxyPeerKey(peerId))
        {
            if (!string.IsNullOrEmpty(AiServerDeviceId))
            {
                IsAiProxyConnected = false;
                AiProxyError = "AI proxy connection closed.";
                OnChanged?.Invoke();
                _ = ScheduleAiProxyReconnectAsync();
            }
            return;
        }

        _coordinator.OnDataChannelClose(peerId);
    }

    private record DataChannelMessage(
        string type,
        string? convoId = null,
        string? content = null,
        string? noteId = null,
        int? chunkIndex = null,
        int? chunkCount = null,
        string? chunkData = null);

    // --- AI proxy (remote chat via peer browser) ---

    private static string GetAiProxyPeerKey(string deviceId) => $"ai:{deviceId}";

    private static bool IsAiProxyPeerKey(string peerKey) =>
        peerKey.StartsWith("ai:", StringComparison.Ordinal);

    private static string GetDeviceIdFromPeerKey(string peerKey) =>
        IsAiProxyPeerKey(peerKey) ? peerKey.Substring(3) : peerKey;

    public string? GetAiServerDeviceName()
    {
        if (string.IsNullOrEmpty(AiServerDeviceId)) return null;
        return Devices.FirstOrDefault(d => IsSelf(d.DeviceId) == false && string.Equals(d.DeviceId, AiServerDeviceId, StringComparison.OrdinalIgnoreCase))?.Name
               ?? Devices.FirstOrDefault(d => string.Equals(d.DeviceId, AiServerDeviceId, StringComparison.OrdinalIgnoreCase))?.Name
               ?? _aiServerName;
    }

    public async Task SetAiServerDeviceAsync(string? deviceId)
    {
        await InitializeAsync();

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            AiServerDeviceId = null;
            _aiServerName = null;
            await SetUserSettingAsync(AiServerDeviceIdKey, null);
            await SetUserSettingAsync(AiServerNameKey, null);
            await CloseAiProxyConnectionAsync();
            RemoteModels = Array.Empty<SyncModelInfo>();
            // Keep known devices on the list (offline) so they can be re-selected later.
            var live = Devices.Where(d => d.IsOnline).ToList();
            Devices = DeviceListMerger.Merge(live, _knownDevices);
            OnChanged?.Invoke();
            return;
        }

        if (IsSelf(deviceId))
            return;

        AiServerDeviceId = deviceId;
        var knownName = Devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? _knownDevices.FirstOrDefault(k =>
                string.Equals(k.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.Name;
        _aiServerName = knownName;
        await SetUserSettingAsync(AiServerDeviceIdKey, deviceId);
        await SetUserSettingAsync(AiServerNameKey, _aiServerName);
        _knownDevices = DeviceListMerger.Remember(_knownDevices, deviceId, _aiServerName);
        await SaveKnownDevicesAsync();
        Devices = DeviceListMerger.Merge(
            Devices,
            _knownDevices,
            AiServerDeviceId,
            _aiServerName);
        RemoteModels = Array.Empty<SyncModelInfo>();
        IsAiProxyConnected = false;
        AiProxyError = null;
        OnChanged?.Invoke();

        await EnsureAiProxyConnectionAsync();
    }

    private async Task ApplyDevicesFromServerAndNotifyAsync(
        IReadOnlyList<SyncDeviceInfo>? list,
        HashSet<string> prevOnline)
    {
        try
        {
            await ApplyDevicesFromServerAsync(list);

            if (!_devicesSnapshotInitialized)
            {
                _devicesSnapshotInitialized = true;
                OnChanged?.Invoke();
                return;
            }

            EnsureCoordinatorWired();
            _coordinator.OnDevicesUpdated(Devices, prevOnline);
            OnChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] DevicesUpdated handling failed: {ex.Message}");
        }
    }

    public async Task EnsureAiProxyConnectionAsync()
    {
        if (string.IsNullOrEmpty(AiServerDeviceId) || _hub?.State != HubConnectionState.Connected)
            return;

        if (IsAiProxyConnected || _aiProxyConnecting)
            return;

        _aiProxyConnecting = true;
        _aiProxyReconnectCts?.Cancel();

        try
        {
            var peerKey = GetAiProxyPeerKey(AiServerDeviceId);
            await _webrtc.CloseAsync(peerKey);

            await _webrtc.CreatePeerConnectionAsync(peerKey, this);
            await _webrtc.CreateDataChannelAsync(peerKey, AiProxyDataChannelLabel);

            var offerJson = await _webrtc.CreateOfferAsync(peerKey);
            await _hub.InvokeAsync("SendToDevice", AiServerDeviceId, "webrtc-offer-ai", offerJson);
            Console.WriteLine($"[WasmSyncService] Sent AI proxy offer to {AiServerDeviceId}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(25));
                    if (_aiProxyConnecting && !IsAiProxyConnected)
                    {
                        _aiProxyConnecting = false;
                        AiProxyError = "Timed out connecting to AI server device.";
                        OnChanged?.Invoke();
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            _aiProxyConnecting = false;
            AiProxyError = $"Failed to connect AI proxy: {ex.Message}";
            Console.WriteLine($"[WasmSyncService] AI proxy connect failed: {ex.Message}");
            OnChanged?.Invoke();
        }
    }

    private async Task CloseAiProxyConnectionAsync()
    {
        _aiProxyReconnectCts?.Cancel();
        _aiProxyReconnectCts = null;

        if (!string.IsNullOrEmpty(AiServerDeviceId))
        {
            try { await _webrtc.CloseAsync(GetAiProxyPeerKey(AiServerDeviceId)); } catch { }
        }

        IsAiProxyConnected = false;
    }

    private async Task ScheduleAiProxyReconnectAsync()
    {
        if (string.IsNullOrEmpty(AiServerDeviceId))
            return;

        _aiProxyReconnectCts?.Cancel();
        _aiProxyReconnectCts = new CancellationTokenSource();
        var token = _aiProxyReconnectCts.Token;

        try
        {
            await Task.Delay(3000, token);
            if (!token.IsCancellationRequested)
                await EnsureAiProxyConnectionAsync();
        }
        catch (TaskCanceledException) { }
    }

    private async Task HandleWebRtcOfferAi(string fromDeviceId, string offerJson)
    {
        if (_hub == null) return;

        try
        {
            var peerKey = GetAiProxyPeerKey(fromDeviceId);
            await _webrtc.CreatePeerConnectionAsync(peerKey, this);
            await _webrtc.SetRemoteDescriptionAsync(peerKey, offerJson);

            var answerJson = await _webrtc.CreateAnswerAsync(peerKey);
            await _hub.InvokeAsync("SendToDevice", fromDeviceId, "webrtc-answer-ai", answerJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Handle AI offer failed: {ex.Message}");
        }
    }

    private async Task HandleWebRtcAnswerAi(string fromDeviceId, string answerJson)
    {
        try
        {
            var peerKey = GetAiProxyPeerKey(fromDeviceId);
            await _webrtc.SetRemoteDescriptionAsync(peerKey, answerJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Handle AI answer failed: {ex.Message}");
        }
    }

    public async Task RequestRemoteModelsAsync()
    {
        if (!IsAiProxyConnected || string.IsNullOrEmpty(AiServerDeviceId))
            return;

        _modelsListTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerKey = GetAiProxyPeerKey(AiServerDeviceId);
        var msg = new DataChannelMessage("models-request");
        Console.WriteLine($"[AI Proxy] Requesting model list from {AiServerDeviceId}");
        await _webrtc.SendDataAsync( peerKey, System.Text.Json.JsonSerializer.Serialize(msg));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _modelsListTcs.Task.WaitAsync(cts.Token);
        }
        catch
        {
            Console.WriteLine("[AI Proxy] Timed out waiting for remote model list.");
            AiProxyError = "Timed out waiting for remote model list.";
            OnChanged?.Invoke();
        }
    }

    public async Task<(string Text, string ToolTrace)> SendChatRequestAsync(
        string modelId,
        List<ChatMessage> messages,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AiServerDeviceId))
            return ("", "");

        if (!IsAiProxyConnected)
        {
            await EnsureAiProxyConnectionAsync();
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(20));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!IsAiProxyConnected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, waitCts.Token);
            }
        }

        if (!IsAiProxyConnected)
            return ($"Not connected to AI server device. {AiProxyError ?? "Try again from the Sync page."}", "");

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ChatResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingChatRequests[requestId] = tcs;

        try
        {
            var payload = new ChatRequestPayload(requestId, modelId, messages);
            var json = System.Text.Json.JsonSerializer.Serialize(new DataChannelMessage("chat-request", content: System.Text.Json.JsonSerializer.Serialize(payload)));
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
            if (byteCount > 2_000_000)
                Console.WriteLine($"[WasmSyncService] Large chat-request payload: {byteCount} bytes");

            var peerKey = GetAiProxyPeerKey(AiServerDeviceId);
            Console.WriteLine($"[AI Proxy] â†’ chat-request model={modelId} messages={messages.Count} id={requestId[..8]}");
            await _webrtc.SendDataAsync( peerKey, json);

            using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            responseCts.CancelAfter(TimeSpan.FromMinutes(10));
            var response = await tcs.Task.WaitAsync(responseCts.Token);

            if (!string.IsNullOrEmpty(response.Error))
            {
                Console.WriteLine($"[AI Proxy] â† chat-response id={requestId[..8]} error: {response.Error}");
                return ($"Remote AI error: {response.Error}", response.ToolTrace ?? "");
            }

            var replyLen = response.Text?.Length ?? 0;
            Console.WriteLine($"[AI Proxy] â† chat-response id={requestId[..8]} ok ({replyLen} chars)");
            return (response.Text ?? "No response.", response.ToolTrace ?? "");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI Proxy] â† chat-request id={requestId[..8]} failed: {ex.Message}");
            return ($"Remote AI request failed: {ex.Message}", "");
        }
        finally
        {
            _pendingChatRequests.Remove(requestId);
        }
    }

    private async Task HandleAiProxyDataReceived(string peerKey, string data)
    {
        var msg = System.Text.Json.JsonSerializer.Deserialize<DataChannelMessage>(data);
        if (msg == null) return;

        if (msg.type == "models-request")
        {
            await _keyStore.LoadAsync();
            await _modelCatalog.RefreshAsync();
            var models = _modelCatalog.GetAvailableModels().Select(ToSyncModel).ToList();
            Console.WriteLine($"[AI Proxy] Serving model list ({models.Count} models) to {GetDeviceIdFromPeerKey(peerKey)}");
            var listMsg = new DataChannelMessage("models-list", content: System.Text.Json.JsonSerializer.Serialize(models));
            await _webrtc.SendDataAsync( peerKey, System.Text.Json.JsonSerializer.Serialize(listMsg));
            return;
        }

        if (msg.type == "models-list" && msg.content != null)
        {
            var models = System.Text.Json.JsonSerializer.Deserialize<List<SyncModelInfo>>(msg.content)
                         ?? new List<SyncModelInfo>();
            var serverName = GetAiServerDeviceName() ?? "remote device";
            RemoteModels = models
                .Select(m => m with { Label = $"{m.Label} (via {serverName})" })
                .ToList();
            Console.WriteLine($"[AI Proxy] Received model list ({RemoteModels.Count} models via {serverName})");
            _modelsListTcs?.TrySetResult(true);
            OnChanged?.Invoke();
            return;
        }

        if (msg.type == "chat-request" && msg.content != null)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ChatRequestPayload>(msg.content);
            if (request == null) return;

            var reqIdShort = request.RequestId.Length >= 8 ? request.RequestId[..8] : request.RequestId;
            Console.WriteLine($"[AI Proxy] â† chat-request from {GetDeviceIdFromPeerKey(peerKey)} model={request.ModelId} messages={request.Messages.Count} id={reqIdShort}");
            var started = DateTime.UtcNow;

            var result = await _chatCompletion.CompleteAsync(
                request.ModelId,
                request.Messages,
                currentUser: _auth.Email,
                ct: default);

            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            var response = new ChatResponsePayload(
                request.RequestId,
                result.Text,
                result.ToolTrace,
                result.Error);

            if (!string.IsNullOrEmpty(result.Error))
                Console.WriteLine($"[AI Proxy] â†’ chat-response id={reqIdShort} error ({elapsed:F1}s): {result.Error}");
            else
                Console.WriteLine($"[AI Proxy] â†’ chat-response id={reqIdShort} ok ({result.Text?.Length ?? 0} chars, {elapsed:F1}s)");

            var responseMsg = new DataChannelMessage("chat-response", content: System.Text.Json.JsonSerializer.Serialize(response));
            await _webrtc.SendDataAsync( peerKey, System.Text.Json.JsonSerializer.Serialize(responseMsg));
            return;
        }

        if (msg.type == "chat-response" && msg.content != null)
        {
            var response = System.Text.Json.JsonSerializer.Deserialize<ChatResponsePayload>(msg.content);
            if (response?.RequestId != null && _pendingChatRequests.TryGetValue(response.RequestId, out var tcs))
                tcs.TrySetResult(response);
        }
    }

    private record ChatRequestPayload(string RequestId, string ModelId, List<ChatMessage> Messages);
    private record ChatResponsePayload(string RequestId, string? Text, string? ToolTrace, string? Error);

    /// <summary>
    /// Returns true if the given deviceId matches the one belonging to this browser instance.
    /// </summary>
    public bool IsSelf(string? deviceId) =>
        !string.IsNullOrEmpty(deviceId) &&
        string.Equals(deviceId, MyDeviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns only the devices that are *not* this one (for "sync to these peers" checkboxes).
    /// </summary>
    public IEnumerable<SyncDeviceInfo> GetOtherDevices() =>
        Devices.Where(d => !IsSelf(d.DeviceId));

    public IReadOnlyCollection<string> GetEffectiveSyncTargetDeviceIds()
    {
        if (SyncToAllDevices)
            return GetOtherDevices().Select(d => d.DeviceId).ToList();
        return _syncTargetDeviceIds.ToList();
    }

    public async ValueTask DisposeAsync()
    {
        _auth.OnChanged -= OnAuthChanged;

        await _coordinator.DisposeAsync();
        if (_hub != null)
        {
            try { await _hub.DisposeAsync(); } catch { }
        }
    }

    private static SyncModelInfo ToSyncModel(ChatModelInfo model) =>
        new(
            model.Id,
            model.Label,
            model.Icon,
            model.ProviderId,
            model.ProviderName,
            model.SupportsTools,
            model.SupportsVision,
            model.IsOllamaBackend,
            model.ContextSize,
            model.VisionProxyModelId);
}
