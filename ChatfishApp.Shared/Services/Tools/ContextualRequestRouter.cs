using System.Text.RegularExpressions;
using ChatfishApp.Core.Storage;
using ChatfishApp.Core.Tools;
using ChatfishApp.Core.UI;

namespace ChatfishApp.Shared.Services.Tools;

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