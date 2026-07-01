using System.ComponentModel;
using System.Text.Json;
using ChatfishApp.Core.SmartHome;
using ChatfishApp.Core.Tools;
using ChatfishApp.Shared.Services.Tools;
using Microsoft.Extensions.AI;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Home Assistant tools for controlling lights, scenes, and querying entity state.
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
            AIFunctionFactory.Create(ListLights),
            AIFunctionFactory.Create(ControlLight),
            AIFunctionFactory.Create(GetEntityState),
            AIFunctionFactory.Create(CallService)
        ];
    }

    public IReadOnlyList<AITool> GetTools() => IsAvailable ? _tools : [];

    [Description("List all Home Assistant light entities with their entity_id and friendly name. Call this first when you need to find the correct entity_id for a room name like 'kitchen'.")]
    private async Task<string> ListLights()
    {
        _trace.Record("🏠 list_lights()");
        var result = await _ha.ListLightEntitiesAsync();
        var preview = result.Length > 400 ? result[..400] + "…" : result;
        _trace.Record($"   {(result.StartsWith("HA error", StringComparison.OrdinalIgnoreCase) || result.StartsWith("Connection", StringComparison.OrdinalIgnoreCase) ? "❌" : "✅")} {preview}");
        return result;
    }

    [Description("Control a Home Assistant light entity. Can turn on/off, set brightness, or change color.")]
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
            _trace.Record($"   {(result == "OK" ? "✅" : "❌")} {result}");
            return result == "OK" ? $"Turned off {entityId}." : result;
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
        _trace.Record($"   {(onResult == "OK" ? "✅" : "❌")} {onResult}");
        return onResult == "OK" ? $"Turned on {entityId}." : onResult;
    }

    [Description("Get the current state of a Home Assistant entity (light, switch, sensor, etc.).")]
    private async Task<string> GetEntityState(
        [Description("Entity ID, e.g. 'light.kitchen' or 'sensor.temperature'")] string entityId)
    {
        _trace.Record($"🏠 get_entity_state(entity=\"{entityId}\")");

        var json = await _ha.GetEntityStateAsync(entityId);
        if (json.StartsWith("HA error", StringComparison.OrdinalIgnoreCase) ||
            json.StartsWith("Home Assistant", StringComparison.OrdinalIgnoreCase))
        {
            _trace.Record($"   ❌ {json}");
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
            _trace.Record($"   ✅ returned state JSON");
            return json;
        }
    }

    [Description("Call any Home Assistant service. Use for scenes, switches, climate, etc. service_data is a JSON object string.")]
    private async Task<string> CallService(
        [Description("Service domain, e.g. 'light', 'scene', 'switch', 'climate'")] string domain,
        [Description("Service name, e.g. 'turn_on', 'turn_off', 'activate'")] string service,
        [Description("JSON object for service data, e.g. {\"entity_id\": \"scene.movie_time\"}")] string serviceDataJson = "{}")
    {
        _trace.Record($"🏠 call_service({domain}.{service})");

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
        _trace.Record($"   {(result == "OK" ? "✅" : "❌")} {result}");
        return result == "OK" ? $"Called {domain}.{service} successfully." : result;
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