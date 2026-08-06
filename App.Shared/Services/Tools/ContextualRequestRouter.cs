using System.Text.RegularExpressions;
using App.Core.Storage;
using App.Core.Tools;
using App.Core.UI;

namespace App.Shared.Services.Tools;

/// <summary>
/// Routes by wake word and per-conversation session context. Designed as a swap point for a future intent model.
/// </summary>
public sealed class ContextualRequestRouter : IRequestRouter
{
    public static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(15);

    private readonly IKeyStore _keyStore;
    private readonly IRoutingSessionStore _sessions;
    private readonly IBrowserPanelState _browserPanel;

    public ContextualRequestRouter(
        IKeyStore keyStore,
        IRoutingSessionStore sessions,
        IBrowserPanelState browserPanel)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _browserPanel = browserPanel ?? throw new ArgumentNullException(nameof(browserPanel));
    }

    public RequestRoute ClassifyRequest(
        string message,
        IReadOnlyList<IToolModule> activeModules,
        string? conversationId = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new RequestRoute(RouteType.ToolAssistedChat);

        var haAvailable = activeModules.Any(m => m.ModuleName == "HomeAssistant" && m.IsAvailable);
        if (haAvailable)
        {
            var assistantName = _keyStore.HomeAssistantAssistantName;
            var session = _sessions.Get(conversationId);

            if (ContainsWakeWord(message, assistantName) || session.IsActive("HomeAssistant", SessionTtl))
                return new RequestRoute(RouteType.ToolAssistedChat, "HomeAssistant");
        }

        if (_browserPanel.IsOpen &&
            activeModules.Any(m => m.ModuleName == "BrowserAgent" && m.IsAvailable))
        {
            return new RequestRoute(RouteType.ToolAssistedChat, "BrowserAgent");
        }

        return new RequestRoute(RouteType.ToolAssistedChat);
    }

    public static bool ShouldEnforceHomeAssistantTools(RequestRoute? route) =>
        route?.TargetModule == "HomeAssistant";

    public static bool ShouldEnforceBrowserTools(RequestRoute? route) =>
        route?.TargetModule == "BrowserAgent";

    /// <summary>
    /// Heuristic: general knowledge chat should NOT get tools (matches Lemonade pure-chat quality).
    /// Only attach Native tools when the user clearly wants search, weather, time, or math.
    /// </summary>
    public static bool MessageSuggestsUtilityTools(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var m = message.ToLowerInvariant();

        // Web / current info
        if (m.Contains("search") || m.Contains("look up") || m.Contains("lookup") ||
            m.Contains("google") || m.Contains("latest") || m.Contains("news") ||
            m.Contains("current price") || m.Contains("today's") || m.Contains("todays") ||
            m.Contains("what happened") || m.Contains("who won") || m.Contains("score"))
            return true;

        // Weather / time
        if (m.Contains("weather") || m.Contains("forecast") || m.Contains("temperature") ||
            m.Contains("what time") || m.Contains("current time") || m.Contains("utc"))
            return true;

        // Explicit math
        if (m.Contains("calculate") || m.Contains("compute") || m.Contains("what is ") &&
            (m.Contains("+") || m.Contains("*") || m.Contains("%") || m.Contains("divided")))
            return true;

        // URL present
        if (m.Contains("http://") || m.Contains("https://") || m.Contains("www."))
            return true;

        return false;
    }

    public static bool ContainsWakeWord(string message, string assistantName)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(assistantName))
            return false;

        var name = assistantName.Trim();
        if (name.Contains(' '))
            return message.Contains(name, StringComparison.OrdinalIgnoreCase);

        return WakeWordRegex(name).IsMatch(message);
    }

    private static Regex WakeWordRegex(string name)
    {
        var escaped = Regex.Escape(name);
        return new Regex($@"(?<!\w){escaped}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}