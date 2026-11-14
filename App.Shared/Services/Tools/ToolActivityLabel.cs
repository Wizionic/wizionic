using App.Core.Skills;

namespace App.Shared.Services.Tools;

/// <summary>
/// Short status line for the chat "thinking" bubble while a tool call is still running.
/// Module notes recorded inside the call (the web-search line, then the HTTP wait) do not
/// count as finished — only a later tool-result step does.
/// </summary>
public static class ToolActivityLabel
{
    public static string? ForInFlightTool(IReadOnlyList<SkillStep>? steps)
    {
        if (steps is null || steps.Count == 0)
            return null;

        string? name = null;
        string? args = null;
        var open = 0;
        foreach (var step in steps)
        {
            if (step.Kind == SkillStepKind.ToolCall)
            {
                open++;
                name = step.ToolName;
                args = step.ArgsSummary;
            }
            else if (step.Kind == SkillStepKind.ToolResult && open > 0)
            {
                open--;
                if (open == 0)
                {
                    name = null;
                    args = null;
                }
            }
        }

        return open > 0 ? Describe(name, args) : null;
    }

    private static string Describe(string? name, string? args)
    {
        var key = Norm(name);
        return key switch
        {
            "searchweb" or "websearch" or "internetsearch" or "googlesearch" => SearchPhrase(args),
            "summarizeurl" => SitePhrase("Reading", args, "Reading a page…"),
            "getcurrenttimeutc" or "getcurrenttime" or "gettime" => "Checking the time…",
            "calculate" => DetailPhrase("Calculating", ReadArg(args, "expression", "expr"), "Calculating…"),
            "getcurrentweather" or "getweather" => "Checking the weather…",
            "navigateto" => SitePhrase("Opening", args, "Opening a page…"),
            "getpagecontent" => "Reading the page…",
            "clickelement" => "Clicking…",
            "fillfield" => "Filling a field…",
            "listentities" => "Looking up devices…",
            "controllight" => LightPhrase(args),
            "controlmediaplayer" => "Controlling media…",
            "controlclimate" => "Adjusting the thermostat…",
            "controlcover" => "Moving a cover…",
            "activatescene" => "Activating a scene…",
            "runscript" => "Running a script…",
            "getentitystate" => "Checking a device…",
            "callservice" => "Calling Home Assistant…",
            "processconversation" => "Asking Home Assistant…",
            "listcalendars" => "Listing calendars…",
            "listevents" => "Checking the calendar…",
            "addcalendarevent" => "Adding a calendar event…",
            "updatecalendarevent" => "Updating a calendar event…",
            "deletecalendarevent" => "Removing a calendar event…",
            "listgalleryalbums" => "Looking through albums…",
            "listrecentchatimages" => "Looking for recent images…",
            "savetogallery" => "Saving to the gallery…",
            "generateimage" or "lemonadegenerateimage" => "Generating an image…",
            "editimage" or "lemonadeeditimage" => "Editing an image…",
            "lemonadetexttospeech" => "Preparing speech…",
            "searchnotes" => DetailPhrase("Searching notes for", ReadArg(args, "query", "q"), "Searching notes…", quote: true),
            "listnotebooks" => "Listing notebooks…",
            "listnoteentries" => "Opening a notebook…",
            "createnotebook" => "Creating a notebook…",
            "addnoteentry" => "Adding a note…",
            "appendtonoteentry" => "Adding to a note…",
            "updatenoteentry" => "Updating a note…",
            _ when key.Contains("websearch", StringComparison.Ordinal) => SearchPhrase(args),
            _ => Calling(name)
        };
    }

    private static string SearchPhrase(string? args) =>
        DetailPhrase("Searching the web for", ReadArg(args, "query", "q"), "Searching the web…", quote: true);

    private static string LightPhrase(string? args)
    {
        var state = ReadArg(args, "state");
        if (state is not null && state.Equals("on", StringComparison.OrdinalIgnoreCase))
            return "Turning a light on…";
        if (state is not null && state.Equals("off", StringComparison.OrdinalIgnoreCase))
            return "Turning a light off…";
        return "Controlling a light…";
    }

    private static string SitePhrase(string verb, string? args, string fallback)
    {
        var site = Site(args);
        return site is null ? fallback : Finish($"{verb} {site}");
    }

    private static string DetailPhrase(string lead, string? detail, string fallback, bool quote = false)
    {
        detail = Clean(detail);
        if (detail is null)
            return fallback;
        if (detail.Length > 48)
            detail = detail[..48].TrimEnd();
        var body = quote ? $"{lead} “{detail}”" : $"{lead} {detail}";
        return Finish(body);
    }

    private static string Calling(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Calling a tool…";
        return Finish("Calling " + Clip(name.Trim(), 42));
    }

    private static string Finish(string text)
    {
        const int max = 96;
        text = text.Trim();
        if (text.Length > max)
            text = text[..(max - 1)].TrimEnd();
        return text.EndsWith('…') ? text : text + "…";
    }

    private static string Clip(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max)
            return text;
        return text[..max].TrimEnd() + "…";
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? Site(string? args)
    {
        var url = Clean(ReadArg(args, "url", "uri", "link"));
        if (url is null)
            return null;
        var candidate = url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url.TrimStart('/');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host;
        return Clip(url, 40);
    }

    private static string? ReadArg(string? args, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(args))
            return null;
        foreach (var key in keys)
        {
            var value = ReadOne(args, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static string? ReadOne(string args, string key)
    {
        var marker = key + "=";
        var search = 0;
        while (search < args.Length)
        {
            var at = args.IndexOf(marker, search, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return null;
            var boundary = at == 0 || args[at - 1] == ' ' || args[at - 1] == ',';
            if (!boundary)
            {
                search = at + marker.Length;
                continue;
            }

            var start = at + marker.Length;
            var end = start;
            while (end < args.Length)
            {
                if (args[end] == ',' && end + 2 < args.Length && args[end + 1] == ' ' && LooksLikeNextArg(args, end + 2))
                    break;
                end++;
            }

            var value = args[start..end].Trim().Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static bool LooksLikeNextArg(string args, int start)
    {
        var eq = args.IndexOf('=', start);
        if (eq <= start)
            return false;
        for (var i = start; i < eq; i++)
        {
            var c = args[i];
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }

    private static string Norm(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is '_' or '-' or ' ' or '.')
                continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
