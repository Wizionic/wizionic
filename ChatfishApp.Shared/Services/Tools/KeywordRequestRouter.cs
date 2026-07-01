using ChatfishApp.Core.Storage;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Simple keyword-based request router. Replace with a local routing model later.
/// </summary>
public sealed class KeywordRequestRouter : IRequestRouter
{
    private static readonly string[] HomeAssistantKeywords =
    [
        "light", "lights", "lamp", "bulb", "bulbs",
        "turn on", "turn off", "turn up", "turn down",
        "thermostat", "temperature", "climate", "home assistant", "homeassistant",
        "kitchen", "bedroom", "living room", "bathroom", "hall", "office", "garage",
        "scene", "switch", "fan", "cover", "blinds", "lock",
        "brightness", "dim", "dimmer", "brighter"
    ];

    private static readonly string[] HomeAssistantFollowUpKeywords =
    [
        "blue", "bluish", "red", "green", "purple", "pink", "pinkish", "orange",
        "yellow", "white", "warm", "cool", "cooler", "warmer",
        "brighter", "dimmer", "darker", "lighter",
        "make it", "adjust", "change it", "set it", "set the", "change the",
        "more ", "less ", "still looks", "looks pink", "looks blue", "looks red",
        "too bright", "too dim", "percent", "%"
    ];

    public RequestRoute ClassifyRequest(
        string message,
        IReadOnlyList<IToolModule> activeModules,
        IReadOnlyList<ChatMessage>? conversation = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new RequestRoute(RouteType.ToolAssistedChat);

        var lower = message.ToLowerInvariant();
        var haAvailable = activeModules.Any(m => m.ModuleName == "HomeAssistant" && m.IsAvailable);

        if (haAvailable)
        {
            if (ContainsAny(lower, HomeAssistantKeywords))
                return new RequestRoute(RouteType.ToolAssistedChat, "HomeAssistant");

            if (conversation != null &&
                HasRecentHomeAssistantContext(conversation) &&
                ContainsAny(lower, HomeAssistantFollowUpKeywords))
            {
                return new RequestRoute(RouteType.ToolAssistedChat, "HomeAssistant");
            }
        }

        if (activeModules.Any(m => m.ModuleName == "BrowserAgent" && m.IsAvailable))
        {
            if (ContainsAny(lower, "browse", "navigate", "click", "fill in", "open the page", "web page", "website"))
                return new RequestRoute(RouteType.ToolAssistedChat, "BrowserAgent");
        }

        return new RequestRoute(RouteType.ToolAssistedChat);
    }

    public static bool ShouldEnforceHomeAssistantTools(
        string message,
        IReadOnlyList<IToolModule> activeModules,
        IReadOnlyList<ChatMessage>? conversation)
    {
        if (!activeModules.Any(m => m.ModuleName == "HomeAssistant" && m.IsAvailable))
            return false;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lower = message.ToLowerInvariant();
        if (ContainsAny(lower, HomeAssistantKeywords))
            return true;

        return conversation != null &&
               HasRecentHomeAssistantContext(conversation) &&
               ContainsAny(lower, HomeAssistantFollowUpKeywords);
    }

    private static bool HasRecentHomeAssistantContext(IReadOnlyList<ChatMessage> conversation)
    {
        var start = Math.Max(0, conversation.Count - 12);
        for (int i = conversation.Count - 1; i >= start; i--)
        {
            var msg = conversation[i];
            if (!string.IsNullOrWhiteSpace(msg.ToolTrace) &&
                (msg.ToolTrace.Contains("🏠", StringComparison.Ordinal) ||
                 msg.ToolTrace.Contains("control_light", StringComparison.OrdinalIgnoreCase) ||
                 msg.ToolTrace.Contains("list_lights", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var text = (msg.Content ?? "").ToLowerInvariant();
            if (ContainsAny(text, HomeAssistantKeywords))
                return true;
        }

        return false;
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}