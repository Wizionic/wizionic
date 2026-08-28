using System.Text.RegularExpressions;
using App.Core.Skills;
using App.Core.Storage;
using App.Core.Tools;
using App.Core.UI;

namespace App.Shared.Services.Tools;

/// <summary>
/// Rule-based tool router: wake word, browser panel, session, skill slash commands, and keyword heuristics.
/// Zero model cost. Used alone (Rules mode) or as the Hybrid fast path / AI fallback.
/// </summary>
public sealed class ContextualRequestRouter : IRequestRouter
{
    public static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(15);

    private readonly IKeyStore _keyStore;
    private readonly IRoutingSessionStore _sessions;
    private readonly IBrowserPanelState _browserPanel;
    private readonly ISkillStore? _skills;

    public ContextualRequestRouter(
        IKeyStore keyStore,
        IRoutingSessionStore sessions,
        IBrowserPanelState browserPanel,
        ISkillStore? skills = null)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _browserPanel = browserPanel ?? throw new ArgumentNullException(nameof(browserPanel));
        _skills = skills;
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

        // Explicit skill invoke: /skill-name or "run skill skill-name"
        var skillRoute = TryMatchSkillCommand(message, available);
        if (skillRoute is not null)
            return skillRoute;

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
                if (MessageSuggestsImageTools(message) && AddImageModule(modules, Has))
                {
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

            // Device-control language (play music on AVR, kitchen light, …) without repeating the wake word.
            if (MessageSuggestsHomeAssistant(message))
            {
                var modules = new List<string> { "HomeAssistant", "Native" };
                return RequestRoute.WithModules(
                    modules,
                    "Home Assistant device intent",
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
            if (MessageSuggestsImageTools(message) && AddImageModule(modules, Has))
            {
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
            AddImageModule(modules, Has);
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

    private RequestRoute? TryMatchSkillCommand(string message, HashSet<string> available)
    {
        if (_skills is null) return null;
        var skillName = ExtractSkillNameFromMessage(message);
        if (string.IsNullOrEmpty(skillName)) return null;

        var rec = ResolveSkillRecord(skillName);
        if (rec is null || !rec.Enabled) return null;

        try
        {
            var doc = SkillMarkdown.Parse(rec.Markdown);
            var res = SkillToolResolver.Resolve(doc.AllowedTools);
            // Prefer available modules, but keep skill-declared modules so HA etc. still attach when registered.
            var modules = res.Modules.Where(available.Contains).ToList();
            if (modules.Count == 0)
                modules = res.Modules.ToList();
            return RequestRoute.WithModules(
                modules,
                reason: "skill /" + doc.Name,
                includeMcp: res.IncludeMcp,
                source: "Rules",
                skillId: rec.Id);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Accepts: /random-house-lights, /skill random-house-lights, /skill-random-house-lights,
    /// run skill random-house-lights (optional trailing args ignored for match).
    /// </summary>
    public static string? ExtractSkillNameFromMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var m = message.Trim();

        if (m.StartsWith('/'))
        {
            var rest = m[1..].Trim();
            // /skill name  or  /skill-name  or  /name
            if (rest.StartsWith("skill ", StringComparison.OrdinalIgnoreCase))
            {
                var after = rest["skill ".Length..].Trim();
                var token = after.Split(new[] { ' ', '\t', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return string.IsNullOrWhiteSpace(token) ? null : SkillMarkdown.NormalizeName(token);
            }

            var slashToken = rest.Split(new[] { ' ', '\t', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(slashToken)) return null;
            slashToken = SkillMarkdown.NormalizeName(slashToken);
            // /skill-random-house-lights → try full then strip skill- prefix
            return slashToken;
        }

        var run = Regex.Match(m, @"^\s*run\s+skill\s+([a-zA-Z0-9][a-zA-Z0-9\-_]*)\b", RegexOptions.IgnoreCase);
        if (run.Success)
            return SkillMarkdown.NormalizeName(run.Groups[1].Value);

        return null;
    }

    private SkillRecord? ResolveSkillRecord(string skillName)
    {
        if (_skills is null || string.IsNullOrWhiteSpace(skillName)) return null;

        // Exact
        var rec = _skills.Get(skillName);
        if (rec is not null) return rec;

        // /skill-foo when skill id is foo
        if (skillName.StartsWith("skill-", StringComparison.OrdinalIgnoreCase) && skillName.Length > 6)
        {
            rec = _skills.Get(skillName["skill-".Length..]);
            if (rec is not null) return rec;
        }

        // Prefix / contains match against enabled skills (unique hit only)
        var enabled = _skills.List().Where(s => s.Enabled).ToList();
        var hits = enabled.Where(s =>
            s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase) ||
            s.Id.Equals(skillName, StringComparison.OrdinalIgnoreCase) ||
            s.Name.StartsWith(skillName, StringComparison.OrdinalIgnoreCase) ||
            skillName.EndsWith(s.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) return hits[0];

        return null;
    }

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
    /// Image generate/edit tools only from the selected <b>profile</b> slots.
    /// A raw model in the picker does not inherit Imagine or Lemonade image tools.
    /// </summary>
    private bool AddImageModule(List<string> modules, Func<string, bool> has)
    {
        var imageId = ModelProfileId.ResolveImageModelId(_keyStore);
        if (string.IsNullOrWhiteSpace(imageId))
            return false;

        if (ModelProfileId.TryCloudProvider(imageId, out _, out _) && has("Cloud"))
        {
            modules.Add("Cloud");
            return true;
        }

        if (ModelProfileId.IsLemonadeCatalog(imageId) && has("Lemonade"))
        {
            modules.Add("Lemonade");
            return true;
        }

        return false;
    }

    /// <summary>
    /// User wants image generation/editing (attach Cloud or Lemonade + Gallery tools).
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
            || m.Contains("list_notebooks") || m.Contains("create_notebook")
            || m.Contains("search_notes") || m.Contains("search notes")
            || m.Contains("find in my notes") || m.Contains("look up in notes")
            || m.Contains("what did i write") || m.Contains("what did i note"))
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
            || m.Contains("add to the latest") || m.Contains("add to the last")
            || m.Contains("update_note_entry") || m.Contains("save it back")
            || m.Contains("save back") || m.Contains("this note") || m.Contains("the note"))
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
    /// Used by Rules (device intent) and by the AI router fallback.
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
            || m.Contains("media player") || m.Contains("media_player")
            || m.Contains("tv ") || m.Contains(" tv")
            || m.Contains("volume") || m.Contains("mute") || m.Contains("unmute")
            || m.Contains("cover") || m.Contains("blinds") || m.Contains("garage")
            || m.Contains("lock the") || m.Contains("unlock") || m.Contains("door lock")
            || m.Contains("vacuum") || m.Contains("scene ") || m.Contains("activate scene")
            || m.Contains("home assistant") || m.Contains("smart home")
            || m.Contains("avr") || m.Contains("receiver") || m.Contains("soundbar")
            || m.Contains("sound bar") || m.Contains("denon") || m.Contains("sonos")
            || m.Contains("yamaha") || m.Contains("heos") || m.Contains("chromecast")
            || m.Contains("shield") || m.Contains("speaker") || m.Contains("stereo"))
            return true;

        // Play/pause/stop media on a house device
        if ((m.Contains("play") || m.Contains("pause") || m.Contains("resume") || m.Contains("stop"))
            && (m.Contains("music") || m.Contains("song") || m.Contains("media")
                || m.Contains("on my") || m.Contains("on the")))
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
