using System.Net;
using App.Core.Auth;
using App.Core.Browser;
using App.Core.Chat;
using App.Core.Configuration;
using App.Core.Storage;
using App.Core.Sync;
using App.Shared.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace App.Maui.Services;

/// <summary>
/// MAUI sync: SignalR presence + WebRTC data transfer via SIPSorcery and shared coordinator.
/// </summary>
public sealed class MauiSyncService : ISyncService, IWebRtcTransportCallbacks
{
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
    private const string AutoSyncBookmarksKey = "app-auto-sync-bookmarks";
    private const string AutoSyncAppsKey = "app-auto-sync-apps";
    private const string SyncToAllDevicesKey = "app-sync-to-all-devices";
    private const string AutoSyncLocalAiKey = "app-auto-sync-local-ai";
    private const string AutoSyncLemonadeKey = "app-auto-sync-lemonade";
    private const string AutoSyncCloudProvidersKey = "app-auto-sync-cloud-providers";
    private const string AutoSyncHomeAssistantKey = "app-auto-sync-home-assistant";
    private const string AutoSyncToolsKey = "app-auto-sync-tools";
    private const string AutoSyncSystemPromptKey = "app-auto-sync-system-prompt";
    private const string AutoSyncProfileKey = "app-auto-sync-profile";
    private const string AutoSyncMemoriesKey = "app-auto-sync-memories";
    private const string AutoSyncAppearanceKey = "app-auto-sync-appearance";
    private const string AutoSyncSkillsKey = "app-auto-sync-skills";
    private const string AutoSyncWorkflowsKey = "app-auto-sync-workflows";

    private readonly MauiAuthCookieStore _cookieStore;
    private readonly SqliteSettingsDatabase _settings;
    private readonly IAuthService _auth;
    private readonly IAppServerEndpoint _serverEndpoint;
    private readonly IWebRtcTransport _webrtc;
    private readonly ISyncPreferencesStore _prefs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKeyStore _keyStore;
    private readonly IBrowserStore _browserStore;
    private readonly IBrowserSidebarStore _sidebarStore;
    private readonly ISettingsSyncStore _settingsSync;
    private readonly ChatModelCatalogService _modelCatalog;
    private readonly ChatCompletionService _chatCompletion;
    private readonly AiProxyRelay _aiProxy;

    private IServiceScope? _syncScope;
    private WebRtcSyncCoordinator? _coordinator;
    private readonly HashSet<string> _syncTargetDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _devicesSnapshotInitialized;
    private List<KnownDeviceRecord> _knownDevices = new();
    private string? _aiServerName;

    private HubConnection? _hub;
    private bool _initialized;
    private int _lastPublishedModelCount = -1;

    public string? MyDeviceId { get; private set; }
    public string MyDeviceName { get; private set; } = "This device";
    public IReadOnlyList<SyncDeviceInfo> Devices { get; private set; } = Array.Empty<SyncDeviceInfo>();
    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    public string? AiServerDeviceId => _aiProxy.AiServerDeviceId;
    public IReadOnlyList<SyncModelInfo> RemoteModels => _aiProxy.RemoteModels;
    public bool IsAiProxyConnected => _aiProxy.IsConnected;
    public string? AiProxyError => _aiProxy.Error;

    public bool SyncToAllDevices { get; private set; } = true;
    public bool AutoSyncChatHistory { get; private set; } = true;
    public bool AutoSyncNotes { get; private set; } = true;
    public bool AutoSyncGallery { get; private set; } = true;
    public bool AutoSyncCalendar { get; private set; } = true;
    public bool AutoSyncBookmarks { get; private set; } = true;
    public bool AutoSyncInstalledApps { get; private set; } = true;
    public bool AutoSyncLocalAi { get; private set; } = true;
    public bool AutoSyncLemonade { get; private set; } = true;
    public bool AutoSyncCloudProviders { get; private set; } = true;
    public bool AutoSyncHomeAssistant { get; private set; } = true;
    public bool AutoSyncTools { get; private set; } = true;
    public bool AutoSyncSystemPrompt { get; private set; } = true;
    public bool AutoSyncProfile { get; private set; } = true;
    public bool AutoSyncMemories { get; private set; } = true;
    public bool AutoSyncAppearance { get; private set; } = true;
    public bool AutoSyncSkills { get; private set; } = true;
    public bool AutoSyncWorkflows { get; private set; } = true;
    public IReadOnlyCollection<string> SyncTargetDeviceIds => _syncTargetDeviceIds;

