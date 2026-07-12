using System.Net;
using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Browser;
using ChatfishApp.Core.Chat;
using ChatfishApp.Core.Configuration;
using ChatfishApp.Core.Storage;
using ChatfishApp.Core.Sync;
using ChatfishApp.Shared.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// MAUI sync: SignalR presence + WebRTC data transfer via SIPSorcery and shared coordinator.
/// </summary>
public sealed class MauiSyncService : ISyncService, IWebRtcTransportCallbacks
{
    private const string DeviceIdKey = "chatfish-device-id";
    private const string DeviceNameKey = "chatfish-device-name";
    private const string AiServerDeviceIdKey = "chatfish-ai-server-device-id";
    private const string SyncTargetDevicesKey = "chatfish-sync-target-devices";
    private const string AutoSyncChatKey = "chatfish-auto-sync-chat";
    private const string AutoSyncNotesKey = "chatfish-auto-sync-notes";
    private const string AutoSyncBookmarksKey = "chatfish-auto-sync-bookmarks";
    private const string AutoSyncAppsKey = "chatfish-auto-sync-apps";

    private readonly MauiAuthCookieStore _cookieStore;
    private readonly SqliteSettingsDatabase _settings;
    private readonly IAuthService _auth;
    private readonly ChatfishServerOptions _serverOptions;
    private readonly IWebRtcTransport _webrtc;
    private readonly ISyncPreferencesStore _prefs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKeyStore _keyStore;
    private readonly IBrowserStore _browserStore;
    private readonly IBrowserSidebarStore _sidebarStore;
    private readonly ChatModelCatalogService _modelCatalog;
    private readonly ChatCompletionService _chatCompletion;
    private readonly AiProxyRelay _aiProxy;

    private IServiceScope? _syncScope;
    private WebRtcSyncCoordinator? _coordinator;
    private readonly HashSet<string> _syncTargetDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _devicesSnapshotInitialized;

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

    public bool AutoSyncChatHistory { get; private set; }
    public bool AutoSyncNotes { get; private set; }
    public bool AutoSyncBookmarks { get; private set; }
    public bool AutoSyncInstalledApps { get; private set; }
    public IReadOnlyCollection<string> SyncTargetDeviceIds => _syncTargetDeviceIds;

    public event Action? OnChanged;
    public event Action? OnConversationsChanged;
    public event Action? OnNotesChanged;
    public event Action? OnBookmarksChanged;
    public event Action? OnInstalledAppsChanged;
    public event Action<string, string, string>? OnSyncPayloadReceived;
    public event Action<string, string>? OnSyncAckReceived;
    public event Action<string, string, string>? OnNoteSyncPayloadReceived;
    public event Action<string, string>? OnNoteSyncAckReceived;

