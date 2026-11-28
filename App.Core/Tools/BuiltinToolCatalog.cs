namespace App.Core.Tools;

/// <summary>
/// How much routing ambiguity a built-in tool adds. Hybrid routing can skip the
/// router model when only <see cref="BuiltinToolTier.Deterministic"/> tools are on.
/// </summary>
public enum BuiltinToolTier
{
    /// <summary>Literal intent keywords are reliable; no model round trip needed.</summary>
    Deterministic = 0,

    /// <summary>Mostly reliable, but paraphrases ("is it going to rain?") benefit from a router.</summary>
    SemiAmbiguous = 1,

    /// <summary>Contextual intent (device control, embedded browser) benefits from a router.</summary>
    Ambiguous = 2
}

/// <summary>
/// One user-toggleable built-in capability. <see cref="Functions"/> is empty for
/// whole-module groups; otherwise it lists the <c>AIFunction</c> names it gates.
/// </summary>
public sealed record BuiltinToolDescriptor(
    string Id,
    string Module,
    IReadOnlyList<string> Functions,
    BuiltinToolTier Tier,
    string Title);

/// <summary>
/// Stable ids and module/function mapping for the built-in tools shown on the Tools page.
/// Storage keeps the <b>disabled</b> ids so new tools default to enabled.
/// </summary>
public static class BuiltinToolCatalog
{
    public const string WebSearch = "web.search";
    public const string WebSummarize = "web.summarize";
    public const string Weather = "weather";
    public const string Time = "time";
    public const string Calculator = "calculator";
    public const string Notes = "notes";
    public const string Gallery = "gallery";
    public const string Calendar = "calendar";
    public const string Pdf = "pdf";
    public const string LocalImages = "images.local";
    public const string CloudImages = "images.cloud";
    public const string Browser = "browser";
    public const string HomeAssistant = "homeassistant";

    private static readonly IReadOnlyList<BuiltinToolDescriptor> Catalog =
    [
        new(WebSearch, "Native", ["SearchWeb"], BuiltinToolTier.SemiAmbiguous, "Web search"),
        new(WebSummarize, "Native", ["SummarizeUrl"], BuiltinToolTier.SemiAmbiguous, "Summarize URL"),
        new(Weather, "Native", ["GetCurrentWeather"], BuiltinToolTier.SemiAmbiguous, "Weather"),
        new(Time, "Native", ["GetCurrentTimeUtc"], BuiltinToolTier.Deterministic, "Time"),
        new(Calculator, "Native", ["Calculate"], BuiltinToolTier.Deterministic, "Calculator"),
        new(Notes, "Notes", [], BuiltinToolTier.Deterministic, "Notes"),
        new(Gallery, "Gallery", [], BuiltinToolTier.Deterministic, "Gallery"),
        new(Calendar, "Calendar", [], BuiltinToolTier.Deterministic, "Calendar"),
        new(Pdf, "Pdf", [], BuiltinToolTier.Deterministic, "PDF"),
        new(LocalImages, "Lemonade", [], BuiltinToolTier.SemiAmbiguous, "Lemonade"),
        new(CloudImages, "Cloud", [], BuiltinToolTier.SemiAmbiguous, "Cloud images"),
        new(Browser, "BrowserAgent", [], BuiltinToolTier.Ambiguous, "Web browser"),
        new(HomeAssistant, "HomeAssistant", [], BuiltinToolTier.Ambiguous, "Home Assistant")
    ];

    public static IReadOnlyList<BuiltinToolDescriptor> All => Catalog;

    public static BuiltinToolDescriptor? Find(string id) =>
        Catalog.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<BuiltinToolDescriptor> ForModule(string module) =>
        Catalog.Where(t => t.Module.Equals(module, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Ids belonging to a module (used by bulk enable/disable).</summary>
    public static IReadOnlyList<string> IdsForModule(string module) =>
        ForModule(module).Select(t => t.Id).ToList();
}
