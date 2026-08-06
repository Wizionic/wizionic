using App.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace App.Apis;

/// <summary>
/// SignalR hub for WASM client device presence (Phase 1) and future live sync signaling (Phase 2).
///
/// Auth:
/// - Uses the same "AppAuth" cookie that the WASM client already sends for /api/* calls.
/// - Therefore Context.User is populated for connected clients (same as the HTTP endpoints).
///
/// Groups:
/// - Clients are added to a per-user group "user:{userIdOrEmail}" so we can efficiently
///   broadcast only to that user's other devices (and the originating device for acks).
///
/// Protocol for Phase 1 (device list + online status):
/// - On connect the client immediately calls RegisterDevice(deviceId, deviceName).
/// - Server replies with current device list via "DevicesUpdated" (includes self).
/// - Client can call Heartbeat() periodically (optional but keeps LastActive fresh).
/// - Client can call UpdateDeviceName(newName) to rename "My Laptop".
/// - On disconnect the server automatically cleans up the connection and broadcasts the
///   updated list (the device becomes "offline" but its last-seen time is preserved).
///
/// For Phase 2 (actual P2P chat history sync via WebRTC):
/// - The same hub will carry signaling messages: Offer, Answer, IceCandidate, etc.
/// - Messages will be addressed to specific (user, targetDeviceId) so only the intended
///   peer receives them (via SendToDevice or a targeted group).
/// - The actual (encrypted) conversation blobs never touch the server; only tiny signaling
///   payloads and the presence/auth handshake do.
///
/// The server never stores or sees conversation content for the WASM path.
/// </summary>
[Authorize(AuthenticationSchemes = "AppAuth")]
public class SyncHub : Hub
{
    private readonly DevicePresenceService _presence;

    public SyncHub(DevicePresenceService presence)
    {
        _presence = presence;
    }

    private string? GetUserId() =>
        Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private string? GetEmail() =>
        Context.User?.Identity?.Name;

    private string GetUserGroup() =>
        $"user:{GetUserId() ?? GetEmail() ?? "anon"}";

    private string GetDeviceIdFromClient() =>
        Context.GetHttpContext()?.Request.Query["deviceId"].ToString()
        ?? Context.Items["DeviceId"] as string
        ?? string.Empty;

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        var email = GetEmail();

        // If the client passed ?deviceId=... on the connection URL we can pre-associate.
        // The authoritative registration still comes from the RegisterDevice RPC.
        var deviceId = Context.GetHttpContext()?.Request.Query["deviceId"].ToString();
        if (!string.IsNullOrWhiteSpace(deviceId))
            Context.Items["DeviceId"] = deviceId;

        // Join the user's group so we receive broadcasts for this account's devices.
        await Groups.AddToGroupAsync(Context.ConnectionId, GetUserGroup());

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var updated = _presence.OnDisconnected(Context.ConnectionId);

        if (updated != null)
        {
            // Notify everyone in the user's group (including other tabs of same device)
            await Clients.Group(GetUserGroup()).SendAsync("DevicesUpdated", updated);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client must call this shortly after connecting (and after it has loaded/created its
    /// persistent deviceId + chosen deviceName from browser storage).
    /// </summary>
    public async Task RegisterDevice(string deviceId, string deviceName)
    {
        var userId = GetUserId();
        var email = GetEmail();
        var connId = Context.ConnectionId;

        if (!string.IsNullOrWhiteSpace(deviceId))
            Context.Items["DeviceId"] = deviceId;

        var list = _presence.Register(userId, email, deviceId, deviceName, connId);

        // Send the authoritative list to the caller immediately (for fast UI paint)
        await Clients.Caller.SendAsync("DevicesUpdated", list);

        // Also broadcast to the rest of the user's devices so their lists update in real time.
        await Clients.GroupExcept(GetUserGroup(), Context.ConnectionId)
                     .SendAsync("DevicesUpdated", list);
    }

    /// <summary>
    /// Optional: client can send this every 30-60s while the tab is visible to keep
    /// "Last Active" reasonably fresh for the "online" entries.
    /// </summary>
    public async Task Heartbeat(string deviceId)
    {
        var userId = GetUserId();
        var email = GetEmail();

        _presence.Heartbeat(userId, email, deviceId, Context.ConnectionId);

        // We do not broadcast on every heartbeat to avoid noise.
        // The UI will see the device as online because it has an active connection.
        // If you want fresher timestamps pushed, you can broadcast here (throttled).
    }

    /// <summary>
    /// Client reports how many AI models it can serve (Ollama + configured cloud keys).
    /// Used to show "Use this device for my chats" on peers that have local AI access.
    /// </summary>
    public async Task UpdateAiCapabilities(string deviceId, int modelCount)
    {
        var userId = GetUserId();
        var email = GetEmail();

        var list = _presence.UpdateAiCapabilities(userId, email, deviceId, modelCount);
        await Clients.Group(GetUserGroup()).SendAsync("DevicesUpdated", list);
    }

    /// <summary>
    /// MAUI clients report that they can sync browser bookmarks / installed PWAs over WebRTC.
    /// </summary>
    public async Task UpdateBrowserCapabilities(string deviceId, bool supportsBrowserSync)
    {
        var userId = GetUserId();
        var email = GetEmail();

        var list = _presence.UpdateBrowserCapabilities(userId, email, deviceId, supportsBrowserSync);
        await Clients.Group(GetUserGroup()).SendAsync("DevicesUpdated", list);
    }

    /// <summary>
    /// User renamed the device from the Sync page. Update server-side record and broadcast.
    /// </summary>
    public async Task UpdateDeviceName(string deviceId, string newName)
    {
        var userId = GetUserId();
        var email = GetEmail();

        var list = _presence.UpdateName(userId, email, deviceId, newName);

        await Clients.Group(GetUserGroup()).SendAsync("DevicesUpdated", list);
    }

    // --- Phase 2 signaling support ---

    /// <summary>
    /// Sends a sync payload (encrypted conversation content) to a specific target device.
    /// The server only routes; it never decrypts or stores the content.
    /// </summary>
    public async Task SendSyncPayload(string targetDeviceId, string convoId, string encryptedContentJson, string fromDeviceId)
    {
        var userId = GetUserId();
        var email = GetEmail();

        var connectionIds = _presence.GetActiveConnectionIds(userId, email, targetDeviceId);

        foreach (var connId in connectionIds)
        {
            await Clients.Client(connId).SendAsync("SyncPayloadReceived", convoId, encryptedContentJson, fromDeviceId);
        }

        // Acknowledge to the sender so the UI can update
        await Clients.Caller.SendAsync("SyncPayloadSent", targetDeviceId, convoId);
    }

    /// <summary>
    /// Generic signaling for WebRTC (offer/answer/ice). Routes to specific device.
    /// </summary>
    public async Task SendToDevice(string targetDeviceId, string messageType, string payloadJson)
    {
        var userId = GetUserId();
        var email = GetEmail();
        var fromDeviceId = Context.Items["DeviceId"] as string ?? "";

        var connectionIds = _presence.GetActiveConnectionIds(userId, email, targetDeviceId);

        foreach (var connId in connectionIds)
        {
            // Send as three separate arguments so it matches the client's
            // _hub.On<string, string, string>("ReceiveSignaling", ...) handler.
            await Clients.Client(connId).SendAsync("ReceiveSignaling", fromDeviceId, messageType, payloadJson);
        }
    }
}
