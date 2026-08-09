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
                if (MessageSuggestsCalendarTools(message) && Has("Calendar"))
                    modules.Add("Calendar");
                if (MessageSuggestsNotesTools(message) && Has("Notes"))
                    modules.Add("Notes");
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
            if (MessageSuggestsNotesTools(message) && Has("Notes"))
                modules.Add("Notes");
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
        var notesIntent = MessageSuggestsNotesTools(message);

        if (imageIntent)
        {
            var modules = new List<string> { "Native" };
            if (Has("Lemonade")) modules.Add("Lemonade");
            if (Has("Gallery")) modules.Add("Gallery");
            // "draw X and add to my notes" — attach Notes so the model can save after generate
            if (notesIntent && Has("Notes")) modules.Add("Notes");
            return RequestRoute.WithModules(
                modules,
                galleryIntent
                    ? "create/save image intent"
                    : notesIntent
                        ? "create image + notes intent"
                        : "image intent",
                source: "Rules");
        }

        if (galleryIntent)
        {
            var modules = new List<string> { "Native" };
            if (Has("Gallery")) modules.Add("Gallery");
            return RequestRoute.WithModules(modules, "gallery intent", source: "Rules");
        }

        var calendarIntent = MessageSuggestsCalendarTools(message);
        if (calendarIntent)
        {
            var modules = new List<string> { "Native" };
            if (Has("Calendar")) modules.Add("Calendar");
            return RequestRoute.WithModules(modules, "calendar intent", source: "Rules");
        }

        if (notesIntent)
        {
            var modules = new List<string> { "Native" };
            if (Has("Notes")) modules.Add("Notes");
            return RequestRoute.WithModules(modules, "notes intent", source: "Rules");
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

        // Avoid bare "latest" — it matches "append to the latest one" (notes) and similar.
        if (m.Contains("search") || m.Contains("look up") || m.Contains("lookup") ||
            m.Contains("google") || m.Contains("news") ||
            m.Contains("latest news") || m.Contains("the latest") && (m.Contains("news") || m.Contains("score") || m.Contains("price") || m.Contains("headline")) ||
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
    /// User wants calendar list/add/update/delete or schedule language.
    /// </summary>
    public static bool MessageSuggestsCalendarTools(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var m = message.ToLowerInvariant();

        if (m.Contains("calendar") || m.Contains("calendars")
            || m.Contains("schedule") || m.Contains("reschedule")
            || m.Contains("appointment") || m.Contains("meeting")
            || m.Contains("add an event") || m.Contains("add a event")
            || m.Contains("add event") || m.Contains("create an event") || m.Contains("create event")
            || m.Contains("book a") || m.Contains("put on my calendar")
            || m.Contains("add to my calendar") || m.Contains("add to the calendar")
            || m.Contains("add to calendar") || m.Contains("on my calendar")
            || m.Contains("to my calendar") || m.Contains("into my calendar")
            || m.Contains("repeating event") || m.Contains("recurring event")
            || m.Contains("what's on my") || m.Contains("whats on my")
            || m.Contains("what is on my") || m.Contains("am i free")
            || m.Contains("free on") || m.Contains("busy on")
            || m.Contains("list events") || m.Contains("show events")
            || m.Contains("delete the event") || m.Contains("cancel the event")
            || m.Contains("update the event") || m.Contains("move the event"))
            return true;

        // "every Wednesday" + add/play sports-style scheduling without saying "calendar"
        if ((m.Contains("every ") || m.Contains("each ") || m.Contains("repeating") || m.Contains("recurring"))
            && (m.Contains("wednesday") || m.Contains("monday") || m.Contains("tuesday")
                || m.Contains("thursday") || m.Contains("friday") || m.Contains("saturday")
                || m.Contains("sunday") || m.Contains("weekday") || m.Contains("week")))
        {
            if (m.Contains("add") || m.Contains("schedule") || m.Contains("create")
                || m.Contains("book") || m.Contains("set up") || m.Contains("setup")
                || m.Contains("put") || m.Contains("playing") || m.Contains("practice"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// User wants notebook create/list or add/append note entries (text and/or images).
    /// </summary>
    public static bool MessageSuggestsNotesTools(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var m = message.ToLowerInvariant();

        // Explicit notebook product language
        if (m.Contains("notebook") || m.Contains("notebooks")
            || m.Contains("list notebooks") || m.Contains("show notebooks")
            || m.Contains("create a notebook") || m.Contains("create notebook")
            || m.Contains("new notebook") || m.Contains("make a notebook")
            || m.Contains("list_note") || m.Contains("add_note") || m.Contains("append_to_note")
            || m.Contains("list_notebooks") || m.Contains("create_notebook"))
            return true;

        // List / show entries (often omits the word "note" — e.g. "list entries in Travel Journal")
        if (m.Contains("list entries") || m.Contains("show entries") || m.Contains("list note entries")
            || m.Contains("show note entries") || m.Contains("entries in ")
            || m.Contains("note entries") || m.Contains("note entry")
            || m.Contains("notebook entries") || m.Contains("journal entries"))
            return true;

        // Append / update existing entry language
        // "append '…' to the latest one" (no "note"/"notebook" in the sentence)
        if (m.Contains("append ") || m.Contains("append to") || m.Contains("append '") || m.Contains("append \"")
            || m.Contains("to the latest one") || m.Contains("to the latest entry")
            || m.Contains("to the last one") || m.Contains("to the last entry")
            || m.Contains("to that entry") || m.Contains("to the entry")
            || m.Contains("update the entry") || m.Contains("edit the entry")
            || m.Contains("add to the latest") || m.Contains("add to the last"))
            return true;

        // "journal" as notebook synonym only with action verbs (avoids "journal article")
        if (m.Contains("journal")
            && (m.Contains("list ") || m.Contains("show ") || m.Contains("add ") || m.Contains("create ")
                || m.Contains("append ") || m.Contains("save ") || m.Contains("write ") || m.Contains("put ")
                || m.Contains("entries") || m.Contains("entry") || m.Contains("my journal")))
            return true;

        // "notes" alone is noisy (e.g. "take note of that"); require action phrasing
        if (m.Contains("add to notes") || m.Contains("add to my notes") || m.Contains("add to the notes")
            || m.Contains("add to notebook") || m.Contains("add to my notebook")
            || m.Contains("add to a note") || m.Contains("add to the note")
            || m.Contains("save to notes") || m.Contains("save to my notes") || m.Contains("save to notebook")
            || m.Contains("save in notes") || m.Contains("save into notes") || m.Contains("save into notebook")
            || m.Contains("put in notes") || m.Contains("put into notes") || m.Contains("put in my notes")
            || m.Contains("put in notebook") || m.Contains("write in notes") || m.Contains("write to notes")
            || m.Contains("append to note") || m.Contains("append to the note") || m.Contains("append to my note")
            || m.Contains("add a note") || m.Contains("add note entry") || m.Contains("add an entry")
            || m.Contains("create a note") || m.Contains("create note") || m.Contains("new note entry")
            || m.Contains("add this to notes") || m.Contains("add that to notes")
            || m.Contains("add this to my notes") || m.Contains("add that to my notes")
            || m.Contains("add this image to notes") || m.Contains("add the image to notes")
            || m.Contains("add this image to my notes") || m.Contains("save image to notes")
            || m.Contains("save the image to notes") || m.Contains("save picture to notes")
            || m.Contains("into my notes") || m.Contains("in my notes") || m.Contains("to my notebook")
            || m.Contains("into my notebook") || m.Contains("in my notebook")
            || m.Contains("to my journal") || m.Contains("in my journal") || m.Contains("into my journal")
            || m.Contains("add to journal") || m.Contains("save to journal"))
            return true;

        // "add … to the Travel notebook" style without the word "notes"
        if ((m.Contains("notebook") || m.Contains("journal") || m.Contains(" note ") || m.Contains(" note.") || m.EndsWith(" note"))
            && (m.Contains("add ") || m.Contains("create ") || m.Contains("write ") || m.Contains("append ")
                || m.Contains("save ") || m.Contains("put ") || m.Contains("list ")))
            return true;

        return false;
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
