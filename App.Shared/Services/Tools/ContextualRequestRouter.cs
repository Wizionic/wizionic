using System.Text.RegularExpressions;
using App.Core.Storage;
using App.Core.Tools;
using App.Core.UI;

namespace App.Shared.Services.Tools;

/// <summary>
/// Rule-based tool router: wake word, browser panel, session, and keyword heuristics.
/// Zero model cost. Used alone (Rules mode) or as the Hybrid fast path / AI fallback.
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

    public Task<RequestRoute> ClassifyRequestAsync(
        string message,
        IReadOnlyList<IToolModule> activeModules,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Direct Rules mode (when Composite is not used): keep session stickiness.
        return Task.FromResult(ClassifyRules(message, activeModules, conversationId, useSessionStickiness: true));
    }

    /// <summary>
    /// Synchronous rules path (also used by Hybrid / AI fallback).
    /// <paramref name="useSessionStickiness"/>: when true (Rules mode), continue HA for ~15 min after a tool call
    /// so short follow-ups work without the wake word. When false (AI / Hybrid), only wake word forces HA —
    /// AI or smart-home heuristics re-open HA on real device commands so topic switches (e.g. weather) work.
    /// </summary>
    public RequestRoute ClassifyRules(
        string message,
        IReadOnlyList<IToolModule> activeModules,
        string? conversationId = null,
        bool useSessionStickiness = true)
    {
        var available = new HashSet<string>(
            activeModules.Where(m => m.IsAvailable).Select(m => m.ModuleName),
            StringComparer.OrdinalIgnoreCase);

        bool Has(string name) => available.Contains(name);

        if (string.IsNullOrWhiteSpace(message))
            return RequestRoute.PureChat("empty message", "Rules");

        // Hard routes: HA wake-word; optional active session (Rules mode only)
        if (Has("HomeAssistant"))
        {
            var assistantName = _keyStore.HomeAssistantAssistantName;
            var session = _sessions.Get(conversationId);
            var wake = ContainsWakeWord(message, assistantName);
            var sticky = useSessionStickiness && session.IsActive("HomeAssistant", SessionTtl);
            if (wake || sticky)
            {
                var modules = new List<string> { "HomeAssistant", "Native" };
                if (MessageSuggestsGalleryTools(message) && Has("Gallery"))
                    modules.Add("Gallery");
                if (MessageSuggestsImageTools(message) && Has("Lemonade"))
                {
                    modules.Add("Lemonade");
                    if (Has("Gallery")) modules.Add("Gallery");
                }

                return RequestRoute.WithModules(
                    modules,
                    wake
                        ? "Home Assistant wake word"
                        : "Home Assistant active session (rules stickiness)",
                    targetModule: "HomeAssistant",
                    includeMcp: true,
                    source: "Rules");
            }
        }

        // Hard route: browser panel open
        if (_browserPanel.IsOpen && Has("BrowserAgent"))
        {
            var modules = new List<string> { "BrowserAgent", "Native" };
            if (MessageSuggestsGalleryTools(message) && Has("Gallery"))
                modules.Add("Gallery");
            if (MessageSuggestsImageTools(message) && Has("Lemonade"))
            {
                modules.Add("Lemonade");
                if (Has("Gallery")) modules.Add("Gallery");
            }

            return RequestRoute.WithModules(
                modules,
                "browser panel open",
                targetModule: "BrowserAgent",
                includeMcp: true,
                source: "Rules");
        }

        var imageIntent = MessageSuggestsImageTools(message);
        var galleryIntent = MessageSuggestsGalleryTools(message);

        if (imageIntent)
        {
            var modules = new List<string> { "Native" };
            if (Has("Lemonade")) modules.Add("Lemonade");
            if (Has("Gallery")) modules.Add("Gallery");
            return RequestRoute.WithModules(
                modules,
                galleryIntent
                    ? "create/save image intent"
                    : "image intent",
                source: "Rules");
        }

        if (galleryIntent)
        {
            var modules = new List<string> { "Native" };
            if (Has("Gallery")) modules.Add("Gallery");
            return RequestRoute.WithModules(modules, "gallery intent", source: "Rules");
        }

        if (MessageSuggestsUtilityTools(message))
        {
            return RequestRoute.WithModules(
                ["Native"],
                "utility intent (search/weather/time/math)",
                source: "Rules");
        }

        return RequestRoute.PureChat("general chat — no tools", "Rules");
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

        if (m.Contains("search") || m.Contains("look up") || m.Contains("lookup") ||
            m.Contains("google") || m.Contains("latest") || m.Contains("news") ||
            m.Contains("current price") || m.Contains("today's") || m.Contains("todays") ||
            m.Contains("what happened") || m.Contains("who won") || m.Contains("score"))
            return true;

        if (m.Contains("weather") || m.Contains("forecast") || m.Contains("temperature") ||
            m.Contains("what time") || m.Contains("current time") || m.Contains("utc"))
            return true;

        if (m.Contains("calculate") || m.Contains("compute") || m.Contains("what is ") &&
            (m.Contains("+") || m.Contains("*") || m.Contains("%") || m.Contains("divided")))
            return true;

        if (m.Contains("http://") || m.Contains("https://") || m.Contains("www."))
            return true;

        return false;
    }

    /// <summary>
    /// User wants image generation/editing (attach Lemonade + Gallery tools).
    /// Also true for "create/make … and save to album" style prompts that omit the word "image".
    /// </summary>
    public static bool MessageSuggestsImageTools(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var m = message.ToLowerInvariant();

        if (m.Contains("generate") && (m.Contains("image") || m.Contains("picture") || m.Contains("photo")
                                       || m.Contains("illustration") || m.Contains("drawing") || m.Contains("artwork"))
            || m.Contains("draw ") || m.Contains("draw a") || m.Contains("draw me") || m.Contains("draw an")
            || m.Contains("illustrate") || m.Contains("paint ") || m.Contains("paint a") || m.Contains("paint me")
            || m.Contains("create an image") || m.Contains("create a picture") || m.Contains("create a photo")
            || m.Contains("create an illustration") || m.Contains("create a drawing")
            || m.Contains("make an image") || m.Contains("make a picture") || m.Contains("make a photo")
            || m.Contains("edit the image") || m.Contains("edit this image") || m.Contains("edit image")
            || m.Contains("img2img") || m.Contains("text-to-image") || m.Contains("text to image")
            || m.Contains("render an image") || m.Contains("render a picture")
            || m.Contains("generate an image") || m.Contains("generate a picture") || m.Contains("generate a photo")
            || m.Contains("generate me") && (m.Contains("image") || m.Contains("picture") || m.Contains("photo")))
            return true;

        if (MessageSuggestsGalleryTools(message) && MessageSuggestsCreateMake(m))
            return true;

        return false;
    }

    private static bool MessageSuggestsCreateMake(string mLower) =>
        mLower.Contains("create a ") || mLower.Contains("create an ") || mLower.Contains("create me ")
        || mLower.Contains("create some ") || mLower.Contains("can you create")
        || mLower.Contains("make a ") || mLower.Contains("make an ") || mLower.Contains("make me ")
        || mLower.Contains("make some ") || mLower.Contains("can you make")
        || mLower.Contains("generate a ") || mLower.Contains("generate an ") || mLower.Contains("generate me ")
        || mLower.Contains("generate some ") || mLower.Contains("can you generate")
        || mLower.Contains("draw a ") || mLower.Contains("draw an ") || mLower.Contains("draw me ")
        || mLower.Contains("paint a ") || mLower.Contains("paint an ") || mLower.Contains("paint me ");

    public static bool MessageSuggestsGalleryTools(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var m = message.ToLowerInvariant();
        return m.Contains("gallery") || m.Contains("album")
               || m.Contains("save to gallery") || m.Contains("save it to") || m.Contains("save that to")
               || m.Contains("save the image") || m.Contains("save this image") || m.Contains("save image")
               || m.Contains("save the picture") || m.Contains("save this picture")
               || m.Contains("add to gallery") || m.Contains("add to album")
               || m.Contains("put it in") && (m.Contains("gallery") || m.Contains("album"))
               || m.Contains("save to the") || m.Contains("save into");
    }

    /// <summary>
    /// Smart-home control language without requiring the HA wake word.
    /// Used by the AI router fallback so pure AI/Hybrid can open HomeAssistant tools.
    /// Rules-only mode still requires the wake word / active session (avoids false HA on every chat).
    /// </summary>
    public static bool MessageSuggestsHomeAssistant(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var m = message.ToLowerInvariant();

        // Explicit domains / devices
        if (m.Contains("light") || m.Contains("lamp") || m.Contains("lights")
            || m.Contains("switch") || m.Contains("outlet") || m.Contains("plug")
            || m.Contains("thermostat") || m.Contains("climate") || m.Contains("hvac")
            || m.Contains("temperature set") || m.Contains("set temperature")
            || m.Contains("media player") || m.Contains("tv ") || m.Contains(" tv")
            || m.Contains("volume") || m.Contains("mute") || m.Contains("unmute")
            || m.Contains("cover") || m.Contains("blinds") || m.Contains("garage")
            || m.Contains("lock the") || m.Contains("unlock") || m.Contains("door lock")
            || m.Contains("vacuum") || m.Contains("scene ") || m.Contains("activate scene")
            || m.Contains("home assistant") || m.Contains("smart home"))
            return true;

        // Actions commonly paired with HA
        if ((m.Contains("turn on") || m.Contains("turn off") || m.Contains("turn the")
             || m.Contains("dim ") || m.Contains("brighten") || m.Contains("brightness")
             || m.Contains("set the") && (m.Contains("%") || m.Contains("percent") || m.Contains("color") || m.Contains("colour")))
            && (m.Contains("room") || m.Contains("kitchen") || m.Contains("living") || m.Contains("bedroom")
                || m.Contains("hallway") || m.Contains("office") || m.Contains("den") || m.Contains("bath")
                || m.Contains("ceiling") || m.Contains("fan") || m.Contains("bulb")))
            return true;

        if (m.Contains("brightness") || m.Contains("color to") || m.Contains("colour to")
            || m.Contains("purple") && (m.Contains("light") || m.Contains("lamp") || m.Contains("%")))
            return true;

        return false;
    }

    public static string? TryExtractAlbumName(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var m = Regex.Match(
            message,
            @"(?:save|add|put|store|move)\s+(?:it|this|that|the\s+image|the\s+picture|them)?\s*" +
            @"(?:to|into|in)\s+(?:the\s+|my\s+)?[""']?([A-Za-z0-9][\w\s\-]{0,40}?)[""']?\s*(?:album|gallery)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
            return CleanAlbumExtract(m.Groups[1].Value);

        m = Regex.Match(
            message,
            @"(?:to|into|in)\s+(?:the\s+|my\s+)?[""']?([A-Za-z0-9][\w\s\-]{0,40}?)[""']?\s*(?:album|gallery)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
            return CleanAlbumExtract(m.Groups[1].Value);

        m = Regex.Match(
            message,
            @"(?:album|gallery)\s+(?:named|called)\s+[""']?([A-Za-z0-9][\w\s\-]{0,40}?)[""']?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
            return CleanAlbumExtract(m.Groups[1].Value);

        m = Regex.Match(
            message,
            @"\b([A-Za-z0-9][\w\-]{0,30})\s+(?:album|gallery)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
            return CleanAlbumExtract(m.Groups[1].Value);

        return null;
    }

    private static string? CleanAlbumExtract(string raw)
    {
        var t = raw.Trim().Trim('"', '\'', ',', '.', '!');
        if (t.Length == 0 || t.Length > 48)
            return null;
        if (t.Equals("the", StringComparison.OrdinalIgnoreCase)
            || t.Equals("my", StringComparison.OrdinalIgnoreCase)
            || t.Equals("a", StringComparison.OrdinalIgnoreCase)
            || t.Equals("an", StringComparison.OrdinalIgnoreCase)
            || t.Equals("new", StringComparison.OrdinalIgnoreCase)
            || t.Equals("photo", StringComparison.OrdinalIgnoreCase)
            || t.Equals("image", StringComparison.OrdinalIgnoreCase)
            || t.Equals("picture", StringComparison.OrdinalIgnoreCase))
            return null;
        return t;
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