    public event Action? OnChanged;
    public event Action? OnConversationsChanged;
    public event Action? OnNotesChanged;
    public event Action? OnGalleryChanged;
    public event Action? OnCalendarsChanged;
    public event Action? OnBookmarksChanged;
    public event Action? OnInstalledAppsChanged;
    public event Action? OnSettingsChanged;
    public event Action<string, string, string>? OnSyncPayloadReceived;
    public event Action<string, string>? OnSyncAckReceived;
    public event Action<string, string, string>? OnNoteSyncPayloadReceived;
    public event Action<string, string>? OnNoteSyncAckReceived;
    public event Action<string, string, string>? OnAlbumSyncPayloadReceived;
    public event Action<string, string>? OnAlbumSyncAckReceived;

    public MauiSyncService(
        MauiAuthCookieStore cookieStore,
        SqliteSettingsDatabase settings,
        IAuthService auth,
        IAppServerEndpoint serverEndpoint,
        IWebRtcTransport webrtc,
        ISyncPreferencesStore prefs,
        IServiceScopeFactory scopeFactory,
        IKeyStore keyStore,
        IBrowserStore browserStore,
        IBrowserSidebarStore sidebarStore,
        ISettingsSyncStore settingsSync,
        ChatModelCatalogService modelCatalog,
        ChatCompletionService chatCompletion)
    {
        _cookieStore = cookieStore;
        _settings = settings;
        _auth = auth;
        _serverEndpoint = serverEndpoint;
        _webrtc = webrtc;
        _prefs = prefs;
        _scopeFactory = scopeFactory;
        _keyStore = keyStore;
        _browserStore = browserStore;
        _sidebarStore = sidebarStore;
        _settingsSync = settingsSync;
        _modelCatalog = modelCatalog;
        _chatCompletion = chatCompletion;

        _aiProxy = new AiProxyRelay(
            _webrtc,
            _keyStore,
            _modelCatalog,
            _chatCompletion,
            _auth,
            async (target, type, payload) =>
            {
                if (_hub?.State == HubConnectionState.Connected)
                    await _hub.InvokeAsync("SendToDevice", target, type, payload);
            },
            () => _hub?.State == HubConnectionState.Connected,
            GetAiServerDeviceName,
            () => OnChanged?.Invoke());

        _auth.OnChanged += OnAuthChanged;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        MyDeviceId = await _settings.GetStringAsync(DeviceIdKey);
        if (string.IsNullOrWhiteSpace(MyDeviceId))
        {
            MyDeviceId = Guid.NewGuid().ToString("N");
            await _settings.SetStringAsync(DeviceIdKey, MyDeviceId);
        }

        var savedName = await _settings.GetStringAsync(DeviceNameKey);
        MyDeviceName = !string.IsNullOrWhiteSpace(savedName)
            ? savedName
            : DeriveDefaultDeviceName();
        if (string.IsNullOrWhiteSpace(savedName))
            await _settings.SetStringAsync(DeviceNameKey, MyDeviceName);

        var savedAiServer = await GetUserSettingAsync(AiServerDeviceIdKey);
        _aiProxy.AiServerDeviceId = string.IsNullOrWhiteSpace(savedAiServer) ? null : savedAiServer;
        _aiServerName = await GetUserSettingAsync(AiServerNameKey);

        await LoadSyncPreferencesAsync();
        await LoadKnownDevicesAsync();
        Devices = DeviceListMerger.Merge(
            Array.Empty<SyncDeviceInfo>(),
            _knownDevices,
            _aiProxy.AiServerDeviceId,
            _aiServerName);

        // File log when sync debug is enabled (toggle on Sync page).
        SyncDebugLog.LogFilePath = Path.Combine(MauiAppData.Directory, "sync-debug.log");
        SyncDebugLog.Hub($"MauiSync Initialize deviceId={MyDeviceId} name={MyDeviceName} dataDir={MauiAppData.Directory}");

        EnsureCoordinator();
        EnsureCoordinatorWired();

        try
        {
            await _keyStore.LoadAsync();
            await _browserStore.LoadAsync();
            await _sidebarStore.LoadAsync();
            SyncDebugLog.Browser(
                $"Local browser store loaded: {_browserStore.GetBookmarks().Count} bookmarks, " +
                $"{_browserStore.GetFolders().Count} folders, " +
                $"{_sidebarStore.GetPinnedApps().Count(a => !a.IsBuiltIn)} pinned apps");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Error($"Browser store load failed: {ex.Message}");
        }

        OnChanged?.Invoke();
    }

    public async Task EnsureConnectedAndRegisteredAsync()
    {
        await InitializeAsync();

        if (!_auth.IsAuthenticated || string.IsNullOrEmpty(_auth.Email))
        {
            Console.WriteLine("[MauiSyncService] Skipping hub connect — not authenticated.");
            return;
        }

        await _cookieStore.EnsureLoadedAsync();

        if (_hub is null)
        {
            var hubUrl = $"{_serverEndpoint.SyncHubUrl}?deviceId={Uri.EscapeDataString(MyDeviceId ?? "")}";
            var hubUri = new Uri(_serverEndpoint.SyncHubUrl);
            var cookies = _cookieStore.Container;

            _hub = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    options.Transports = HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ =>
                    {
                        return new HttpClientHandler
                        {
                            CookieContainer = cookies,
                            UseCookies = true,
                            AutomaticDecompression = DecompressionMethods.All
                        };
                    };
                })
                .WithAutomaticReconnect()
                .Build();

