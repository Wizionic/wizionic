using System.Text.RegularExpressions;
using ChatfishApp.Core.Storage;
using ChatfishApp.Core.Tools;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Routes by wake word and per-conversation session context. Designed as a swap point for a future intent model.
/// </summary>
public sealed class ContextualRequestRouter : IRequestRouter
{
    public static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(15);

    private readonly IKeyStore _keyStore;
    private readonly IRoutingSessionStore _sessions;

    public ContextualRequestRouter(IKeyStore keyStore, IRoutingSessionStore sessions)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
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

        if (activeModules.Any(m => m.ModuleName == "BrowserAgent" && m.IsAvailable))
        {
            // Future: browser wake word from BrowserAgentConfig
        }

        return new RequestRoute(RouteType.ToolAssistedChat);
    }

    public static bool ShouldEnforceHomeAssistantTools(RequestRoute? route) =>
        route?.TargetModule == "HomeAssistant";

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