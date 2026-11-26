using System.Text.Json;
using App.Core.Storage;

namespace App.Core.Sync;

public sealed partial class WebRtcSyncCoordinator
{
    readonly ProjectStore? _projects;
    public event Action? OnProjectsChanged;
    public void ScheduleProjectSync()
    {
        if (!AutoSyncChatHistory || _projects == null) return;
        _ = DebouncedAutoSyncAsync("projects", async () =>
        {
            if (EnsureConnectedAsync != null) await EnsureConnectedAsync();
            foreach (var peer in GetOnlineSyncTargetIdsInternal()) await QueueProjectsAsync(peer);
        });
    }
    private async Task QueueProjectsAsync(string peer)
    {
        if (_projects == null) return;
        var ack = await LoadPeerAckStateAsync(peer);
        foreach (var row in (await _projects.IndexAsync()).Where(d => !ProjectStore.IsCache(d.Id)).OrderBy(d => d.Id))
        {
            if (ack.GetValueOrDefault(GetAckItemKey(SyncItemKind.Project, row.Id)) == row.Source) continue;
            await EnqueueSyncAsync(peer, new SyncQueueItem { Kind = SyncItemKind.Project, ItemId = row.Id,
                DataJson = JsonSerializer.Serialize(row), ContentFingerprint = row.Source });
        }
    }
    private async Task<bool> HandleProjectMessageAsync(string peer, DataChannelMessage msg)
    {
        if (msg.type is not ("project-sync-data" or "project-sync-chunk" or "project-sync-ack")) return false;
        if (_projects == null || msg.itemId == null || IsAuthenticated?.Invoke() != true) return true;
        if (msg.type == "project-sync-ack")
        {
            if (_activeSyncByPeer.TryGetValue(peer, out var item) && item.Kind == SyncItemKind.Project && item.ItemId == msg.itemId && msg.requestRevision == item.ContentFingerprint)
            {
                await RecordSuccessfulSyncAsync(peer, item);
                await AdvanceSyncQueueAsync(peer);
            }
            return true;
        }
        var json = msg.content;
        if (msg.type == "project-sync-chunk" && (msg.chunkCount is not (> 0 and <= 256) || !TryAddChunk(peer, msg.itemId, SyncItemKind.Project, msg.chunkIndex, msg.chunkCount, msg.chunkData, out json))) return true;
        if (json == null || json.Length > 512 * 1024) return true;
        var block = JsonSerializer.Deserialize<ProjectBlock>(json);
        if (block == null || block.Id != msg.itemId) return true;
        var changed = await _projects.ApplyAsync(block);
        await _webrtc.SendDataAsync(peer, SerializeDataChannelMessage(new DataChannelMessage("project-sync-ack", itemId: block.Id, requestRevision: block.Version)));
        if (changed)
        {
            // The sender already has this revision; forward it only to other configured peers.
            await RecordSuccessfulSyncAsync(peer, new SyncQueueItem { Kind = SyncItemKind.Project, ItemId = block.Id, ContentFingerprint = block.Version, DataJson = "" });
            Raise(OnProjectsChanged);
            ScheduleProjectSync();
        }
        return true;
    }
}
