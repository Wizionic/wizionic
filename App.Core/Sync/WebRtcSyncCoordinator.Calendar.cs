using App.Core.Storage;

namespace App.Core.Sync;

/// <summary>Calendar WebRTC sync (meta + events) — partial of <see cref="WebRtcSyncCoordinator"/>.</summary>
public sealed partial class WebRtcSyncCoordinator
{
    public Task StartWebRtcCalendarSyncAsync(string targetDeviceId, string calendarId) =>
        EnqueueCalendarMetaSyncAsync(targetDeviceId, calendarId);

    public Task StartWebRtcCalendarEventSyncAsync(string targetDeviceId, string calendarId, string eventId) =>
        EnqueueCalendarEventSyncAsync(targetDeviceId, calendarId, eventId);

    /// <summary>Ids of Workflows system calendars — never transferred over WebRTC.</summary>
    private async Task<HashSet<string>> GetWorkflowCalendarIdsAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CalendarConstants.WorkflowCalendarId
        };
        if (_calendarStore is null) return set;
        try
        {
            foreach (var c in await _calendarStore.LoadCalendarsAsync())
            {
                if (c.IsWorkflowCalendar)
                    set.Add(c.Id);
            }
        }
        catch
        {
            // best-effort
        }
        return set;
    }

    private static bool IsWorkflowCalendarId(string? calendarId, HashSet<string> workflowCalIds) =>
        !string.IsNullOrWhiteSpace(calendarId)
        && (workflowCalIds.Contains(calendarId)
            || string.Equals(calendarId, CalendarConstants.WorkflowCalendarId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(calendarId, CalendarConstants.WorkflowCalendarName, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> IsWorkflowScopedEventAsync(string eventId, HashSet<string>? workflowCalIds = null)
    {
        if (_calendarStore is null || string.IsNullOrWhiteSpace(eventId)) return false;
        try
        {
            var evt = await _calendarStore.LoadEventAsync(eventId);
            if (evt is null) return false;
            if (!string.IsNullOrWhiteSpace(evt.WorkflowId)) return true;
            workflowCalIds ??= await GetWorkflowCalendarIdsAsync();
            return IsWorkflowCalendarId(evt.CalendarId, workflowCalIds);
        }
        catch
        {
            return false;
        }
    }

    public async Task EnqueueCalendarMetaSyncAsync(string targetDeviceId, string calendarId)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _calendarStore == null)
            return;
        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue calendar {calendarId}: hub not connected.");
            return;
        }

        var index = await _calendarStore.LoadCalendarsAsync();
        var cal = index.FirstOrDefault(c => string.Equals(c.Id, calendarId, StringComparison.OrdinalIgnoreCase));
        if (cal is null)
            return;

        // Device-local: Workflows calendar is never pushed to peers.
        if (cal.IsWorkflowCalendar || IsWorkflowCalendarId(cal.Id, await GetWorkflowCalendarIdsAsync()))
        {
            SyncDebugLog.Info($"Skipping workflow calendar sync for {calendarId}");
            return;
        }

        var dataJson = CalendarMetaSyncPayload.Serialize(
            cal.Id, cal.Name, cal.Color, cal.IsVisible, cal.Description, cal.LastUpdated.Ticks, cal.IsWorkflowCalendar);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Calendar,
            ItemId = calendarId,
            NoteTitle = cal.Name,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForCalendar(
                cal.Id, cal.Name, cal.Color, cal.IsVisible, cal.Description, cal.LastUpdated.Ticks, cal.IsWorkflowCalendar)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueCalendarDeleteAsync(string targetDeviceId, string calendarId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected() || _calendarStore == null)
            return;

        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Calendar,
            IsDelete = true,
            ItemId = calendarId,
            DataJson = DeleteSyncPayload.Serialize(calendarId, deletedAtUtc.Ticks),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtUtc.Ticks),
            DeletedAtTicks = deletedAtUtc.Ticks
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueCalendarEventSyncAsync(string targetDeviceId, string calendarId, string eventId)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _calendarStore == null)
            return;
        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue calendar event {calendarId}/{eventId}: hub not connected.");
            return;
        }

        var evt = await _calendarStore.LoadEventAsync(eventId);
        if (evt is null)
            return;

        // Device-local: workflow projections / WorkflowId events never leave this device.
        if (!string.IsNullOrWhiteSpace(evt.WorkflowId)
            || IsWorkflowCalendarId(evt.CalendarId, await GetWorkflowCalendarIdsAsync())
            || IsWorkflowCalendarId(calendarId, await GetWorkflowCalendarIdsAsync()))
        {
            SyncDebugLog.Info($"Skipping workflow calendar event sync for {calendarId}/{eventId}");
            return;
        }

        if (evt.DeletedAt.HasValue)
        {
            await EnqueueCalendarEventDeleteAsync(targetDeviceId, calendarId, eventId, evt.DeletedAt.Value);
            return;
        }

        if (!string.Equals(evt.CalendarId, calendarId, StringComparison.OrdinalIgnoreCase))
            evt = evt with { CalendarId = calendarId };

        var composite = CalendarEventSyncPayload.CompositeId(calendarId, eventId);
        var dataJson = CalendarEventSyncPayload.Serialize(calendarId, evt);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.CalendarEvent,
            ItemId = composite,
            NoteTitle = evt.Summary,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForCalendarEvent(evt)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueCalendarEventDeleteAsync(string targetDeviceId, string calendarId, string eventId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected() || _calendarStore == null)
            return;

        var composite = CalendarEventSyncPayload.CompositeId(calendarId, eventId);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.CalendarEvent,
            IsDelete = true,
            ItemId = composite,
            DataJson = DeleteSyncPayload.Serialize(composite, deletedAtUtc.Ticks),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtUtc.Ticks),
            DeletedAtTicks = deletedAtUtc.Ticks
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public void ScheduleAutoSyncCalendarAfterLocalSave(string calendarId)
    {
        if (!AutoSyncCalendar || _calendarStore == null || SyncTargetDeviceIds.Count == 0)
            return;
        if (IsWorkflowCalendarId(calendarId, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { CalendarConstants.WorkflowCalendarId }))
            return;

        _ = DebouncedAutoSyncAsync($"calendar:{calendarId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected() || _calendarStore == null)
                return;
            if (IsWorkflowCalendarId(calendarId, await GetWorkflowCalendarIdsAsync()))
                return;

            var manifest = await _calendarStore.LoadCalendarManifestEntriesAsync();
            var entry = manifest.FirstOrDefault(n => n.Id == calendarId);
            var fingerprint = entry?.ContentFingerprint;

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Calendar, calendarId, fingerprint))
                    continue;
                await EnqueueCalendarMetaSyncAsync(targetId, calendarId);
            }
        });
    }

    public void ScheduleAutoSyncCalendarDeleteAfterLocalDelete(string calendarId, DateTime deletedAt)
    {
        if (!AutoSyncCalendar || _calendarStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"calendar-delete:{calendarId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Calendar, calendarId, DeleteSyncPayload.AckValue(deletedAt.Ticks)))
                    continue;
                await EnqueueCalendarDeleteAsync(targetId, calendarId, deletedAt);
            }
        });
    }

    public void ScheduleAutoSyncEventAfterLocalSave(string calendarId, string eventId)
    {
        if (!AutoSyncCalendar || _calendarStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        var composite = CalendarEventSyncPayload.CompositeId(calendarId, eventId);
        _ = DebouncedAutoSyncAsync($"calendar-event:{composite}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected() || _calendarStore == null)
                return;
            if (await IsWorkflowScopedEventAsync(eventId) || IsWorkflowCalendarId(calendarId, await GetWorkflowCalendarIdsAsync()))
                return;

            var evt = await _calendarStore.LoadEventAsync(eventId);
            var fingerprint = evt is null ? null : SyncFingerprint.ForCalendarEvent(evt);

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.CalendarEvent, composite, fingerprint))
                    continue;
                await EnqueueCalendarEventSyncAsync(targetId, calendarId, eventId);
            }
        });
    }

    public void ScheduleAutoSyncEventDeleteAfterLocalDelete(string calendarId, string eventId, DateTime deletedAt)
    {
        if (!AutoSyncCalendar || _calendarStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        var composite = CalendarEventSyncPayload.CompositeId(calendarId, eventId);
        _ = DebouncedAutoSyncAsync($"calendar-event-delete:{composite}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.CalendarEvent, composite, DeleteSyncPayload.AckValue(deletedAt.Ticks)))
                    continue;
                await EnqueueCalendarEventDeleteAsync(targetId, calendarId, eventId, deletedAt);
            }
        });
    }

    public Task<int> SyncAllCalendarsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        StartDeltaSyncToDevicesAsync(targetDeviceIds, includeConvos: false, includeNotes: false, includeCalendars: true);

    internal async Task<bool> TryHandleCalendarDataChannelAsync(string peerId, string type, string? content, string? itemId, int? chunkIndex, int? chunkCount, string? chunkData)
    {
        if (_calendarStore == null)
            return false;

        if ((type == "calendar-sync-data" || type == "calendar-sync-chunk") && (content != null || itemId != null))
        {
            var contentJson = content;
            if (type == "calendar-sync-chunk")
            {
                if (itemId == null
                    || !TryAddChunk(peerId, itemId, SyncItemKind.Calendar, chunkIndex, chunkCount, chunkData, out contentJson))
                    return true;
            }
            if (contentJson == null) return true;

            await HandleIncomingCalendarSyncPayload(contentJson, peerId);
            var meta = CalendarMetaSyncPayload.Deserialize(contentJson);
            if (meta?.CalendarId != null)
            {
                var ack = new DataChannelMessage("calendar-sync-ack", itemId: meta.CalendarId);
                await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
            }
            return true;
        }

        if (type == "calendar-sync-ack" && itemId != null)
        {
            await HandleGenericItemAckAsync("calendar-sync-ack", itemId, peerId);
            return true;
        }

        if ((type == "calendar-event-sync-data" || type == "calendar-event-sync-chunk") && (content != null || itemId != null))
        {
            var contentJson = content;
            if (type == "calendar-event-sync-chunk")
            {
                if (itemId == null
                    || !TryAddChunk(peerId, itemId, SyncItemKind.CalendarEvent, chunkIndex, chunkCount, chunkData, out contentJson))
                    return true;
            }
            if (contentJson == null) return true;

            await HandleIncomingCalendarEventSyncPayload(contentJson, peerId);
            var evtPayload = CalendarEventSyncPayload.Deserialize(contentJson);
            if (evtPayload?.Event?.Id != null)
            {
                var composite = CalendarEventSyncPayload.CompositeId(evtPayload.CalendarId, evtPayload.Event.Id);
                var ack = new DataChannelMessage("calendar-event-sync-ack", itemId: composite);
                await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
            }
            return true;
        }

        if (type == "calendar-event-sync-ack" && itemId != null)
        {
            await HandleGenericItemAckAsync("calendar-event-sync-ack", itemId, peerId);
            return true;
        }

        if (type == "calendar-delete" && content != null)
        {
            await HandleIncomingCalendarDeleteAsync(content, peerId);
            var deletePayload = DeleteSyncPayload.Deserialize(content);
            if (deletePayload != null)
            {
                var ack = new DataChannelMessage("calendar-delete-ack", itemId: deletePayload.Id);
                await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
            }
            return true;
        }

        if (type == "calendar-delete-ack" && itemId != null)
        {
            await HandleGenericItemAckAsync("calendar-delete-ack", itemId, peerId);
            return true;
        }

        if (type == "calendar-event-delete" && content != null)
        {
            await HandleIncomingCalendarEventDeleteAsync(content, peerId);
            var deletePayload = DeleteSyncPayload.Deserialize(content);
            if (deletePayload != null)
            {
                var ack = new DataChannelMessage("calendar-event-delete-ack", itemId: deletePayload.Id);
                await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
            }
            return true;
        }

        if (type == "calendar-event-delete-ack" && itemId != null)
        {
            await HandleGenericItemAckAsync("calendar-event-delete-ack", itemId, peerId);
            return true;
        }

        return false;
    }

    private async Task HandleIncomingCalendarSyncPayload(string json, string fromDeviceId)
    {
        if (_calendarStore == null) return;
        try
        {
            var meta = CalendarMetaSyncPayload.Deserialize(json);
            if (meta == null || string.IsNullOrEmpty(meta.CalendarId))
                return;

            if (meta.IsWorkflowCalendar || IsWorkflowCalendarId(meta.CalendarId, await GetWorkflowCalendarIdsAsync()))
            {
                SyncDebugLog.Info($"Ignoring remote workflow calendar meta {meta.CalendarId}");
                return;
            }

            if (!await _calendarStore.ShouldAcceptIncomingCalendarAsync(meta.CalendarId, meta.LastUpdatedTicks))
            {
                SyncDebugLog.Info($"Ignoring stale calendar meta {meta.CalendarId}");
                return;
            }

            await _calendarStore.ApplyRemoteCalendarMetaAsync(
                meta.CalendarId, meta.Name, meta.Color, meta.IsVisible, meta.Description, meta.LastUpdatedTicks);
            OnCalendarsChanged?.Invoke();
            SyncDebugLog.Info($"Applied calendar meta {meta.CalendarId} from {fromDeviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist calendar meta: {ex.Message}");
        }
    }

    private async Task HandleIncomingCalendarEventSyncPayload(string json, string fromDeviceId)
    {
        if (_calendarStore == null) return;
        try
        {
            var payload = CalendarEventSyncPayload.Deserialize(json);
            if (payload?.Event is null || string.IsNullOrEmpty(payload.CalendarId))
                return;

            var evt = payload.Event with { CalendarId = payload.CalendarId };
            if (!string.IsNullOrWhiteSpace(evt.WorkflowId)
                || IsWorkflowCalendarId(evt.CalendarId, await GetWorkflowCalendarIdsAsync()))
            {
                SyncDebugLog.Info($"Ignoring remote workflow calendar event {evt.Id}");
                return;
            }

            if (!await _calendarStore.ShouldAcceptIncomingEventAsync(evt.Id, evt))
            {
                SyncDebugLog.Info($"Ignoring stale calendar event {evt.Id}");
                return;
            }

            await _calendarStore.UpsertEventAsync(evt);
            OnCalendarsChanged?.Invoke();
            SyncDebugLog.Info($"Applied calendar event {payload.CalendarId}/{evt.Id} from {fromDeviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist calendar event: {ex.Message}");
        }
    }

    private async Task HandleIncomingCalendarDeleteAsync(string json, string fromDeviceId)
    {
        if (_calendarStore == null) return;
        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;
            if (IsWorkflowCalendarId(payload.Id, await GetWorkflowCalendarIdsAsync()))
            {
                SyncDebugLog.Info($"Ignoring remote delete of workflow calendar {payload.Id}");
                return;
            }
            if (await _calendarStore.TryApplyRemoteCalendarDeleteAsync(payload.Id, payload.DeletedAtTicks))
            {
                OnCalendarsChanged?.Invoke();
                SyncDebugLog.Info($"Applied calendar delete {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to apply calendar delete: {ex.Message}");
        }
    }

    private async Task HandleIncomingCalendarEventDeleteAsync(string json, string fromDeviceId)
    {
        if (_calendarStore == null) return;
        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;

            var eventId = payload.Id;
            if (CalendarEventSyncPayload.TrySplitCompositeId(payload.Id, out var calId, out var splitId))
            {
                eventId = splitId;
                if (IsWorkflowCalendarId(calId, await GetWorkflowCalendarIdsAsync()))
                {
                    SyncDebugLog.Info($"Ignoring remote delete of workflow calendar event {eventId}");
                    return;
                }
            }
            if (await IsWorkflowScopedEventAsync(eventId))
            {
                SyncDebugLog.Info($"Ignoring remote delete of workflow calendar event {eventId}");
                return;
            }

            if (await _calendarStore.TryApplyRemoteEventDeleteAsync(eventId, payload.DeletedAtTicks))
            {
                OnCalendarsChanged?.Invoke();
                SyncDebugLog.Info($"Applied calendar event delete {eventId} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to apply calendar event delete: {ex.Message}");
        }
    }

    private async Task QueueCalendarItemsFromManifestAsync(string peerId, SyncManifestResponse response)
    {
        if (_calendarStore == null) return;

        foreach (var calendarId in (response.NeededCalendars ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            await EnqueueCalendarMetaSyncAsync(peerId, calendarId);

        foreach (var eventId in (response.NeededCalendarEvents ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var evt = await _calendarStore.LoadEventAsync(eventId);
            if (evt is null) continue;
            await EnqueueCalendarEventSyncAsync(peerId, evt.CalendarId, eventId);
        }
    }
}