    public MauiSyncService(
        MauiAuthCookieStore cookieStore,
        SqliteSettingsDatabase settings,
        IAuthService auth,
        IOptions<ChatfishServerOptions> serverOptions,
        IWebRtcTransport webrtc,
        ISyncPreferencesStore prefs,
        IServiceScopeFactory scopeFactory,
        IKeyStore keyStore,
        IBrowserStore browserStore,
        IBrowserSidebarStore sidebarStore,
        ChatModelCatalogService modelCatalog,
        ChatCompletionService chatCompletion)
    {
        _cookieStore = cookieStore;
        _settings = settings;
        _auth = auth;
        _serverOptions = serverOptions.Value;
        _webrtc = webrtc;
        _prefs = prefs;
        _scopeFactory = scopeFactory;
        _keyStore = keyStore;
        _browserStore = browserStore;
        _sidebarStore = sidebarStore;
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

        var savedAiServer = await _settings.GetStringAsync(AiServerDeviceIdKey);
        _aiProxy.AiServerDeviceId = string.IsNullOrWhiteSpace(savedAiServer) ? null : savedAiServer;

        await LoadSyncPreferencesAsync();
        EnsureCoordinator();
        EnsureCoordinatorWired();
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
            var hubUrl = $"{_serverOptions.SyncHubUrl}?deviceId={Uri.EscapeDataString(MyDeviceId ?? "")}";
            var hubUri = new Uri(_serverOptions.SyncHubUrl);
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
            Console.WriteLine($"[MauiSyncService] Hub cookies for {_serverOptions.SyncHubUrl}: {cookieCount}");
            if (cookieCount == 0)
                Console.WriteLine("[MauiSyncService] WARNING: No auth cookies — hub connection will likely fail.");

            _hub.On<IReadOnlyList<SyncDeviceInfo>>("DevicesUpdated", list =>
            {
                var prevOnline = Devices
                    .Where(d => d.IsOnline)
                    .Select(d => d.DeviceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                Devices = list ?? Array.Empty<SyncDeviceInfo>();

                if (!_devicesSnapshotInitialized)
                {
                    _devicesSnapshotInitialized = true;
                    OnChanged?.Invoke();
                    return;
                }

                EnsureCoordinatorWired();
                _coordinator?.OnDevicesUpdated(Devices, prevOnline);
                OnChanged?.Invoke();
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

        await _settings.SetStringAsync(
            SyncTargetDevicesKey,
            System.Text.Json.JsonSerializer.Serialize(_syncTargetDeviceIds.ToList()));
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public async Task SetAutoSyncChatHistoryAsync(bool enabled)
    {
        AutoSyncChatHistory = enabled;
        await _settings.SetStringAsync(AutoSyncChatKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public async Task SetAutoSyncNotesAsync(bool enabled)
    {
        AutoSyncNotes = enabled;
        await _settings.SetStringAsync(AutoSyncNotesKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public async Task SetAutoSyncBookmarksAsync(bool enabled)
    {
        AutoSyncBookmarks = enabled;
        await _settings.SetStringAsync(AutoSyncBookmarksKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

    public async Task SetAutoSyncInstalledAppsAsync(bool enabled)
    {
        AutoSyncInstalledApps = enabled;
        await _settings.SetStringAsync(AutoSyncAppsKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
        EnsureCoordinatorWired();
    }

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

    public string? GetAiServerDeviceName()
    {
        if (string.IsNullOrEmpty(_aiProxy.AiServerDeviceId)) return null;
        return Devices.FirstOrDefault(d => string.Equals(d.DeviceId, _aiProxy.AiServerDeviceId, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    public Task SetAiServerDeviceAsync(string? deviceId) =>
        _aiProxy.SetAiServerDeviceAsync(
            deviceId,
            () => _settings.RemoveAsync(AiServerDeviceIdKey),
            id => _settings.SetStringAsync(AiServerDeviceIdKey, id),
            IsSelf);

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
            sidebarStore: _sidebarStore);

        _coordinator.OnConversationsChanged += () => OnConversationsChanged?.Invoke();
        _coordinator.OnNotesChanged += () => OnNotesChanged?.Invoke();
        _coordinator.OnBookmarksChanged += () => OnBookmarksChanged?.Invoke();
        _coordinator.OnInstalledAppsChanged += () => OnInstalledAppsChanged?.Invoke();
        _coordinator.OnSyncPayloadReceived += (c, j, f) => OnSyncPayloadReceived?.Invoke(c, j, f);
        _coordinator.OnSyncAckReceived += (c, f) => OnSyncAckReceived?.Invoke(c, f);
        _coordinator.OnNoteSyncPayloadReceived += (n, j, f) => OnNoteSyncPayloadReceived?.Invoke(n, j, f);
        _coordinator.OnNoteSyncAckReceived += (n, f) => OnNoteSyncAckReceived?.Invoke(n, f);
    }

    private void EnsureCoordinatorWired()
    {
        EnsureCoordinator();
        _coordinator!.AutoSyncChatHistory = AutoSyncChatHistory;
        _coordinator.AutoSyncNotes = AutoSyncNotes;
        _coordinator.AutoSyncBookmarks = AutoSyncBookmarks;
        _coordinator.AutoSyncInstalledApps = AutoSyncInstalledApps;
        _coordinator.SyncTargetDeviceIds = _syncTargetDeviceIds;
        _coordinator.IsSelf = IsSelf;
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
        if (_auth.IsAuthenticated)
        {
            try { await EnsureConnectedAndRegisteredAsync(); }
            catch { /* transient */ }
        }
        else
        {
            await StopAsync();
            Devices = Array.Empty<SyncDeviceInfo>();
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
            await _hub.InvokeAsync("UpdateBrowserCapabilities", MyDeviceId, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiSyncService] UpdateBrowserCapabilities failed: {ex.Message}");
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
            var targetsJson = await _settings.GetStringAsync(SyncTargetDevicesKey);
            if (!string.IsNullOrWhiteSpace(targetsJson))
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(targetsJson);
                if (ids is not null)
                {
                    foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)))
                        _syncTargetDeviceIds.Add(id);
                }
            }

            AutoSyncChatHistory = string.Equals(
                await _settings.GetStringAsync(AutoSyncChatKey),
                "true",
                StringComparison.Ordinal);
            AutoSyncNotes = string.Equals(
                await _settings.GetStringAsync(AutoSyncNotesKey),
                "true",
                StringComparison.Ordinal);
            AutoSyncBookmarks = string.Equals(
                await _settings.GetStringAsync(AutoSyncBookmarksKey),
                "true",
                StringComparison.Ordinal);
            AutoSyncInstalledApps = string.Equals(
                await _settings.GetStringAsync(AutoSyncAppsKey),
                "true",
                StringComparison.Ordinal);
        }
        catch
        {
            // Ignore preference load errors.
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
                return $"Chatfish • {platform} ({model})";
            return $"Chatfish • {platform}";
        }
        catch
        {
            return "Chatfish • This device";
        }
    }
}