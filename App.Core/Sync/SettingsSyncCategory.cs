namespace App.Core.Sync;

/// <summary>
/// Stable category ids for settings blobs transferred over WebRTC.
/// Login server / auth config is intentionally never a category.
/// Workflows are device-local and are not a settings sync category.
/// </summary>
public static class SettingsSyncCategory
{
    public const string LocalAi = "local-ai";
    public const string Lemonade = "lemonade";
    public const string CloudProviders = "cloud-providers";
    public const string ModelProfiles = "model-profiles";
    public const string HomeAssistant = "home-assistant";
    public const string Tools = "tools";
    public const string SystemPrompt = "system-prompt";
    public const string Profile = "profile";
    public const string Memories = "memories";
    public const string Appearance = "appearance";
    public const string Skills = "skills";

    public static readonly string[] All =
    [
        LocalAi,
        Lemonade,
        CloudProviders,
        ModelProfiles,
        HomeAssistant,
        Tools,
        SystemPrompt,
        Profile,
        Memories,
        Appearance,
        Skills
    ];

    public static string DisplayName(string category) => category switch
    {
        LocalAi => "Local AI",
        Lemonade => "Lemonade",
        CloudProviders => "Cloud Providers",
        ModelProfiles => "Model profiles",
        HomeAssistant => "Home Assistant",
        Tools => "Tools (MCP + Connectors)",
        SystemPrompt => "System prompt",
        Profile => "About you",
        Memories => "Memories",
        Appearance => "Appearance",
        Skills => "Skills (SKILL.md)",
        _ => category
    };
}
