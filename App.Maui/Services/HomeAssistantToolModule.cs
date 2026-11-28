using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            AIFunctionFactory.Create(ControlLight),
            AIFunctionFactory.Create(ControlMediaPlayer),
            AIFunctionFactory.Create(ControlClimate),
            AIFunctionFactory.Create(ControlCover),
            AIFunctionFactory.Create(ActivateScene),
            AIFunctionFactory.Create(RunScript),
            AIFunctionFactory.Create(GetEntityState),
            AIFunctionFactory.Create(CallService),
            AIFunctionFactory.Create(ProcessConversation)
        ];
    }

    public IReadOnlyList<AITool> GetTools() => IsAvailable ? _tools : [];

    [Description(
        "Find Home Assistant entity_ids by domain and/or search (room or name). Never invent entity_ids.")]
    private async Task<string> ListEntities(
        [Description("Optional HA domain filter, e.g. 'media_player', 'light', 'switch', 'climate', 'cover', 'scene'")] string? domain = null,
        [Description("Optional search text matching friendly name or entity_id, e.g. 'kitchen', 'denon', 'living room'")] string? search = null)
    {
        _trace.Record($"🏠 list_entities(domain=\"{domain ?? ""}\", search=\"{search ?? ""}\")");
        var result = await _ha.ListEntitiesAsync(domain, search);
        TraceResult(result);
        return result;
    }

    [Description("Turn a light on/off; optional color and brightness 0-255. Color names are resolved to RGB.")]
    private async Task<string> ControlLight(
        [Description("Entity ID, e.g. 'light.kitchen'")] string entityId,
        [Description("State: 'on' or 'off'")] string state,
        [Description("Optional color name (e.g. 'purple', 'violet', 'magenta', 'white') or hex like '#8000FF'. Wizionic sends rgb_color. Omit unless the user asked for a color.")] string? color = null,
        [Description("Optional brightness 0-255 (50% ≈ 128). Omit unless the user asked for a specific brightness.")] int? brightness = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return "Entity ID is required.";

        var normalizedState = state?.Trim().ToLowerInvariant() ?? "";
        var colorText = color?.Trim();
        int[]? rgb = null;

        if (normalizedState == "on" && !string.IsNullOrWhiteSpace(colorText))
        {
            if (!HomeAssistantColor.TryParse(colorText, out var parsed))
                return $"Unknown color '{colorText}'. Use a color name or #RRGGBB.";
            rgb = parsed;
        }

        var requested = new List<string> { $"state=\"{normalizedState}\"" };
        if (normalizedState == "on")
        {
            if (rgb is not null)
                requested.Add($"rgb_color={HomeAssistantColor.FormatRgb(rgb)}");
            if (brightness.HasValue)
                requested.Add($"brightness={Math.Clamp(brightness.Value, 0, 255)}");
        }
        _trace.Record($"🏠 control_light(entity=\"{entityId}\", {string.Join(", ", requested)})");

        if (normalizedState == "off")
        {
            var offResult = await _ha.CallServiceAsync("light", "turn_off", new { entity_id = entityId });
            TraceResult(offResult);
            return await ConfirmLightChangeAsync(entityId, offResult, "off", null, null);
        }

        if (normalizedState != "on")
            return "State must be 'on' or 'off'.";

        var serviceData = new Dictionary<string, object?> { ["entity_id"] = entityId };

        if (rgb is not null)
            serviceData["rgb_color"] = rgb;

        if (brightness.HasValue)
            serviceData["brightness"] = Math.Clamp(brightness.Value, 0, 255);

        _trace.Record($"🏠 control_light_payload({JsonSerializer.Serialize(serviceData)})");

        var onResult = await _ha.CallServiceAsync("light", "turn_on", serviceData);
        TraceResult(onResult);
        return await ConfirmLightChangeAsync(entityId, onResult, "on", rgb, brightness);
    }

    /// <summary>
    /// HA integrations can accept a service call and still report the old state. Verify
    /// the requested color/brightness, poll once for lag, then retry once with the
    /// current brightness (or transition 0) before telling the model it is unconfirmed.
    /// </summary>
    private async Task<string> ConfirmLightChangeAsync(
        string entityId,
        string result,
        string expectedState,
        int[]? rgb,
        int? brightness)
    {
        if (HomeAssistantResult.IsFailure(result))
            return result;

        var expected = new List<string> { $"is {expectedState}" };
        if (rgb is not null)
            expected.Add($"rgb_color={HomeAssistantColor.FormatRgb(rgb)}");
        if (brightness.HasValue)
            expected.Add($"brightness={Math.Clamp(brightness.Value, 0, 255)}");

        if (Matches(result, expected))
            return result;

        var state = await _ha.GetEntityStateAsync(entityId);
        if (Matches(state, expected))
        {
            _trace.Record($"   ✅ confirmed: {Truncate(state.Replace('\n', ' '), 400)}");
            return result + "\nConfirmed state:\n" + state;
        }

        await Task.Delay(300);

        state = await _ha.GetEntityStateAsync(entityId);
        if (Matches(state, expected))
        {
            _trace.Record($"   ✅ confirmed after delay: {Truncate(state.Replace('\n', ' '), 400)}");
            return result + "\nConfirmed state:\n" + state;
        }

        var retryData = new Dictionary<string, object?> { ["entity_id"] = entityId };
        if (expectedState == "on")
        {
            if (rgb is not null)
                retryData["rgb_color"] = rgb;
            if (brightness.HasValue)
                retryData["brightness"] = Math.Clamp(brightness.Value, 0, 255);
            else if (TryReadBrightness(state, out var currentBrightness) && currentBrightness > 0)
                retryData["brightness"] = currentBrightness;
            else
                retryData["transition"] = 0;
        }
        else
        {
            retryData["transition"] = 0;
        }

        _trace.Record($"🏠 control_light_retry({JsonSerializer.Serialize(retryData)})");
        var retryResult = await _ha.CallServiceAsync(
            "light", expectedState == "on" ? "turn_on" : "turn_off", retryData);
        TraceResult(retryResult);

        var retryState = await _ha.GetEntityStateAsync(entityId);
        if (Matches(retryState, expected))
        {
            _trace.Record($"   ✅ confirmed after retry: {Truncate(retryState.Replace('\n', ' '), 400)}");
            return retryResult + "\nConfirmed state:\n" + retryState;
        }

        return $"{retryResult}\nRequested {string.Join(", ", expected)}; HA reports:\n{retryState}\nThe change was not confirmed.";
    }

    private static bool Matches(string text, IReadOnlyList<string> expected) =>
        !string.IsNullOrWhiteSpace(text) &&
        expected.All(e => text.Contains(e, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadBrightness(string state, out int brightness)
    {
        brightness = 0;
        var match = Regex.Match(state, @"\bbrightness=(\d{1,3})\b");
        return match.Success && int.TryParse(match.Groups[1].Value, out brightness);
    }

    [Description(
        "Control a media_player. volume_percent is 0-100. ListEntities(domain='media_player') if entity_id is unknown.")]
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
        return result;
    }

    [Description(
        "Climate/HVAC. Do not set dangerous temperatures. ListEntities(domain='climate') if entity_id is unknown.")]
    private async Task<string> ControlClimate(
        [Description("Climate entity_id, e.g. 'climate.living_room'")] string entityId,
        [Description("Action: set_temperature, set_hvac_mode, off, or on")] string action,
        [Description("Target temperature when action is set_temperature")] double? temperature = null,
        [Description("HVAC mode when action is set_hvac_mode: heat, cool, heat_cool, auto, off, fan_only, dry")] string? hvacMode = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return "Entity ID is required. Call ListEntities(domain='climate') first.";

        var act = (action ?? "").Trim().ToLowerInvariant();
        _trace.Record($"🏠 control_climate(entity=\"{entityId}\", action=\"{act}\", temp={temperature?.ToString() ?? ""})");

        string service;
        var data = new Dictionary<string, object?> { ["entity_id"] = entityId.Trim() };
        switch (act)
        {
            case "set_temperature":
            case "temperature":
                if (!temperature.HasValue)
                    return "temperature is required when action is set_temperature.";
                service = "set_temperature";
                data["temperature"] = temperature.Value;
                break;
            case "set_hvac_mode":
            case "hvac_mode":
            case "mode":
                if (string.IsNullOrWhiteSpace(hvacMode))
                    return "hvac_mode is required when action is set_hvac_mode.";
                service = "set_hvac_mode";
                data["hvac_mode"] = hvacMode.Trim().ToLowerInvariant();
                break;
            case "off":
            case "turn_off":
                service = "turn_off";
                break;
            case "on":
            case "turn_on":
                service = "turn_on";
                break;
            default:
                return "Action must be one of: set_temperature, set_hvac_mode, off, on.";
        }

        var result = await _ha.CallServiceAsync("climate", service, data);
        TraceResult(result);
        return result;
    }

    [Description(
        "Cover/blinds/garage. Garage doors only when the user named the door. ListEntities(domain='cover') if unknown.")]
    private async Task<string> ControlCover(
        [Description("Cover entity_id, e.g. 'cover.garage_door'")] string entityId,
        [Description("Action: open, close, stop, or set_position")] string action,
        [Description("Position 0-100 when action is set_position (0=closed, 100=open)")] int? position = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return "Entity ID is required. Call ListEntities(domain='cover') first.";

        var act = (action ?? "").Trim().ToLowerInvariant();
        _trace.Record($"🏠 control_cover(entity=\"{entityId}\", action=\"{act}\")");

        string service;
        var data = new Dictionary<string, object?> { ["entity_id"] = entityId.Trim() };
        switch (act)
        {
            case "open":
            case "open_cover":
                service = "open_cover";
                break;
            case "close":
            case "close_cover":
                service = "close_cover";
                break;
            case "stop":
            case "stop_cover":
                service = "stop_cover";
                break;
            case "set_position":
            case "position":
                if (!position.HasValue)
                    return "position (0-100) is required when action is set_position.";
                service = "set_cover_position";
                data["position"] = Math.Clamp(position.Value, 0, 100);
                break;
            default:
                return "Action must be one of: open, close, stop, set_position.";
        }

        var result = await _ha.CallServiceAsync("cover", service, data);
        TraceResult(result);
        return result;
    }

    [Description("Activate a scene. ListEntities(domain='scene') if entity_id is unknown.")]
    private async Task<string> ActivateScene(
        [Description("Scene entity_id, e.g. 'scene.movie_time'")] string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return "Entity ID is required. Call ListEntities(domain='scene') first.";

        _trace.Record($"🏠 activate_scene(entity=\"{entityId}\")");
        var result = await _ha.CallServiceAsync("scene", "turn_on", new { entity_id = entityId.Trim() });
        TraceResult(result);
        return result;
    }

    [Description("Run a named script only. ListEntities(domain='script') if entity_id is unknown.")]
    private async Task<string> RunScript(
        [Description("Script entity_id, e.g. 'script.good_night'")] string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return "Entity ID is required. Call ListEntities(domain='script') first.";

        _trace.Record($"🏠 run_script(entity=\"{entityId}\")");
        var result = await _ha.CallServiceAsync("script", "turn_on", new { entity_id = entityId.Trim() });
        TraceResult(result);
        return result;
    }

    [Description("Current state of one entity_id, with key attributes such as brightness, color, volume, or temperature.")]
    private async Task<string> GetEntityState(
        [Description("Entity ID, e.g. 'light.kitchen', 'media_player.living_room', 'sensor.temperature'")] string entityId)
    {
        _trace.Record($"🏠 get_entity_state(entity=\"{entityId}\")");

        var state = await _ha.GetEntityStateAsync(entityId);
        TraceResult(state);
        return state;
    }

    [Description(
        "Call any HA service. Always include entity_id in service_data JSON. " +
        "Ask first for locks, garage, alarms, unnamed scripts, or extreme climate. ListEntities if entity_id is unknown.")]
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

    [Description(
        "HA Assist for area phrases like 'turn off kitchen lights'. If it fails, ListEntities then ControlLight/CallService. " +
        "Refuse harm to a person; confirm locks, garage, alarms, unnamed scripts.")]
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
        HomeAssistantResult.IsFailure(result);

    private static bool IsSuccess(string? result) => !IsFailure(result);

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? "";
        return text[..max] + "…";
    }
}
