using System.ComponentModel;
using System.Text.Json;
using App.Core.SmartHome;
using App.Core.Tools;
using App.Shared.Services.Tools;
using Microsoft.Extensions.AI;

namespace App.Maui.Services;

/// <summary>
/// Home Assistant tools: generic entity discovery and service control for any domain,
/// plus light convenience and Assist conversation as a secondary path.
/// </summary>
public sealed class HomeAssistantToolModule : IToolModule
{
    private readonly ISmartHomeService _ha;
    private readonly IToolExecutionTrace _trace;
    private readonly IReadOnlyList<AITool> _tools;

    public string ModuleName => "HomeAssistant";
    public bool IsAvailable => _ha.IsConfigured;

    public HomeAssistantToolModule(ISmartHomeService ha, IToolExecutionTrace trace)
    {
        _ha = ha ?? throw new ArgumentNullException(nameof(ha));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));

        _tools =
        [
            AIFunctionFactory.Create(ListEntities),
            AIFunctionFactory.Create(ListLights),
            AIFunctionFactory.Create(ControlLight),
            AIFunctionFactory.Create(ControlMediaPlayer),
            AIFunctionFactory.Create(GetEntityState),
            AIFunctionFactory.Create(CallService),
            AIFunctionFactory.Create(ListServices),
            AIFunctionFactory.Create(ProcessConversation)
        ];
    }

    public IReadOnlyList<AITool> GetTools() => IsAvailable ? _tools : [];

    [Description(
        "Discover Home Assistant entities. Call this when you need entity_id for a device, room, or domain " +
        "(light, switch, media_player, climate, cover, fan, lock, scene, script, remote, vacuum, etc.). " +
        "domain is optional (omit for all controllable domains). search matches friendly_name or entity_id " +
        "(e.g. search='kitchen' or 'denon'). Never invent entity_ids — always list/search first if unknown.")]
    private async Task<string> ListEntities(
        [Description("Optional HA domain filter, e.g. 'media_player', 'light', 'switch', 'climate', 'cover', 'scene'")] string? domain = null,
        [Description("Optional search text matching friendly name or entity_id, e.g. 'kitchen', 'denon', 'living room'")] string? search = null)
    {
        _trace.Record($"🏠 list_entities(domain=\"{domain ?? ""}\", search=\"{search ?? ""}\")");
        var result = await _ha.ListEntitiesAsync(domain, search);
        TraceResult(result);
        return result;
    }

    [Description("List all Home Assistant light entities. Prefer ListEntities(domain='light', search=...) when searching by room name.")]
    private async Task<string> ListLights()
    {
        _trace.Record("🏠 list_lights()");
        var result = await _ha.ListLightEntitiesAsync();
        TraceResult(result);
        return result;
    }

    [Description("Control a Home Assistant light entity. Can turn on/off, set brightness, or change color. For non-light devices use CallService.")]
    private async Task<string> ControlLight(
        [Description("Entity ID, e.g. 'light.kitchen'")] string entityId,
        [Description("State: 'on' or 'off'")] string state,
        [Description("Optional color name (e.g. 'blue', 'red') or hex like '#0000FF'")] string? color = null,
        [Description("Optional brightness 0-255")] int? brightness = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return "Entity ID is required.";

        var normalizedState = state?.Trim().ToLowerInvariant() ?? "";
        _trace.Record($"🏠 control_light(entity=\"{entityId}\", state=\"{normalizedState}\")");

        if (normalizedState == "off")
        {
            var result = await _ha.CallServiceAsync("light", "turn_off", new { entity_id = entityId });
            TraceResult(result);
            return IsSuccess(result) ? $"Turned off {entityId}.\n{result}" : result;
        }

        if (normalizedState != "on")
            return "State must be 'on' or 'off'.";

        var serviceData = new Dictionary<string, object?> { ["entity_id"] = entityId };

        if (brightness.HasValue)
            serviceData["brightness"] = Math.Clamp(brightness.Value, 0, 255);

        if (!string.IsNullOrWhiteSpace(color))
        {
            if (color.StartsWith('#'))
                serviceData["rgb_color"] = HexToRgb(color);
            else
                serviceData["color_name"] = color.Trim();
        }

        var onResult = await _ha.CallServiceAsync("light", "turn_on", serviceData);
        TraceResult(onResult);
        return IsSuccess(onResult) ? $"Turned on {entityId}.\n{onResult}" : onResult;
    }

    [Description(
        "Control a Home Assistant media_player (AVR, TV, speaker, Shield, etc.). " +
        "Prefer this over CallService for play/pause/stop/power/volume. " +
        "volume_percent is 0-100 (converted to HA volume_level 0.0-1.0). " +
        "If entity_id is unknown, call ListEntities(domain='media_player', search=...) first.")]
    private async Task<string> ControlMediaPlayer(
        [Description("Media player entity_id, e.g. 'media_player.denon_avr_x1700h_2'")] string entityId,
        [Description("Action: play, pause, stop, on, off, volume, or select_source")] string action,
        [Description("Volume percent 0-100 when action is 'volume' (e.g. 50 means half volume)")] int? volumePercent = null,
        [Description("Source name when action is 'select_source'")] string? source = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return "Entity ID is required. Call ListEntities(domain='media_player') first.";

        var act = (action ?? "").Trim().ToLowerInvariant();
        _trace.Record($"🏠 control_media_player(entity=\"{entityId}\", action=\"{act}\", volume={volumePercent?.ToString() ?? ""})");

        string domain = "media_player";
        string service;
        var serviceData = new Dictionary<string, object?> { ["entity_id"] = entityId.Trim() };

        switch (act)
        {
            case "play":
            case "media_play":
                service = "media_play";
                break;
            case "pause":
            case "media_pause":
                service = "media_pause";
                break;
            case "stop":
            case "media_stop":
                service = "media_stop";
                break;
            case "on":
            case "turn_on":
                service = "turn_on";
                break;
            case "off":
            case "turn_off":
                service = "turn_off";
                break;
            case "volume":
            case "volume_set":
            case "set_volume":
                if (!volumePercent.HasValue)
                    return "volume_percent (0-100) is required when action is volume.";
                service = "volume_set";
                serviceData["volume_level"] = Math.Clamp(volumePercent.Value, 0, 100) / 100.0;
                break;
            case "select_source":
            case "source":
                if (string.IsNullOrWhiteSpace(source))
                    return "source is required when action is select_source.";
                service = "select_source";
                serviceData["source"] = source.Trim();
                break;
            default:
                return "Action must be one of: play, pause, stop, on, off, volume, select_source.";
        }

        var result = await _ha.CallServiceAsync(domain, service, serviceData);
        TraceResult(result);
        if (!IsSuccess(result))
            return result;

        if (act is "volume" or "volume_set" or "set_volume")
            return $"Set volume on {entityId} to {Math.Clamp(volumePercent!.Value, 0, 100)}%.\n{result}";

        return $"Media player {entityId}: {service} succeeded.\n{result}";
    }

    [Description("Get the current state of any Home Assistant entity (light, switch, media_player, sensor, climate, cover, etc.).")]
    private async Task<string> GetEntityState(
        [Description("Entity ID, e.g. 'light.kitchen', 'media_player.living_room', 'sensor.temperature'")] string entityId)
    {
        _trace.Record($"🏠 get_entity_state(entity=\"{entityId}\")");

        var json = await _ha.GetEntityStateAsync(entityId);
        if (IsFailure(json))
        {
            _trace.Record($"   ❌ {Truncate(json, 300)}");
            return json;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var s) ? s.GetString() : "unknown";
            var friendly = root.TryGetProperty("attributes", out var attrs) &&
                           attrs.TryGetProperty("friendly_name", out var fn)
                ? fn.GetString()
                : entityId;

            var summary = $"{friendly} ({entityId}) is {state}.";
            _trace.Record($"   ✅ {summary}");
            return $"{summary}\n\nFull state:\n{json}";
        }
        catch
        {
            _trace.Record("   ✅ returned state JSON");
            return json;
        }
    }

    [Description(
        "Call any Home Assistant service (POST /api/services/{domain}/{service}). " +
        "PRIMARY control path for non-light devices. Always put entity_id in service_data JSON. " +
        "Examples: " +
        "media_player turn_on/media_play/media_pause/volume_set/select_source/play_media " +
        "(play_media needs media_content_id + media_content_type); " +
        "switch turn_on/turn_off; climate set_temperature/set_hvac_mode; " +
        "cover open_cover/close_cover; scene turn_on; script turn_on; fan turn_on/turn_off/set_percentage; " +
        "lock lock/unlock; remote send_command. " +
        "If entity_id is unknown, call ListEntities first.")]
    private async Task<string> CallService(
        [Description("Service domain, e.g. 'media_player', 'switch', 'climate', 'cover', 'scene', 'script', 'light'")] string domain,
        [Description("Service name, e.g. 'turn_on', 'turn_off', 'media_play', 'play_media', 'set_temperature', 'open_cover'")] string service,
        [Description("JSON object for service data, e.g. {\"entity_id\": \"media_player.living_room\"} or {\"entity_id\": \"media_player.avr\", \"volume_level\": 0.3}")] string serviceDataJson = "{}")
    {
        _trace.Record($"🏠 call_service({domain}.{service}, data={Truncate(serviceDataJson, 120)})");

        object serviceData;
        try
        {
            serviceData = JsonSerializer.Deserialize<Dictionary<string, object?>>(serviceDataJson)
                          ?? new Dictionary<string, object?>();
        }
        catch (Exception ex)
        {
            var err = $"Invalid service_data JSON: {ex.Message}";
            _trace.Record($"   ❌ {err}");
            return err;
        }

        var result = await _ha.CallServiceAsync(domain, service, serviceData);
        TraceResult(result);
        return result;
    }

    [Description("List Home Assistant services for a domain (or all domains). Use when unsure which service name to call for a device type.")]
    private async Task<string> ListServices(
        [Description("Optional domain filter, e.g. 'media_player', 'climate', 'cover'")] string? domain = null)
    {
        _trace.Record($"🏠 list_services(domain=\"{domain ?? ""}\")");
        var result = await _ha.ListServicesAsync(domain);
        TraceResult(result);
        return result;
    }

    [Description(
        "Send a natural-language command to Home Assistant Assist (built-in conversation agent). " +
        "SECONDARY path — good for area phrases like 'turn off kitchen lights'. " +
        "For media players, climate setpoints, or when Assist returns no_intent_match, use ListEntities + CallService instead. " +
        "Do not refuse smart-home control; fall back to structured tools.")]
    private async Task<string> ProcessConversation(
        [Description("Natural language command without the wake word, e.g. 'turn off the kitchen lights'")] string text,
        [Description("Optional conversation_id from a previous Assist response for multi-turn")] string? conversationId = null)
    {
        _trace.Record($"🏠 process_conversation(text=\"{Truncate(text, 80)}\")");
        var result = await _ha.ProcessConversationAsync(text, conversationId);
        TraceResult(result);
        return result;
    }

    private void TraceResult(string result)
    {
        var ok = IsSuccess(result);
        var preview = Truncate(result.Replace('\n', ' '), 400);
        _trace.Record($"   {(ok ? "✅" : "❌")} {preview}");
    }

    private static bool IsFailure(string? result) =>
        HomeAssistantService.IsHaFailure(result) ||
        (result?.StartsWith("Invalid service_data", StringComparison.OrdinalIgnoreCase) == true) ||
        (result?.StartsWith("Entity ID is required", StringComparison.OrdinalIgnoreCase) == true) ||
        (result?.StartsWith("State must be", StringComparison.OrdinalIgnoreCase) == true) ||
        (result?.StartsWith("Assist could not handle", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsSuccess(string? result) => !IsFailure(result);

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? "";
        return text[..max] + "…";
    }

    private static int[] HexToRgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6)
            return [255, 255, 255];

        return
        [
            Convert.ToInt32(hex[..2], 16),
            Convert.ToInt32(hex[2..4], 16),
            Convert.ToInt32(hex[4..6], 16)
        ];
    }
}
