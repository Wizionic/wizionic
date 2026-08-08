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

    /// <summary>
    /// User wants image generation/editing (attach Lemonade + Gallery tools).
    /// Keyword list only — no album-name vocabulary.
    /// Also true for "create/make … and save to album" style prompts that omit the word "image".
    /// </summary>
    public static bool MessageSuggestsImageTools(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var m = message.ToLowerInvariant();

        // Explicit image / draw language
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

        // "create a purple orange fruit and save to the Fruits album" — no "image" word,
        // but create/make + gallery save clearly wants generation.
        if (MessageSuggestsGalleryTools(message) && MessageSuggestsCreateMake(m))
            return true;

        return false;
    }

    /// <summary>Create/make/generate verbs that imply producing new media (not "create a list").</summary>
    private static bool MessageSuggestsCreateMake(string mLower)
    {
        // Prefer generative verbs; avoid pure "create album" without other create phrasing.
        if (mLower.Contains("create album") || mLower.Contains("new album") || mLower.Contains("make album"))
        {
            // Still allow if they also ask to create a subject: "create a cat and a new album"
            // Fall through only when another create/make phrase exists.
        }

        return mLower.Contains("create a ") || mLower.Contains("create an ") || mLower.Contains("create me ")
               || mLower.Contains("create some ") || mLower.Contains("can you create")
               || mLower.Contains("make a ") || mLower.Contains("make an ") || mLower.Contains("make me ")
               || mLower.Contains("make some ") || mLower.Contains("can you make")
               || mLower.Contains("generate a ") || mLower.Contains("generate an ") || mLower.Contains("generate me ")
               || mLower.Contains("generate some ") || mLower.Contains("can you generate")
               || mLower.Contains("draw a ") || mLower.Contains("draw an ") || mLower.Contains("draw me ")
               || mLower.Contains("paint a ") || mLower.Contains("paint an ") || mLower.Contains("paint me ");
    }

    /// <summary>
    /// User wants to save/list gallery albums (attach Gallery tools).
    /// </summary>
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
    /// Best-effort album title from natural language, e.g.
    /// "save to the Fruit album", "put it in Fruit gallery", "album Fruit".
    /// Returns null when no clear album phrase is present.
    /// </summary>
    public static string? TryExtractAlbumName(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        // save|add|put … to|into|in … [the|my] NAME [album|gallery]
        var m = Regex.Match(
            message,
            @"(?:save|add|put|store|move)\s+(?:it|this|that|the\s+image|the\s+picture|them)?\s*" +
            @"(?:to|into|in)\s+(?:the\s+|my\s+)?[""']?([A-Za-z0-9][\w\s\-]{0,40}?)[""']?\s*(?:album|gallery)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
            return CleanAlbumExtract(m.Groups[1].Value);

        // … to|into the NAME album|gallery (no save verb required when album/gallery present)
        m = Regex.Match(
            message,
            @"(?:to|into|in)\s+(?:the\s+|my\s+)?[""']?([A-Za-z0-9][\w\s\-]{0,40}?)[""']?\s*(?:album|gallery)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
            return CleanAlbumExtract(m.Groups[1].Value);

        // album|gallery named|called NAME / NAME album
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
        // Reject generic filler
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