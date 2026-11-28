using App.Core.Storage;
using App.Core.Tools;
using Microsoft.Extensions.AI;

namespace App.Shared.Services.Tools;

/// <summary>
/// Hard gate for user-disabled built-in tools. A disabled tool is excluded from the
/// active module set, the router prompt, and the <see cref="AITool"/> definitions sent
/// to the chat model.
/// </summary>
public sealed class BuiltinToolGate
{
    private readonly IKeyStore _keys;

    public BuiltinToolGate(IKeyStore keys)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    public bool IsToolEnabled(string id) => _keys.IsBuiltinToolEnabled(id);

    public bool IsModuleEnabled(string module)
    {
        var tools = BuiltinToolCatalog.ForModule(module);
        if (tools.Count == 0)
            return true; // Not a user-toggleable built-in (MCP/connector handled elsewhere).

        return tools.Any(t => IsToolEnabled(t.Id));
    }

    /// <summary>
    /// A function is enabled when at least one enabled descriptor covers it. Modules with
    /// no function list are all-or-nothing. Unknown function names follow the module.
    /// </summary>
    public bool IsFunctionEnabled(string module, string functionName)
    {
        var tools = BuiltinToolCatalog.ForModule(module);
        if (tools.Count == 0)
            return true;

        var covering = tools
            .Where(t => t.Functions.Count == 0 ||
                        t.Functions.Contains(functionName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (covering.Count == 0)
            return IsModuleEnabled(module);

        return covering.Any(t => IsToolEnabled(t.Id));
    }

    /// <summary>Filters AI functions in a module; non-function tools pass through.</summary>
    public IEnumerable<AITool> FilterTools(string module, IEnumerable<AITool> tools)
    {
        foreach (var tool in tools)
        {
            if (tool is AIFunction fn && !IsFunctionEnabled(module, fn.Name))
                continue;
            yield return tool;
        }
    }

    public IReadOnlyList<string> FilterModules(IEnumerable<string> modules) =>
        modules
            .Where(m => !string.IsNullOrWhiteSpace(m) && IsModuleEnabled(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// True when a SemiAmbiguous or Ambiguous tool is enabled <b>and its module is active</b>
    /// (configured on this device). Hybrid uses this to decide whether a router-model call
    /// can add value at all.
    /// </summary>
    public bool HasContextualToolsEnabled(IEnumerable<string> activeModules)
    {
        var active = activeModules.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return BuiltinToolCatalog.All.Any(t =>
            t.Tier != BuiltinToolTier.Deterministic &&
            active.Contains(t.Module) &&
            IsToolEnabled(t.Id));
    }

    /// <summary>Native capability text for the router prompt, limited to enabled functions.</summary>
    public IReadOnlyList<string> EnabledNativeCapabilities()
    {
        var list = new List<string>();
        if (IsFunctionEnabled("Native", "SearchWeb")) list.Add("search the web");
        if (IsFunctionEnabled("Native", "SummarizeUrl")) list.Add("read a specific URL");
        if (IsFunctionEnabled("Native", "GetCurrentWeather")) list.Add("weather");
        if (IsFunctionEnabled("Native", "GetCurrentTimeUtc")) list.Add("current time");
        if (IsFunctionEnabled("Native", "Calculate")) list.Add("math");
        return list;
    }

    public bool HasEnabledNativeTools => EnabledNativeCapabilities().Count > 0;

    /// <summary>Friendly names of user-disabled built-ins, for the chat system prompt.</summary>
    public IReadOnlyList<string> DisabledToolNames() =>
        BuiltinToolCatalog.All
            .Where(t => !IsToolEnabled(t.Id))
            .Select(t => t.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