            var cookieCount = cookies.GetCookies(hubUri).Count;
            Console.WriteLine($"[MauiSyncService] Hub cookies for {_serverEndpoint.SyncHubUrl}: {cookieCount}");
            if (cookieCount == 0)
                Console.WriteLine("[MauiSyncService] WARNING: No auth cookies — hub connection will likely fail.");

            _hub.On<IReadOnlyList<SyncDeviceInfo>>("DevicesUpdated", list =>
            {
                var prevOnline = Devices
                    .Where(d => d.IsOnline)
                    .Select(d => d.DeviceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                _ = ApplyDevicesFromServerAndNotifyAsync(list, prevOnline);
            });

            WireSyncHandlers();

            _hub.Closed += _ =>
            {
                NotifyConnectionChanged();
                return Task.CompletedTask;
            };

            _hub.Reconnecting += _ =>
            {
                NotifyConnectionChanged();
                return Task.CompletedTask;
            };

            _hub.Reconnected += async _ =>
            {
                await SafeRegisterAsync();
                NotifyConnectionChanged();
            };
        }

        if (_hub.State == HubConnectionState.Disconnected)
        {
            try
            {
                await _hub.StartAsync();
                NotifyConnectionChanged();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MauiSyncService] Hub connect failed: {ex.Message}");
                NotifyConnectionChanged();
                return;
            }
        }

        await SafeRegisterAsync(retry: true);
        await PublishAiCapabilitiesAsync();

        if (!string.IsNullOrEmpty(_aiProxy.AiServerDeviceId))
            await _aiProxy.EnsureConnectionAsync();
    }

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
            await InvokeHubAsync("UpdateAiCapabilities", MyDeviceId, count);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiSyncService] UpdateAiCapabilities failed: {ex.Message}");
        }
    }

    public async Task RefreshAsync()
    {
        if (_hub?.State == HubConnectionState.Connected && !string.IsNullOrEmpty(MyDeviceId))
        {
            await SafeRegisterAsync();
            await PublishAiCapabilitiesAsync();
        }
        else if (_auth.IsAuthenticated)
            await EnsureConnectedAndRegisteredAsync();
    }

    public async Task SetDeviceNameAsync(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;

        MyDeviceName = newName.Trim();
        await _settings.SetStringAsync(DeviceNameKey, MyDeviceName);

        if (_hub?.State == HubConnectionState.Connected && !string.IsNullOrEmpty(MyDeviceId))
            await InvokeHubAsync("UpdateDeviceName", MyDeviceId, MyDeviceName);

        OnChanged?.Invoke();
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

    public async Task SetAutoSyncCalendarAsync(bool enabled)
    {
        AutoSyncCalendar = enabled;
        await SetUserSettingAsync(AutoSyncCalendarKey, enabled ? "true" : "false");
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

    public async Task SetAutoSyncBookmarksAsync(bool enabled)
    {
        AutoSyncBookmarks = enabled;
        await SetUserSettingAsync(AutoSyncBookmarksKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public async Task SetAutoSyncInstalledAppsAsync(bool enabled)
    {
        AutoSyncInstalledApps = enabled;
        await SetUserSettingAsync(AutoSyncAppsKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

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
    public Task SetAutoSyncWorkflowsAsync(bool enabled) =>
        SetAutoSyncFlagAsync(AutoSyncWorkflowsKey, v => AutoSyncWorkflows = v, enabled);

    public Task SendSyncPayloadAsync(string targetDeviceId, string convoId, List<ChatMessage> messages) =>
        Task.CompletedTask;

    public Task StartWebRtcSyncAsync(string targetDeviceId, string convoId, List<ChatMessage> messages)
    {
        EnsureCoordinatorWired();
        return Coordinator.StartWebRtcSyncAsync(targetDeviceId, convoId, messages);
    }

    public Task StartWebRtcNoteSyncAsync(string targetDeviceId, string noteId, string title, List<ChatMessage> entries)
    {
        EnsureCoordinatorWired();
        return Coordinator.StartWebRtcNoteSyncAsync(targetDeviceId, noteId, title, entries);
    }

    public Task StartWebRtcAlbumSyncAsync(string targetDeviceId, string albumId, string title)
    {
        EnsureCoordinatorWired();
        return Coordinator.StartWebRtcAlbumSyncAsync(targetDeviceId, albumId, title);
    }

    public Task StartWebRtcAlbumImageSyncAsync(string targetDeviceId, string albumId, string imageId)
    {
        EnsureCoordinatorWired();
        return Coordinator.StartWebRtcAlbumImageSyncAsync(targetDeviceId, albumId, imageId);
    }

    public Task StartWebRtcBookmarkSyncAsync(string targetDeviceId, BrowserBookmark bookmark)
    {
        EnsureCoordinatorWired();
        return Coordinator.StartWebRtcBookmarkSyncAsync(targetDeviceId, bookmark);
    }

    public Task StartWebRtcFolderSyncAsync(string targetDeviceId, BrowserBookmarkFolder folder)
    {
        EnsureCoordinatorWired();
        return Coordinator.StartWebRtcFolderSyncAsync(targetDeviceId, folder);
    }

    public Task StartWebRtcSidebarAppSyncAsync(string targetDeviceId, SidebarApp app)
    {
        EnsureCoordinatorWired();
        return Coordinator.StartWebRtcSidebarAppSyncAsync(targetDeviceId, app);
    }

    public Task<int> SyncAllConversationsToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return Coordinator.SyncAllConversationsToDevicesAsync(targetDeviceIds);
    }

    public Task<int> SyncAllNotesToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return Coordinator.SyncAllNotesToDevicesAsync(targetDeviceIds);
    }

    public Task<int> SyncAllAlbumsToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return Coordinator.SyncAllAlbumsToDevicesAsync(targetDeviceIds);
    }

    public Task<int> SyncAllBookmarksToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return Coordinator.SyncAllBookmarksToDevicesAsync(targetDeviceIds);
    }

    public Task<int> SyncAllInstalledAppsToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return Coordinator.SyncAllInstalledAppsToDevicesAsync(targetDeviceIds);
    }

    public void ScheduleAutoSyncConvoAfterLocalSave(string convoId, string? title = null)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncConvoAfterLocalSave(convoId, title);
    }

    public void ScheduleAutoSyncConvoDeleteAfterLocalDelete(string convoId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncConvoDeleteAfterLocalDelete(convoId, deletedAtUtc);
    }

    public void ScheduleAutoSyncNoteAfterLocalSave(string noteId, string title)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncNoteAfterLocalSave(noteId, title);
    }

    public void ScheduleAutoSyncNoteDeleteAfterLocalDelete(string noteId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncNoteDeleteAfterLocalDelete(noteId, deletedAtUtc);
    }

    public void ScheduleAutoSyncAlbumMetaAfterLocalSave(string albumId, string title)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncAlbumMetaAfterLocalSave(albumId, title);
    }

    public void ScheduleAutoSyncAlbumDeleteAfterLocalDelete(string albumId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncAlbumDeleteAfterLocalDelete(albumId, deletedAtUtc);
    }

    public void ScheduleAutoSyncAlbumImageAfterLocalSave(string albumId, string imageId)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncAlbumImageAfterLocalSave(albumId, imageId);
    }

    public void ScheduleAutoSyncAlbumImageDeleteAfterLocalDelete(string albumId, string imageId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncAlbumImageDeleteAfterLocalDelete(albumId, imageId, deletedAtUtc);
    }

    // Calendar sync (full WebRTC protocol in a later phase — stubs keep ICalendarSyncBridge satisfied).
    public void ScheduleAutoSyncCalendarAfterLocalSave(string calendarId)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncCalendarAfterLocalSave(calendarId);
    }

    public void ScheduleAutoSyncCalendarDeleteAfterLocalDelete(string calendarId, DateTime deletedAt)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncCalendarDeleteAfterLocalDelete(calendarId, deletedAt);
    }

    public void ScheduleAutoSyncEventAfterLocalSave(string calendarId, string eventId)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncEventAfterLocalSave(calendarId, eventId);
    }

    public void ScheduleAutoSyncEventDeleteAfterLocalDelete(string calendarId, string eventId, DateTime deletedAt)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncEventDeleteAfterLocalDelete(calendarId, eventId, deletedAt);
    }

    public Task StartWebRtcCalendarSyncAsync(string targetDeviceId, string calendarId)
    {
        EnsureCoordinatorWired();
        return Coordinator.StartWebRtcCalendarSyncAsync(targetDeviceId, calendarId);
    }

    public Task StartWebRtcCalendarEventSyncAsync(string targetDeviceId, string calendarId, string eventId)
    {
        EnsureCoordinatorWired();
        return Coordinator.StartWebRtcCalendarEventSyncAsync(targetDeviceId, calendarId, eventId);
    }

    public Task<int> SyncAllCalendarsToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return Coordinator.SyncAllCalendarsToDevicesAsync(targetDeviceIds);
    }

    public void ScheduleAutoSyncBookmarkAfterLocalSave(string bookmarkId)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncBookmarkAfterLocalSave(bookmarkId);
    }

    public void ScheduleAutoSyncBookmarkDeleteAfterLocalDelete(string bookmarkId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncBookmarkDeleteAfterLocalDelete(bookmarkId, deletedAtUtc);
    }

    public void ScheduleAutoSyncFolderAfterLocalSave(string folderId)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncFolderAfterLocalSave(folderId);
    }

    public void ScheduleAutoSyncFolderDeleteAfterLocalDelete(string folderId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncFolderDeleteAfterLocalDelete(folderId, deletedAtUtc);
    }

    public void ScheduleAutoSyncSidebarAppAfterLocalSave(string appId)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncSidebarAppAfterLocalSave(appId);
    }

    public void ScheduleAutoSyncSidebarAppDeleteAfterLocalDelete(string appId, DateTime deletedAtUtc)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncSidebarAppDeleteAfterLocalDelete(appId, deletedAtUtc);
    }

    public void ScheduleAutoSyncSettingsAfterLocalSave(string category)
    {
        EnsureCoordinatorWired();
        Coordinator.ScheduleAutoSyncSettingsAfterLocalSave(category);
    }

    public async Task StartWebRtcSettingsSyncAsync(string targetDeviceId, string category)
    {
        EnsureCoordinatorWired();
        await Coordinator.StartWebRtcSettingsSyncAsync(targetDeviceId, category);
    }

    public async Task<int> SyncSettingsCategoryToDevicesAsync(string category, IEnumerable<string> targetDeviceIds)
    {
        EnsureCoordinatorWired();
        return await Coordinator.SyncSettingsCategoryToDevicesAsync(category, targetDeviceIds);
    }

    public string? GetAiServerDeviceName()
    {
        if (string.IsNullOrEmpty(_aiProxy.AiServerDeviceId)) return null;
        return Devices.FirstOrDefault(d => string.Equals(d.DeviceId, _aiProxy.AiServerDeviceId, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    public async Task SetAiServerDeviceAsync(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _aiServerName = null;
            await SetUserSettingAsync(AiServerNameKey, null);
            await _aiProxy.SetAiServerDeviceAsync(
                null,
                () => SetUserSettingAsync(AiServerDeviceIdKey, null),
                id => SetUserSettingAsync(AiServerDeviceIdKey, id),
                IsSelf);
            var live = Devices.Where(d => d.IsOnline).ToList();
            Devices = DeviceListMerger.Merge(live, _knownDevices);
            OnChanged?.Invoke();
            return;
        }

        if (IsSelf(deviceId))
            return;

        var knownName = Devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? _knownDevices.FirstOrDefault(k =>
                string.Equals(k.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.Name;
        _aiServerName = knownName;
        await SetUserSettingAsync(AiServerNameKey, _aiServerName);
        _knownDevices = DeviceListMerger.Remember(_knownDevices, deviceId, _aiServerName);
        await SaveKnownDevicesAsync();

        await _aiProxy.SetAiServerDeviceAsync(
            deviceId,
            () => SetUserSettingAsync(AiServerDeviceIdKey, null),
            id => SetUserSettingAsync(AiServerDeviceIdKey, id),
            IsSelf);

        Devices = DeviceListMerger.Merge(Devices, _knownDevices, AiServerDeviceId, _aiServerName);
        OnChanged?.Invoke();
    }

    public Task EnsureAiProxyConnectionAsync() => _aiProxy.EnsureConnectionAsync();

    public Task RequestRemoteModelsAsync() => _aiProxy.RequestRemoteModelsAsync();

    public Task<(string Text, string ToolTrace)> SendChatRequestAsync(
        string modelId,
        List<ChatMessage> messages,
        CancellationToken ct = default) =>
        _aiProxy.SendChatRequestAsync(modelId, messages, ct);

    public bool IsSelf(string? deviceId) =>
        !string.IsNullOrEmpty(deviceId) &&
        string.Equals(deviceId, MyDeviceId, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<SyncDeviceInfo> GetOtherDevices() =>
        Devices.Where(d => !IsSelf(d.DeviceId));

    public IReadOnlyCollection<string> GetEffectiveSyncTargetDeviceIds()
    {
        if (SyncToAllDevices)
            return GetOtherDevices().Select(d => d.DeviceId).ToList();
        return _syncTargetDeviceIds.ToList();
    }

    public Task OnIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default)
    {
        if (AiProxyRelay.IsAiPeer(peerId))
            return _aiProxy.OnIceCandidateAsync(peerId, candidateJson, ct);
        return Coordinator.OnIceCandidateAsync(peerId, candidateJson, ct);
    }

    public Task OnDataChannelOpenAsync(string peerId, CancellationToken ct = default)
    {
        if (AiProxyRelay.IsAiPeer(peerId))
            return _aiProxy.OnDataChannelOpenAsync(peerId, ct);
        return Coordinator.OnDataChannelOpenAsync(peerId, ct);
    }

    public void OnConnectionStateChange(string peerId, string state)
    {
        if (!AiProxyRelay.IsAiPeer(peerId))
            Coordinator.OnConnectionStateChange(peerId, state);
    }

    public Task OnDataReceivedAsync(string peerId, string data, CancellationToken ct = default)
    {
        if (AiProxyRelay.IsAiPeer(peerId))
            return _aiProxy.OnDataReceivedAsync(peerId, data, ct);
        return Coordinator.OnDataReceivedAsync(peerId, data, ct);
    }

    public void OnDataChannelClose(string peerId)
    {
        if (AiProxyRelay.IsAiPeer(peerId))
            _aiProxy.OnDataChannelClose(peerId);
        else
            Coordinator.OnDataChannelClose(peerId);
    }

    public async ValueTask DisposeAsync()
    {
        _auth.OnChanged -= OnAuthChanged;
        await _aiProxy.CloseConnectionAsync();

        if (_coordinator is not null)
        {
            try { await _coordinator.DisposeAsync(); } catch { }
            _coordinator = null;
        }

        _syncScope?.Dispose();
        _syncScope = null;

        if (_hub is not null)
        {
            try { await _hub.StopAsync(); } catch { }
            try { await _hub.DisposeAsync(); } catch { }
            _hub = null;
        }
    }

    private WebRtcSyncCoordinator Coordinator =>
        _coordinator ?? throw new InvalidOperationException("Sync coordinator not initialized.");

    private void EnsureCoordinator()
    {
        if (_coordinator is not null)
            return;

        _syncScope?.Dispose();
        _syncScope = _scopeFactory.CreateScope();
        var sp = _syncScope.ServiceProvider;

        _coordinator = new WebRtcSyncCoordinator(
            _webrtc,
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<INoteStore>(),
            _prefs,
            async (target, type, payload) =>
            {
                if (_hub?.State == HubConnectionState.Connected)
                    await _hub.InvokeAsync("SendToDevice", target, type, payload);
            },
            () => _hub?.State == HubConnectionState.Connected,
            transportCallbacks: this,
            browserStore: _browserStore,
            sidebarStore: _sidebarStore,
            settingsStore: _settingsSync,
            galleryStore: sp.GetRequiredService<IGalleryStore>(),
            storageQuota: sp.GetService<IStorageQuotaService>(),
            calendarStore: sp.GetRequiredService<ICalendarStore>());

        _coordinator.OnConversationsChanged += () => OnConversationsChanged?.Invoke();
        _coordinator.OnNotesChanged += () => OnNotesChanged?.Invoke();
        _coordinator.OnGalleryChanged += () => OnGalleryChanged?.Invoke();
        _coordinator.OnCalendarsChanged += () => OnCalendarsChanged?.Invoke();
        _coordinator.OnBookmarksChanged += () => OnBookmarksChanged?.Invoke();
        _coordinator.OnInstalledAppsChanged += () => OnInstalledAppsChanged?.Invoke();
        _coordinator.OnSettingsChanged += () => OnSettingsChanged?.Invoke();
        _coordinator.OnSyncPayloadReceived += (c, j, f) => OnSyncPayloadReceived?.Invoke(c, j, f);
        _coordinator.OnSyncAckReceived += (c, f) => OnSyncAckReceived?.Invoke(c, f);
        _coordinator.OnNoteSyncPayloadReceived += (n, j, f) => OnNoteSyncPayloadReceived?.Invoke(n, j, f);
        _coordinator.OnNoteSyncAckReceived += (n, f) => OnNoteSyncAckReceived?.Invoke(n, f);
        _coordinator.OnAlbumSyncPayloadReceived += (a, j, f) => OnAlbumSyncPayloadReceived?.Invoke(a, j, f);
        _coordinator.OnAlbumSyncAckReceived += (a, f) => OnAlbumSyncAckReceived?.Invoke(a, f);
    }

    private void EnsureCoordinatorWired()
    {
        EnsureCoordinator();
        _coordinator!.AutoSyncChatHistory = AutoSyncChatHistory;
        _coordinator.AutoSyncNotes = AutoSyncNotes;
        _coordinator.AutoSyncGallery = AutoSyncGallery;
        _coordinator.AutoSyncCalendar = AutoSyncCalendar;
        _coordinator.AutoSyncBookmarks = AutoSyncBookmarks;
        _coordinator.AutoSyncInstalledApps = AutoSyncInstalledApps;
        _coordinator.AutoSyncLocalAi = AutoSyncLocalAi;
        _coordinator.AutoSyncLemonade = AutoSyncLemonade;
        _coordinator.AutoSyncCloudProviders = AutoSyncCloudProviders;
        _coordinator.AutoSyncHomeAssistant = AutoSyncHomeAssistant;
        _coordinator.AutoSyncTools = AutoSyncTools;
        _coordinator.AutoSyncSystemPrompt = AutoSyncSystemPrompt;
        _coordinator.AutoSyncProfile = AutoSyncProfile;
        _coordinator.AutoSyncMemories = AutoSyncMemories;
        _coordinator.AutoSyncAppearance = AutoSyncAppearance;
        _coordinator.AutoSyncSkills = AutoSyncSkills;
        _coordinator.AutoSyncWorkflows = AutoSyncWorkflows;
        _coordinator.SyncTargetDeviceIds = GetEffectiveSyncTargetDeviceIds();
        _coordinator.IsSelf = IsSelf;
        _coordinator.LocalDeviceId = MyDeviceId;
        _coordinator.IsAuthenticated = () => _auth.IsAuthenticated;
        _coordinator.EnsureConnectedAsync = EnsureConnectedAndRegisteredAsync;
        _coordinator.GetDevices = () => Devices;
    }

    private void WireSyncHandlers()
    {
        if (_hub is null) return;

        _hub.On<string, string, string>("SyncPayloadReceived", async (convoId, json, fromDeviceId) =>
        {
            EnsureCoordinatorWired();
            await Coordinator.HandleIncomingSyncPayloadAsync(convoId, json, fromDeviceId);
        });

        _hub.On<string, string, string>("ReceiveSignaling", async (fromDeviceId, type, payload) =>
        {
            if (await _aiProxy.TryHandleSignalingAsync(fromDeviceId, type, payload))
                return;

            EnsureCoordinatorWired();
            await Coordinator.HandleReceiveSignalingAsync(fromDeviceId, type, payload);
        });
    }

    private async void OnAuthChanged()
    {
        try
        {
            // Reload per-user sync prefs and AI server selection after login/logout.
            var savedAiServer = await GetUserSettingAsync(AiServerDeviceIdKey);
            _aiProxy.AiServerDeviceId = string.IsNullOrWhiteSpace(savedAiServer) ? null : savedAiServer;
            _aiServerName = await GetUserSettingAsync(AiServerNameKey);
            await LoadSyncPreferencesAsync();
            await LoadKnownDevicesAsync();
            Devices = DeviceListMerger.Merge(
                Array.Empty<SyncDeviceInfo>(),
                _knownDevices,
                _aiProxy.AiServerDeviceId,
                _aiServerName);
            await _keyStore.LoadAsync();
            await _browserStore.LoadAsync();
            await _sidebarStore.LoadAsync();
            OnBookmarksChanged?.Invoke();
            OnInstalledAppsChanged?.Invoke();
            OnChanged?.Invoke();
        }
        catch (Exception ex)
        {
            SyncDebugLog.Error($"Auth-changed store reload failed: {ex.Message}");
        }

        if (_auth.IsAuthenticated)
        {
            try { await EnsureConnectedAndRegisteredAsync(); }
            catch { /* transient */ }
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
        if (_hub is null) return;

        try { await _hub.StopAsync(); } catch { }
        try { await _hub.DisposeAsync(); } catch { }
        _hub = null;
        _devicesSnapshotInitialized = false;
        NotifyConnectionChanged();
    }

    private async Task SafeRegisterAsync(bool retry = false)
    {
        if (_hub?.State != HubConnectionState.Connected) return;

        var attempts = retry ? 3 : 1;
        for (var i = 0; i < attempts; i++)
        {
            if (await TryRegisterDeviceAsync())
                return;

            if (i < attempts - 1)
                await Task.Delay(400 * (i + 1));
        }
    }

    private async Task<bool> TryRegisterDeviceAsync()
    {
        if (_hub?.State != HubConnectionState.Connected) return false;

        try
        {
            await _hub.InvokeAsync("RegisterDevice", MyDeviceId ?? "", MyDeviceName);
            await PublishBrowserCapabilitiesAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiSyncService] RegisterDevice failed: {ex.Message}");
            return false;
        }
    }

    private async Task PublishBrowserCapabilitiesAsync()
    {
        if (_hub?.State != HubConnectionState.Connected || string.IsNullOrEmpty(MyDeviceId))
            return;

        try
        {
            await _hub.InvokeAsync("UpdateBrowserCapabilities", MyDeviceId, true, true);
            SyncDebugLog.Hub($"Published SupportsBrowserSync=true IsNativeApp=true for {MyDeviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Error($"UpdateBrowserCapabilities failed: {ex.Message}");
        }
    }

    private async Task InvokeHubAsync(string method, params object?[] args)
    {
        if (_hub?.State != HubConnectionState.Connected) return;

        try
        {
            await _hub.InvokeAsync(method, args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiSyncService] {method} failed: {ex.Message}");
        }
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
                if (ids is not null)
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
            AutoSyncBookmarks = await LoadBoolPrefAsync(AutoSyncBookmarksKey, defaultValue: true);
            AutoSyncInstalledApps = await LoadBoolPrefAsync(AutoSyncAppsKey, defaultValue: true);
            AutoSyncLocalAi = await LoadBoolPrefAsync(AutoSyncLocalAiKey, defaultValue: true);
            AutoSyncLemonade = await LoadBoolPrefAsync(AutoSyncLemonadeKey, defaultValue: true);
            AutoSyncCloudProviders = await LoadBoolPrefAsync(AutoSyncCloudProvidersKey, defaultValue: true);
            AutoSyncHomeAssistant = await LoadBoolPrefAsync(AutoSyncHomeAssistantKey, defaultValue: true);
            AutoSyncTools = await LoadBoolPrefAsync(AutoSyncToolsKey, defaultValue: true);
            AutoSyncSystemPrompt = await LoadBoolPrefAsync(AutoSyncSystemPromptKey, defaultValue: true);
            AutoSyncProfile = await LoadBoolPrefAsync(AutoSyncProfileKey, defaultValue: true);
            AutoSyncMemories = await LoadBoolPrefAsync(AutoSyncMemoriesKey, defaultValue: true);
            AutoSyncAppearance = await LoadBoolPrefAsync(AutoSyncAppearanceKey, defaultValue: true);
            AutoSyncSkills = await LoadBoolPrefAsync(AutoSyncSkillsKey, defaultValue: true);
            AutoSyncWorkflows = await LoadBoolPrefAsync(AutoSyncWorkflowsKey, defaultValue: true);
        }
        catch
        {
            // Ignore preference load errors.
        }
    }

    /// <summary>Opt-out prefs: missing key defaults to true; only explicit "false" turns off.</summary>
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

    private string UserPrefixed(string baseKey) => StorageNamespace.PrefixedKey(_auth, baseKey);

    private async Task<string?> GetUserSettingAsync(string baseKey)
    {
        var nk = UserPrefixed(baseKey);
        var value = await _settings.GetStringAsync(nk);
        if (value != null)
            return value;

        // One-time migrate from pre-multi-user unprefixed keys.
        var legacy = await _settings.GetStringAsync(baseKey);
        if (legacy != null)
        {
            await _settings.SetStringAsync(nk, legacy);
            await _settings.RemoveAsync(baseKey);
            return legacy;
        }

        return null;
    }

    private async Task SetUserSettingAsync(string baseKey, string? value)
    {
        var nk = UserPrefixed(baseKey);
        if (value is null)
        {
            // Clear both namespaced and legacy keys so uncheck survives restart/refresh.
            await _settings.RemoveAsync(nk);
            await _settings.RemoveAsync(baseKey);
            return;
        }

        await _settings.SetStringAsync(nk, value);
        await _settings.RemoveAsync(baseKey);
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

    private async Task ApplyDevicesFromServerAndNotifyAsync(
        IReadOnlyList<SyncDeviceInfo>? list,
        HashSet<string> prevOnline)
    {
        try
        {
            await ApplyDevicesFromServerAsync(list);
            SyncDebugLog.Hub(
                $"DevicesUpdated count={Devices.Count}: " +
                string.Join("; ", Devices.Select(d =>
                    $"{d.Name} online={d.IsOnline} browser={d.SupportsBrowserSync} ai={d.CanRelayAi}")));

            if (!string.IsNullOrEmpty(MyDeviceId)
                && Devices.Any(d =>
                    string.Equals(d.DeviceId, MyDeviceId, StringComparison.OrdinalIgnoreCase)
                    && !d.SupportsBrowserSync))
            {
                _ = PublishBrowserCapabilitiesAsync();
            }

            if (!_devicesSnapshotInitialized)
            {
                _devicesSnapshotInitialized = true;
                OnChanged?.Invoke();
                return;
            }

            EnsureCoordinatorWired();
            _coordinator?.OnDevicesUpdated(Devices, prevOnline);
            OnChanged?.Invoke();
        }
        catch (Exception ex)
        {
            SyncDebugLog.Error($"DevicesUpdated handling failed: {ex.Message}");
        }
    }

    private void NotifyConnectionChanged() => OnChanged?.Invoke();

    private static string DeriveDefaultDeviceName()
    {
        try
        {
            var platform = DeviceInfo.Platform.ToString();
            var model = DeviceInfo.Model;
            if (!string.IsNullOrWhiteSpace(model))
                return $"Wizionic • {platform} ({model})";
            return $"Wizionic • {platform}";
        }
        catch
        {
            return "Wizionic • This device";
        }
    }
}