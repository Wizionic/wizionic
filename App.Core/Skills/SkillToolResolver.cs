namespace App.Core.Skills;

/// <summary>
/// Maps official <c>allowed-tools</c> tokens to Wizionic <see cref="IToolModule"/> names
/// and whether to include MCP / OpenAPI connector tools.
/// </summary>
public static class SkillToolResolver
{
    public sealed record Resolution(
        IReadOnlyList<string> Modules,
        bool IncludeMcp,
        IReadOnlyList<string> UnknownTokens);

    private static readonly Dictionary<string, string> TokenToModule = new(StringComparer.OrdinalIgnoreCase)
    {
        // Modules
        ["gallery"] = "Gallery",
        ["notes"] = "Notes",
        ["calendar"] = "Calendar",
        ["lemonade"] = "Lemonade",
        ["native"] = "Native",
        ["homeassistant"] = "HomeAssistant",
        ["home-assistant"] = "HomeAssistant",
        ["ha"] = "HomeAssistant",
        ["browser"] = "BrowserAgent",
        ["browseragent"] = "BrowserAgent",

        // Gallery functions
        ["list_gallery_albums"] = "Gallery",
        ["list_recent_chat_images"] = "Gallery",
        ["save_to_gallery"] = "Gallery",

        // Notes functions
        ["list_notebooks"] = "Notes",
        ["list_note_entries"] = "Notes",
        ["create_notebook"] = "Notes",
        ["add_note_entry"] = "Notes",
        ["append_to_note_entry"] = "Notes",
        ["add_note"] = "Notes",
        ["list_notes"] = "Notes",

        // Calendar functions
        ["list_calendars"] = "Calendar",
        ["list_events"] = "Calendar",
        ["add_calendar_event"] = "Calendar",
        ["update_calendar_event"] = "Calendar",
        ["delete_calendar_event"] = "Calendar",

        // Native
        ["search_web"] = "Native",
        ["summarize_url"] = "Native",
        ["get_time"] = "Native",
        ["calculate"] = "Native",
        ["get_current_weather"] = "Native",

        // HA (common tool names)
        ["listentities"] = "HomeAssistant",
        ["listlights"] = "HomeAssistant",
        ["controllight"] = "HomeAssistant",
        ["controlmediaplayer"] = "HomeAssistant",
        ["getentitystate"] = "HomeAssistant",
        ["callservice"] = "HomeAssistant",
        ["listservices"] = "HomeAssistant",
        ["processconversation"] = "HomeAssistant",
    };

    /// <summary>
    /// When allowed-tools is empty: all common client modules + MCP (flexible default).
    /// </summary>
    public static readonly string[] DefaultModules =
    [
        "Native", "Gallery", "Notes", "Calendar", "Lemonade", "HomeAssistant", "BrowserAgent"
    ];

    public static Resolution Resolve(string? allowedTools)
    {
        if (string.IsNullOrWhiteSpace(allowedTools))
            return new Resolution(DefaultModules, IncludeMcp: true, Array.Empty<string>());

        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        var includeMcp = false;

        foreach (var raw in allowedTools.Split(new[] { ' ', '\t', '\r', '\n', ',' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim();
            if (token is "*" or "all" or "mcp" or "MCP" or "connectors" or "oauth")
            {
                includeMcp = true;
                continue;
            }

            // GitHub (and other) OpenAPI connector tool names ride the MCP/connector bag.
            if (token.Equals("github", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("github_", StringComparison.OrdinalIgnoreCase))
            {
                includeMcp = true;
                continue;
            }

            if (TokenToModule.TryGetValue(token, out var mod) ||
                TokenToModule.TryGetValue(token.Replace("-", ""), out mod))
            {
                modules.Add(mod);
                continue;
            }

            // Pascal/camel module names as-is
            if (token is "Gallery" or "Notes" or "Calendar" or "Lemonade" or "Native"
                or "HomeAssistant" or "BrowserAgent")
            {
                modules.Add(token);
                continue;
            }

            // Unknown → treat as MCP/connector tool name
            includeMcp = true;
            unknown.Add(token);
        }

        if (modules.Count == 0 && includeMcp)
            return new Resolution(Array.Empty<string>(), true, unknown);

        if (modules.Count == 0)
            return new Resolution(DefaultModules, IncludeMcp: true, unknown);

        return new Resolution(modules.ToList(), includeMcp, unknown);
    }

    /// <summary>Known tokens for the Skill Creator multi-select.</summary>
    public static IReadOnlyList<(string Token, string Label, string Group)> Catalog { get; } =
    [
        ("Gallery", "Gallery (all)", "Gallery"),
        ("list_gallery_albums", "list_gallery_albums", "Gallery"),
        ("save_to_gallery", "save_to_gallery", "Gallery"),
        ("list_recent_chat_images", "list_recent_chat_images", "Gallery"),
        ("Notes", "Notes (all)", "Notes"),
        ("list_notebooks", "list_notebooks", "Notes"),
        ("create_notebook", "create_notebook", "Notes"),
        ("add_note_entry", "add_note_entry", "Notes"),
        ("append_to_note_entry", "append_to_note_entry", "Notes"),
        ("list_note_entries", "list_note_entries", "Notes"),
        ("Calendar", "Calendar (all)", "Calendar"),
        ("list_calendars", "list_calendars", "Calendar"),
        ("list_events", "list_events", "Calendar"),
        ("add_calendar_event", "add_calendar_event", "Calendar"),
        ("update_calendar_event", "update_calendar_event", "Calendar"),
        ("delete_calendar_event", "delete_calendar_event", "Calendar"),
        ("Lemonade", "Lemonade (image/STT/TTS)", "Local AI"),
        ("Native", "Native (search, weather, time)", "Native"),
        ("search_web", "search_web", "Native"),
        ("get_current_weather", "get_current_weather", "Native"),
        ("HomeAssistant", "Home Assistant (all)", "Home Assistant"),
        ("ListEntities", "ListEntities", "Home Assistant"),
        ("ControlLight", "ControlLight", "Home Assistant"),
        ("CallService", "CallService", "Home Assistant"),
        ("BrowserAgent", "Embedded browser", "Browser"),
        ("MCP", "MCP + OAuth connectors (enabled)", "MCP"),
        ("github_get_user", "github_get_user", "GitHub"),
        ("github_list_repos", "github_list_repos", "GitHub"),
        ("github_list_issues", "github_list_issues", "GitHub"),
        ("github_create_issue", "github_create_issue", "GitHub"),
    ];
}
