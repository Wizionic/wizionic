using System.Text.RegularExpressions;

namespace App.Shared.Services.Tools;

internal static class ProjectHomeIntent
{
    internal enum Access { None, Read, Write }

    internal static Access Classify(string request)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (!Regex.IsMatch(request, @"\b(home assistant|smart home|lights?|lamps?|bulbs?|thermostat|blinds|garage door|switches|plugs?|receiver|denon|avr|speaker|volume|scenes?|media_player|tv|sensors?)\b", options))
            return Access.None;
        // A command starts a request/clause. Mentions such as "which lights turn off at night?" are not commands.
        const string clause = @"(?:^|[.!;]\s*|,\s*|\band\s+|\bthen\s+)(?:please\s+|(?:can|could|would) you\s+)?";
        const string device = @"(?:lights?|lamps?|bulbs?|switch(?:es)?|plugs?|thermostat|blinds|garage door|receiver|denon|avr|speakers?|tv)\b";
        const string qualifier = @"(?:(?:the|my|all|kitchen|living room|bedroom|office|hallway)\s+)*";
        if (Regex.IsMatch(request, clause + @"(?:turn\s+(?:on|off)\s+" + qualifier + device +
            @"|turn\s+" + qualifier + device + @"\s+(?:on|off)\b|switch\s+(?:on|off)\s+" + qualifier + device +
            @"|set\s+" + qualifier + @"(?:volume|temperature|thermostat|lights?|blinds)\b" +
            @"|(?:dim|brighten|mute|unmute)\s+" + qualifier + device +
            @"|(?:open|close)\s+" + qualifier + @"(?:blinds|garage door)\b|activate\s+" + qualifier + @"scene\b" +
            @"|(?:play|pause|resume)\s+" + qualifier + @"(?:music|playback|media)\b)", options))
            return Access.Write;
        return Regex.IsMatch(request, @"\b(check|status|state|which|show|list|what|whether|report|currently|are|is)\b", options) ? Access.Read : Access.None;
    }

    internal static bool Allows(Access access, string name) => access switch
    {
        Access.None => false,
        Access.Read => name.Equals("ListEntities", StringComparison.OrdinalIgnoreCase) || name.Equals("GetEntityState", StringComparison.OrdinalIgnoreCase),
        _ => !name.Equals("ProcessConversation", StringComparison.OrdinalIgnoreCase) && !name.Equals("CallService", StringComparison.OrdinalIgnoreCase)
    };
}
