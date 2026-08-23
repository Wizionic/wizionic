using System.Text.RegularExpressions;
using App.Core.SmartHome;
using App.Core.Tools;

namespace App.Shared.Services.Tools;

/// <summary>
/// App-level HA recovery when the chat model skips tools: structured REST first, then Assist.
/// </summary>
public static class HomeAssistantFallback
{
    private static readonly string[] ColorNames =
    [
        "white", "warm_white", "cold_white", "red", "green", "blue", "yellow", "orange",
        "purple", "pink", "cyan", "magenta", "lime", "turquoise", "gold"
    ];

    /// <summary>
    /// Deterministic control from user text + session + device catalog (no LLM).
    /// Returns user-facing success text, or null if intent/entity could not be resolved.
    /// </summary>
    public static async Task<(string? Message, string? EntityId, string? Action)> TryStructuredAsync(
        ISmartHomeService ha,
        IToolExecutionTrace trace,
        string userMessage,
        string assistantName,
        RoutingSession session,
        string deviceCatalog,
        CancellationToken ct = default)
    {
        if (!ha.IsConfigured || string.IsNullOrWhiteSpace(userMessage))
            return (null, null, null);

        var text = StripWakeWord(userMessage, assistantName);
        if (string.IsNullOrWhiteSpace(text))
            text = userMessage.Trim();

        var looksLikeLight = LooksLikeLightRequest(text);
        var volumePct = TryExtractVolumePercent(text, session, looksLikeLight);

        // --- Volume / music level ---
        if (volumePct is int pct)
        {
            var mediaEntity = ResolveEntity(
                text, "media_player", session.LastMediaPlayerEntity, deviceCatalog);
            if (string.IsNullOrWhiteSpace(mediaEntity))
                return (null, null, null);

            var level = pct / 100.0;
            var label = DisplayName(mediaEntity, deviceCatalog);
            trace.Record($"🏠 structured_fallback volume_set(entity=\"{mediaEntity}\", volume_percent={pct})");
            var result = await ha.CallServiceAsync(
                "media_player",
                "volume_set",
                new Dictionary<string, object?> { ["entity_id"] = mediaEntity, ["volume_level"] = level },
                ct);

            if (IsFailure(result))
            {
                trace.Record($"   ❌ {Truncate(result, 300)}");
                return (null, null, null);
            }

            trace.Record($"   ✅ {Truncate(result.Replace('\n', ' '), 300)}");
            return ($"Set volume on {label} to {pct}%.", mediaEntity, "volume_set");
        }

        // --- Media play/pause/stop (not volume) ---
        if (!looksLikeLight &&
            (Regex.IsMatch(text, @"\b(play|pause|stop|resume)\b", RegexOptions.IgnoreCase)
             || Regex.IsMatch(text, @"\b(play music|pause music|stop music)\b", RegexOptions.IgnoreCase)))
        {
            // Avoid treating "playing" in "music playing to 50%" as a play command when % already handled above
            var mediaEntity = ResolveEntity(text, "media_player", session.LastMediaPlayerEntity, deviceCatalog);
            if (!string.IsNullOrWhiteSpace(mediaEntity))
            {
                var lower = text.ToLowerInvariant();
                var service = lower.Contains("pause") ? "media_pause"
                    : lower.Contains("stop") ? "media_stop"
                    : "media_play";
                var label = DisplayName(mediaEntity, deviceCatalog);
                trace.Record($"🏠 structured_fallback {service}(entity=\"{mediaEntity}\")");
                var result = await ha.CallServiceAsync(
                    "media_player",
                    service,
                    new Dictionary<string, object?> { ["entity_id"] = mediaEntity },
                    ct);
                if (!IsFailure(result))
                {
                    trace.Record($"   ✅ {Truncate(result.Replace('\n', ' '), 300)}");
                    return ($"{FormatMediaAction(service)} on {label}.", mediaEntity, service);
                }

                trace.Record($"   ❌ {Truncate(result, 300)}");
            }
        }

        // --- Lights: on/off, color, brightness % ---
        var lightOn = Regex.IsMatch(text, @"\b(turn|switch|set)\b.+\b(on)\b", RegexOptions.IgnoreCase)
                      || Regex.IsMatch(text, @"\blights?\s+on\b", RegexOptions.IgnoreCase)
                      || Regex.IsMatch(text, @"\bto\s+\d{1,3}\s*%", RegexOptions.IgnoreCase); // "to 50%" implies on
        var lightOff = Regex.IsMatch(text, @"\b(turn|switch|set)\b.+\b(off)\b", RegexOptions.IgnoreCase)
                       || Regex.IsMatch(text, @"\blights?\s+off\b", RegexOptions.IgnoreCase);
        var color = ExtractColor(text);
        var brightnessPct = ExtractLightBrightnessPercent(text);
        var isLightRequest = looksLikeLight
            || color is not null
            || (!string.IsNullOrWhiteSpace(session.LastLightEntity)
                && Regex.IsMatch(text, @"\b(on|off|color|bright|dim)\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(text, @"\b(volume|music|media|avr|receiver)\b", RegexOptions.IgnoreCase));

        if (isLightRequest && (lightOn || lightOff || color is not null || brightnessPct is not null))
        {
            var lightEntity = ResolveEntity(text, "light", session.LastLightEntity, deviceCatalog);
            if (string.IsNullOrWhiteSpace(lightEntity) && !string.IsNullOrWhiteSpace(session.LastLightEntity))
                lightEntity = session.LastLightEntity;

            if (!string.IsNullOrWhiteSpace(lightEntity))
            {
                var label = DisplayName(lightEntity, deviceCatalog);

                if (lightOff && color is null && brightnessPct is null)
                {
                    trace.Record($"🏠 structured_fallback light.turn_off(entity=\"{lightEntity}\")");
                    var result = await ha.CallServiceAsync(
                        "light", "turn_off", new { entity_id = lightEntity }, ct);
                    if (!IsFailure(result))
                    {
                        trace.Record($"   ✅ {Truncate(result.Replace('\n', ' '), 300)}");
                        return ($"Turned off {label}.", lightEntity, "turn_off");
                    }

                    trace.Record($"   ❌ {Truncate(result, 300)}");
                }
                else
                {
                    var data = new Dictionary<string, object?> { ["entity_id"] = lightEntity };
                    if (color is not null)
                        data["color_name"] = color;
                    if (brightnessPct is int bp)
                        data["brightness"] = (int)Math.Round(Math.Clamp(bp, 0, 100) * 255.0 / 100.0);

                    var brightNote = brightnessPct is int b ? $", brightness={b}%" : "";
                    trace.Record($"🏠 structured_fallback light.turn_on(entity=\"{lightEntity}\", color={color ?? ""}{brightNote})");
                    var result = await ha.CallServiceAsync("light", "turn_on", data, ct);
                    if (!IsFailure(result))
                    {
                        trace.Record($"   ✅ {Truncate(result.Replace('\n', ' '), 300)}");
                        var parts = new List<string>();
                        if (color is not null)
                            parts.Add(color);
                        if (brightnessPct is int b2)
                            parts.Add($"{b2}% brightness");
                        var detail = parts.Count > 0 ? string.Join(", ", parts) : "on";
                        return ($"Set {label} to {detail}.", lightEntity, "turn_on");
                    }

                    trace.Record($"   ❌ {Truncate(result, 300)}");
                }
            }
        }

        // --- Climate / thermostat ---
        var tempMatch = Regex.Match(
            text,
            @"\b(?:set|turn|change|make)\b.{0,40}?\b(?:temp(?:erature)?|thermostat|climate|heat|ac|hvac)\b.{0,40}?\b(?:to|at)\s*(-?\d{1,3}(?:\.\d)?)",
            RegexOptions.IgnoreCase);
        if (!tempMatch.Success)
        {
            tempMatch = Regex.Match(
                text,
                @"\b(?:temp(?:erature)?|thermostat)\b.{0,20}?\b(?:to|at)\s*(-?\d{1,3}(?:\.\d)?)",
                RegexOptions.IgnoreCase);
        }

        if (tempMatch.Success && double.TryParse(tempMatch.Groups[1].Value, out var tempVal))
        {
            var climateEntity = ResolveEntity(text, "climate", session.LastClimateEntity, deviceCatalog);
            if (!string.IsNullOrWhiteSpace(climateEntity))
            {
                var label = DisplayName(climateEntity, deviceCatalog);
                trace.Record($"🏠 structured_fallback climate.set_temperature(entity=\"{climateEntity}\", temperature={tempVal})");
                var result = await ha.CallServiceAsync(
                    "climate",
                    "set_temperature",
                    new Dictionary<string, object?> { ["entity_id"] = climateEntity, ["temperature"] = tempVal },
                    ct);
                if (!IsFailure(result))
                {
                    trace.Record($"   ✅ {Truncate(result.Replace('\n', ' '), 300)}");
                    return ($"Set {label} to {tempVal}.", climateEntity, "set_temperature");
                }

                trace.Record($"   ❌ {Truncate(result, 300)}");
            }
        }

        // --- Covers / garage / blinds ---
        var coverClose = Regex.IsMatch(text, @"\b(close|shut)\b.+\b(garage|cover|blind|shade|curtain|door)\b", RegexOptions.IgnoreCase)
                         || Regex.IsMatch(text, @"\b(garage|cover|blind|shade|curtain).+\b(close|shut)\b", RegexOptions.IgnoreCase);
        var coverOpen = Regex.IsMatch(text, @"\b(open)\b.+\b(garage|cover|blind|shade|curtain|door)\b", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(text, @"\b(garage|cover|blind|shade|curtain).+\bopen\b", RegexOptions.IgnoreCase);
        if (coverOpen || coverClose)
        {
            var coverEntity = ResolveEntity(text, "cover", session.LastCoverEntity, deviceCatalog);
            if (!string.IsNullOrWhiteSpace(coverEntity))
            {
                var service = coverClose ? "close_cover" : "open_cover";
                var label = DisplayName(coverEntity, deviceCatalog);
                trace.Record($"🏠 structured_fallback {service}(entity=\"{coverEntity}\")");
                var result = await ha.CallServiceAsync(
                    "cover", service, new Dictionary<string, object?> { ["entity_id"] = coverEntity }, ct);
                if (!IsFailure(result))
                {
                    trace.Record($"   ✅ {Truncate(result.Replace('\n', ' '), 300)}");
                    return ($"{(coverClose ? "Closed" : "Opened")} {label}.", coverEntity, service);
                }

                trace.Record($"   ❌ {Truncate(result, 300)}");
            }
        }

        return (null, null, null);
    }

    /// <summary>
    /// Extract volume percent for media. Returns null if not a volume request.
    /// Handles: "volume to 50%", "music playing to 50%", "to 50%" with last media player.
    /// </summary>
    internal static int? TryExtractVolumePercent(string text, RoutingSession session, bool looksLikeLight)
    {
        // Explicit volume/vol keyword
        var m = Regex.Match(
            text,
            @"\b(?:volume|vol)\b(?:\s+(?:to|at|of))?\s*(?:down|up)?\s*(?:to|at)?\s*(\d{1,3})\s*%?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p1))
            return Math.Clamp(p1, 0, 100);

        m = Regex.Match(
            text,
            @"\b(?:set|turn|change|make)\b.+\b(?:volume|vol)\b.+\b(\d{1,3})\s*%?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p2))
            return Math.Clamp(p2, 0, 100);

        // Colloquial: "turn the music playing to 50%", "music to 40", "set media to 30%"
        // Do not steal light brightness ("kitchen light to 50%").
        if (!looksLikeLight)
        {
            m = Regex.Match(
                text,
                @"\b(?:music|media|song|audio|sound|playing)\b.{{0,40}}?\b(?:to|at)\s*(\d{1,3})\s*%?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p3))
                return Math.Clamp(p3, 0, 100);

            m = Regex.Match(
                text,
                @"\b(?:set|turn|change|make)\b.{{0,40}}?\b(?:music|media|song|audio|sound|playing)\b.{{0,40}}?\b(?:to|at)\s*(\d{1,3})\s*%?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p4))
                return Math.Clamp(p4, 0, 100);

            // Bare "to 50%" when we have a last media player and message hints media (or no light)
            if (!string.IsNullOrWhiteSpace(session.LastMediaPlayerEntity) &&
                Regex.IsMatch(text, @"\b(?:music|media|song|audio|sound|playing|avr|receiver|denon|volume|vol)\b", RegexOptions.IgnoreCase))
            {
                m = Regex.Match(text, @"\b(?:to|at)\s*(\d{1,3})\s*%?", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var p5))
                    return Math.Clamp(p5, 0, 100);
            }
        }

        return null;
    }

    private static bool LooksLikeLightRequest(string text) =>
        Regex.IsMatch(text, @"\blights?\b", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(text, @"\b(volume|music|media|avr|receiver|denon)\b", RegexOptions.IgnoreCase);

    /// <summary>Brightness for lights only (message already classified as light-ish).</summary>
    private static int? ExtractLightBrightnessPercent(string text)
    {
        // Prefer "to 50%" / "at 50%" / "50% brightness" / "brightness 50"
        var m = Regex.Match(
            text,
            @"\b(?:brightness|bright)\b\s*(?:to|at|=)?\s*(\d{1,3})\s*%?",
            RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var b1))
            return Math.Clamp(b1, 0, 100);

        m = Regex.Match(
            text,
            @"\b(\d{1,3})\s*%\s*(?:brightness|bright)?",
            RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var b2))
            return Math.Clamp(b2, 0, 100);

        m = Regex.Match(
            text,
            @"\b(?:to|at)\s*(\d{1,3})\s*%",
            RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var b3))
            return Math.Clamp(b3, 0, 100);

        return null;
    }

    private static string DisplayName(string entityId, string deviceCatalog) =>
        FriendlyNameFor(entityId, deviceCatalog) ?? entityId;

    private static string FormatMediaAction(string service) => service switch
    {
        "media_play" => "Playing",
        "media_pause" => "Paused",
        "media_stop" => "Stopped",
        _ => service
    };

    /// <summary>
    /// Build a clean natural-language sentence for HA Assist (no raw entity_ids).
    /// For volume-like intents, rewrite to "set volume to N percent on {friendly}".
    /// </summary>
    public static string BuildAssistCommand(
        string userMessage,
        string assistantName,
        RoutingSession session,
        string deviceCatalog)
    {
        var text = StripWakeWord(userMessage, assistantName);
        if (string.IsNullOrWhiteSpace(text))
            text = userMessage?.Trim() ?? "";

        // Drop filler words that confuse Assist ("now" → "device called now")
        text = Regex.Replace(text, @"\b(please|thanks|thank you|can you|could you|would you)\b", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bnow\b", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s{2,}", " ").Trim().TrimEnd('?', '.', '!');

        var looksLikeLight = LooksLikeLightRequest(text);
        var volumePct = TryExtractVolumePercent(text, session, looksLikeLight);

        if (volumePct is int vp)
        {
            var mediaEntity = ResolveEntity(text, "media_player", session.LastMediaPlayerEntity, deviceCatalog);
            var friendly = mediaEntity is null ? null : FriendlyNameFor(mediaEntity, deviceCatalog);
            if (!string.IsNullOrWhiteSpace(friendly))
                return $"set volume to {vp} percent on {friendly}";
            return $"set volume to {vp} percent";
        }

        var isVolume = Regex.IsMatch(text, @"\bvolume\b|\bvol\b", RegexOptions.IgnoreCase);
        var isMedia = isVolume || Regex.IsMatch(text, @"\b(music|media|play|pause|avr|receiver|denon|shield)\b", RegexOptions.IgnoreCase);
        var isLight = Regex.IsMatch(text, @"\blight\b|\bcolor\b|\bbright", RegexOptions.IgnoreCase);

        string? preferredEntity = null;
        if (isMedia && !isLight)
            preferredEntity = ResolveEntity(text, "media_player", session.LastMediaPlayerEntity, deviceCatalog);
        else if (isLight && !isMedia)
            preferredEntity = ResolveEntity(text, "light", session.LastLightEntity, deviceCatalog);
        else if (isVolume)
            preferredEntity = ResolveEntity(text, "media_player", session.LastMediaPlayerEntity, deviceCatalog);

        var friendlyName = preferredEntity is null ? null : FriendlyNameFor(preferredEntity, deviceCatalog);

        if (friendlyName is not null &&
            !text.Contains(friendlyName, StringComparison.OrdinalIgnoreCase) &&
            !text.Contains(preferredEntity!, StringComparison.OrdinalIgnoreCase))
        {
            return $"{text} on {friendlyName}";
        }

        return text;
    }

    public static string? ResolveEntity(
        string userText,
        string domain,
        string? sessionEntityForDomain,
        string deviceCatalog)
    {
        // Prefer name match from catalog against user text
        var fromCatalog = FindBestCatalogMatch(userText, domain, deviceCatalog);
        if (!string.IsNullOrWhiteSpace(fromCatalog))
            return fromCatalog;

        // Session entity of the right domain
        if (!string.IsNullOrWhiteSpace(sessionEntityForDomain) &&
            sessionEntityForDomain.StartsWith(domain + ".", StringComparison.OrdinalIgnoreCase))
            return sessionEntityForDomain;

        // Explicit entity_id in message
        var idMatch = Regex.Match(userText, $@"\b{Regex.Escape(domain)}\.[a-z0-9_]+\b", RegexOptions.IgnoreCase);
        if (idMatch.Success)
            return idMatch.Value.ToLowerInvariant();

        return null;
    }

    public static string? FindBestCatalogMatch(string userText, string domain, string deviceCatalog)
    {
        if (string.IsNullOrWhiteSpace(deviceCatalog) || string.IsNullOrWhiteSpace(userText))
            return null;

        // Lines like: "  • Helios Denon AVR -X1700H → media_player.denon_avr_x1700h_2 (idle)"
        var matches = new List<(string EntityId, string Friendly, int Score)>();
        foreach (Match m in Regex.Matches(
                     deviceCatalog,
                     @"[•\-\*]\s*(.+?)\s*→\s*([a-z0-9_]+\.[a-z0-9_]+)",
                     RegexOptions.IgnoreCase))
        {
            var friendly = m.Groups[1].Value.Trim();
            var entityId = m.Groups[2].Value.Trim();
            if (!entityId.StartsWith(domain + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            var score = ScoreNameMatch(userText, friendly, entityId);
            if (score > 0)
                matches.Add((entityId, friendly, score));
        }

        if (matches.Count == 0)
            return null;

        return matches.OrderByDescending(x => x.Score).First().EntityId;
    }

    public static string? FriendlyNameFor(string entityId, string deviceCatalog)
    {
        if (string.IsNullOrWhiteSpace(deviceCatalog))
            return null;

        var m = Regex.Match(
            deviceCatalog,
            @"[•\-\*]\s*(.+?)\s*→\s*" + Regex.Escape(entityId) + @"\b",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static int ScoreNameMatch(string userText, string friendly, string entityId)
    {
        var score = 0;
        var lower = userText.ToLowerInvariant();
        var fn = friendly.ToLowerInvariant();

        if (lower.Contains(fn, StringComparison.Ordinal))
            return 100 + fn.Length;

        // Token overlap (skip very short tokens)
        var tokens = Regex.Split(fn, @"[^a-z0-9]+")
            .Where(t => t.Length >= 3)
            .Distinct()
            .ToList();
        foreach (var t in tokens)
        {
            if (lower.Contains(t, StringComparison.Ordinal))
                score += t.Length;
        }

        // entity object part tokens
        var objectPart = entityId.Contains('.') ? entityId[(entityId.IndexOf('.') + 1)..] : entityId;
        foreach (var t in objectPart.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length >= 3 && lower.Contains(t, StringComparison.OrdinalIgnoreCase))
                score += t.Length;
        }

        return score;
    }

    private static string? ExtractColor(string text)
    {
        foreach (var c in ColorNames.OrderByDescending(x => x.Length))
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(c.Replace('_', ' '))}\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, $@"\b{Regex.Escape(c)}\b", RegexOptions.IgnoreCase))
                return c;
        }

        var m = Regex.Match(text, @"\b(white|red|green|blue|yellow|orange|purple|pink|cyan)\b(?:\s+color)?", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static string StripWakeWord(string message, string assistantName)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        var text = message.Trim();
        var name = (assistantName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return text;

        var patterns = new[]
        {
            $@"^(?:hey|hi|ok|okay)\s+{Regex.Escape(name)}\s*[,:]?\s*",
            $@"^{Regex.Escape(name)}\s*[,:]?\s*"
        };
        foreach (var p in patterns)
        {
            var stripped = Regex.Replace(text, p, "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!string.Equals(stripped, text, StringComparison.Ordinal))
                return stripped.Trim();
        }

        return text;
    }

    private static bool IsFailure(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return true;
        return result.StartsWith("HA error", StringComparison.OrdinalIgnoreCase)
               || result.StartsWith("Connection", StringComparison.OrdinalIgnoreCase)
               || result.StartsWith("Home Assistant is not configured", StringComparison.OrdinalIgnoreCase)
               || result.StartsWith("Domain and service", StringComparison.OrdinalIgnoreCase)
               || result.StartsWith("Smart home integration", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? "";
        return text[..max] + "…";
    }
}
