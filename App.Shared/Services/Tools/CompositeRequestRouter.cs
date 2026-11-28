using App.Core.Storage;
using App.Core.Tools;

namespace App.Shared.Services.Tools;

/// <summary>
/// Settings-driven router: Rules, AI, or Hybrid (rules first; AI when rules are weak).
/// Registered as the app-wide <see cref="IRequestRouter"/>.
/// </summary>
public sealed class CompositeRequestRouter : IRequestRouter
{
    private readonly ContextualRequestRouter _rules;
    private readonly AiRequestRouter _ai;
    private readonly IKeyStore _keyStore;
    private readonly BuiltinToolGate _gate;

    public CompositeRequestRouter(
        ContextualRequestRouter rules,
        AiRequestRouter ai,
        IKeyStore keyStore,
        BuiltinToolGate gate)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public async Task<RequestRoute> ClassifyRequestAsync(
        string message,
        IReadOnlyList<IToolModule> activeModules,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        var mode = _keyStore.ToolRoutingMode;
        var hasModel = !string.IsNullOrWhiteSpace(_keyStore.ToolRoutingModelId);

        // No model configured → always rules
        if (mode is ToolRoutingMode.Ai or ToolRoutingMode.Hybrid && !hasModel)
            mode = ToolRoutingMode.Rules;

        // Rules-only: session stickiness helps "make it brighter" without the wake word.
        // AI/Hybrid: no sticky HA session — re-classify every turn so weather after lights works.
        var useStickiness = mode == ToolRoutingMode.Rules;
        var rulesRoute = _rules.ClassifyRules(
            message, activeModules, conversationId, useSessionStickiness: useStickiness);

        // Explicit skill invoke (/name, /skill name, run skill …) always wins over AI router.
        if (!string.IsNullOrWhiteSpace(rulesRoute.SkillId))
            return rulesRoute with { Source = mode == ToolRoutingMode.Rules ? "Rules" : $"{mode}→Skill" };

        // Clear Home Assistant action with HA turned off: no router call can add a tool.
        if (!rulesRoute.HasTools
            && !_gate.IsModuleEnabled("HomeAssistant")
            && ContextualRequestRouter.MessageLooksLikeHomeAssistantCommand(message))
            return rulesRoute with { Source = "Rules (Home Assistant disabled)" };

        // Everything the user could enable is off: chat with no tool overhead at all.
        if (!activeModules.Any(m => m.IsAvailable && _gate.IsModuleEnabled(m.ModuleName)))
            return RequestRoute.PureChat("no built-in tools enabled", "Rules");

        if (mode == ToolRoutingMode.Rules)
            return rulesRoute;

        if (mode == ToolRoutingMode.Ai)
            return await _ai.ClassifyAsync(message, activeModules, conversationId, "AI", ct);

        // Hybrid: when only predictable tools are enabled, a router-model call cannot
        // add useful discrimination — keep the zero-cost rules result.
        if (!_gate.HasContextualToolsEnabled(activeModules
                .Where(m => m.IsAvailable && _gate.IsModuleEnabled(m.ModuleName))
                .Select(m => m.ModuleName)))
            return rulesRoute with { Source = "Hybrid→Rules (predictable tools)" };

        // Hybrid: wake-word HA / browser panel / clear Lemonade stay on rules (TTFT).
        // PureChat, Gallery-only, Native-only, or smart-home without wake word → AI.
        if (IsStrongRulesRoute(rulesRoute))
            return rulesRoute with { Source = "Hybrid→Rules" };

        return await _ai.ClassifyAsync(message, activeModules, conversationId, "Hybrid", ct);
    }

    /// <summary>
    /// Strong enough that Hybrid should not pay for an AI call.
    /// Wake-word HA is strong; PureChat is not (AI / heuristic may open HA without wake word).
    /// </summary>
    internal static bool IsStrongRulesRoute(RequestRoute route)
    {
        // Explicit skill slash / run skill …
        if (!string.IsNullOrWhiteSpace(route.SkillId))
            return true;

        // Wake word or browser panel (not sticky session — stickiness disabled for Hybrid).
        if (route.TargetModule is "HomeAssistant" or "BrowserAgent")
            return true;

        var mods = route.Modules;
        if (mods.Count == 0)
            return false; // PureChat → try AI (incl. smart-home without wake word)

        if (mods.Any(m =>
                m.Equals("Cloud", StringComparison.OrdinalIgnoreCase) ||
                m.Equals("Lemonade", StringComparison.OrdinalIgnoreCase)))
            return true; // clear image path

        // Gallery-only or Native-only can miss multi-intent → AI
        return false;
    }
}
