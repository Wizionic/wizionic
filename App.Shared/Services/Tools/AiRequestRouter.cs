using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Storage;
using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace App.Shared.Services.Tools;

/// <summary>
/// Classifies which tool modules to attach using a small/fast chat model (no tools on the router call).
/// Falls back to rules / smart-home heuristics on timeout, empty reply, or unparseable output.
/// </summary>
public sealed class AiRequestRouter
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);
    /// <summary>Thinking models (Qwen) may burn tokens on reasoning before JSON — allow headroom.</summary>
    private const int MaxOutputTokens = 384;

    private static readonly HashSet<string> KnownModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Native", "Lemonade", "Gallery", "HomeAssistant", "BrowserAgent"
    };

    private readonly ChatModelCatalogService _catalog;
    private readonly IKeyStore _keyStore;
    private readonly ContextualRequestRouter _rules;

    public AiRequestRouter(
        ChatModelCatalogService catalog,
        IKeyStore keyStore,
        ContextualRequestRouter rules)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public async Task<RequestRoute> ClassifyAsync(
        string message,
        IReadOnlyList<IToolModule> activeModules,
        string? conversationId,
        string sourceLabel,
        CancellationToken ct = default)
    {
        // No HA session stickiness on the AI path — sticky sessions steal weather/etc. after a light command.
        var fallback = _rules.ClassifyRules(
            message, activeModules, conversationId, useSessionStickiness: false);

        var modelId = (_keyStore.ToolRoutingModelId ?? "").Trim();
        if (string.IsNullOrEmpty(modelId))
            return Relabel(fallback, sourceLabel + "→Rules", "no routing model");

        var available = activeModules
            .Where(m => m.IsAvailable)
            .Select(m => m.ModuleName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (available.Count == 0)
            return RequestRoute.PureChat("no modules available", sourceLabel + "→Rules");

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultTimeout);

            var client = _catalog.GetChatClientForModel(modelId);
            var prompt = BuildPrompt(message, available);
            var history = new List<AiChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, prompt)
            };
            var options = new ChatOptions { MaxOutputTokens = MaxOutputTokens };

            var response = await client.GetResponseAsync(history, options, timeoutCts.Token);
            var text = ExtractText(response);
            var parsed = TryParseRoute(text, available, sourceLabel);
            if (parsed != null)
                return MergeHardConstraints(parsed, fallback, available, sourceLabel);

            // AI empty / non-JSON: prefer rules if strong; else smart-home heuristic without wake word.
            var soft = TrySoftHomeAssistant(message, available, sourceLabel);
            if (soft != null && !fallback.HasTools)
                return soft;

            if (fallback.HasTools || fallback.TargetModule != null)
                return Relabel(
                    fallback,
                    sourceLabel + "→Rules",
                    string.IsNullOrWhiteSpace(text)
                        ? "AI empty reply; used rules"
                        : "AI reply not JSON; used rules");

            if (soft != null)
                return soft;

            return Relabel(
                fallback,
                sourceLabel + "→Rules",
                string.IsNullOrWhiteSpace(text)
                    ? "AI empty reply"
                    : "AI reply not JSON: " + Truncate(text, 60));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var soft = TrySoftHomeAssistant(message, available, sourceLabel);
            if (soft != null && !fallback.HasTools)
                return soft;
            return Relabel(fallback, sourceLabel + "→Rules", "AI timeout");
        }
        catch (Exception ex)
        {
            var soft = TrySoftHomeAssistant(message, available, sourceLabel);
            if (soft != null && !fallback.HasTools)
                return soft;
            return Relabel(fallback, sourceLabel + "→Rules", "AI error: " + Truncate(ex.Message, 50));
        }
    }

    private static RequestRoute Relabel(RequestRoute route, string source, string note)
    {
        var reason = string.IsNullOrWhiteSpace(route.Reason)
            ? note
            : route.Reason + " · " + note;
        return route with { Source = source, Reason = reason };
    }

    /// <summary>
    /// When AI fails to return JSON, still route obvious smart-home requests without requiring a wake word.
    /// (Wake word remains the rules-only gate.)
    /// </summary>
    private static RequestRoute? TrySoftHomeAssistant(
        string message,
        IReadOnlyList<string> available,
        string sourceLabel)
    {
        if (!available.Contains("HomeAssistant", StringComparer.OrdinalIgnoreCase))
            return null;
        if (!ContextualRequestRouter.MessageSuggestsHomeAssistant(message))
            return null;

        var modules = new List<string> { "HomeAssistant" };
        if (available.Contains("Native", StringComparer.OrdinalIgnoreCase))
            modules.Add("Native");

        return RequestRoute.WithModules(
            modules,
            "smart-home intent (AI fallback heuristic)",
            targetModule: "HomeAssistant",
            includeMcp: true,
            source: sourceLabel + "→HA-heuristic");
    }

    /// <summary>HA / Browser hard routes from rules (wake word / session) always win.</summary>
    private static RequestRoute MergeHardConstraints(
        RequestRoute ai,
        RequestRoute rules,
        IReadOnlyList<string> available,
        string sourceLabel)
    {
        if (rules.TargetModule is "HomeAssistant" or "BrowserAgent")
        {
            var modules = new List<string>(rules.Modules);
            foreach (var m in ai.Modules)
            {
                if (available.Contains(m, StringComparer.OrdinalIgnoreCase)
                    && !modules.Contains(m, StringComparer.OrdinalIgnoreCase))
                    modules.Add(m);
            }

            return RequestRoute.WithModules(
                modules,
                ai.Reason ?? rules.Reason ?? "AI + session/wake",
                targetModule: rules.TargetModule,
                includeMcp: true,
                source: sourceLabel + "→AI+session");
        }

        return ai;
    }

    private static string SystemPrompt =>
        "You are a tool router. Reply with ONLY one JSON object. No markdown, no prose, no thinking tags. " +
        "Schema: {\"modules\":[\"...\"],\"pure_chat\":false,\"target_module\":null,\"reason\":\"short\"}. " +
        "modules must be from the available list only. " +
        "pure_chat=true only for general chat/coding with no tools. " +
        "HomeAssistant: lights, switches, climate, media players, covers, scenes, locks, vacuum, " +
        "brightness, color, turn on/off, volume, thermostat — even without a wake word. " +
        "When using HomeAssistant set target_module to \"HomeAssistant\" and include it in modules. " +
        "Lemonade: draw/generate/create images. Gallery: albums/save photos. " +
        "Native: weather, search, time, math, URLs. " +
        "BrowserAgent: control embedded browser only.";

    private static string BuildPrompt(string message, IReadOnlyList<string> available)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Available modules: " + string.Join(", ", available));
        sb.AppendLine("Example HomeAssistant: {\"modules\":[\"HomeAssistant\",\"Native\"],\"pure_chat\":false,\"target_module\":\"HomeAssistant\",\"reason\":\"kitchen light\"}");
        sb.AppendLine("Example weather: {\"modules\":[\"Native\"],\"pure_chat\":false,\"target_module\":null,\"reason\":\"weather\"}");
        sb.AppendLine("Example chat: {\"modules\":[],\"pure_chat\":true,\"target_module\":null,\"reason\":\"chit-chat\"}");
        sb.AppendLine();
        sb.AppendLine("User message:");
        sb.AppendLine(message.Length > 1500 ? message[..1500] : message);
        sb.AppendLine();
        sb.AppendLine("JSON only:");
        return sb.ToString();
    }

    private static RequestRoute? TryParseRoute(string? text, IReadOnlyList<string> available, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var json = ExtractJsonObject(text);
        if (json == null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var pureChat = root.TryGetProperty("pure_chat", out var pc) &&
                           (pc.ValueKind == JsonValueKind.True
                            || (pc.ValueKind == JsonValueKind.String
                                && bool.TryParse(pc.GetString(), out var b) && b));

            string? reason = null;
            if (root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                reason = r.GetString();

            string? target = null;
            if (root.TryGetProperty("target_module", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var ts = t.GetString()?.Trim();
                if (string.Equals(ts, "HomeAssistant", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ts, "BrowserAgent", StringComparison.OrdinalIgnoreCase))
                    target = string.Equals(ts, "HomeAssistant", StringComparison.OrdinalIgnoreCase)
                        ? "HomeAssistant"
                        : "BrowserAgent";
                else if (string.Equals(ts, "null", StringComparison.OrdinalIgnoreCase)
                         || string.IsNullOrWhiteSpace(ts))
                    target = null;
            }

            var modules = new List<string>();
            if (!pureChat && root.TryGetProperty("modules", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var name = NormalizeModuleName(item.GetString());
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!available.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!modules.Contains(name, StringComparer.OrdinalIgnoreCase))
                        modules.Add(name);
                }
            }

            // Infer target from modules when model forgot target_module
            if (target == null)
            {
                if (modules.Any(m => m.Equals("HomeAssistant", StringComparison.OrdinalIgnoreCase)))
                    target = "HomeAssistant";
                else if (modules.Any(m => m.Equals("BrowserAgent", StringComparison.OrdinalIgnoreCase)))
                    target = "BrowserAgent";
            }

            // Ensure target module is in the list
            if (target != null
                && available.Contains(target, StringComparer.OrdinalIgnoreCase)
                && !modules.Contains(target, StringComparer.OrdinalIgnoreCase))
                modules.Insert(0, target);

            if (modules.Count > 0
                && available.Contains("Native", StringComparer.OrdinalIgnoreCase)
                && !modules.Contains("Native", StringComparer.OrdinalIgnoreCase))
                modules.Add("Native");

            if (pureChat || modules.Count == 0)
                return RequestRoute.PureChat(reason ?? "AI pure chat", sourceLabel + "→AI");

            var includeMcp = target != null;
            return RequestRoute.WithModules(
                modules,
                reason ?? "AI classification",
                targetModule: target,
                includeMcp: includeMcp,
                source: sourceLabel + "→AI");
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeModuleName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var n = raw.Trim().Trim('"', '\'');
        // Tolerate snake_case / spaced names from weak models
        n = n.Replace(" ", "").Replace("_", "");
        foreach (var known in KnownModules)
        {
            if (string.Equals(known.Replace("_", ""), n, StringComparison.OrdinalIgnoreCase)
                || string.Equals(known, raw.Trim(), StringComparison.OrdinalIgnoreCase))
                return known;
        }
        // Pass through if it matches available casing later
        return raw.Trim();
    }

    private static string? ExtractJsonObject(string text)
    {
        var t = text.Trim();

        // Drop common thinking wrappers
        t = Regex.Replace(t, @"<think>[\s\S]*?</think>", " ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<reasoning>[\s\S]*?</reasoning>", " ", RegexOptions.IgnoreCase);

        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = t.IndexOf('\n');
            if (firstNl > 0) t = t[(firstNl + 1)..];
            var fence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) t = t[..fence];
            t = t.Trim();
        }

        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return t[start..(end + 1)];
    }

    /// <summary>
    /// Small thinking models often put the only useful text in reasoning fields with empty content.
    /// </summary>
    private static string ExtractText(ChatResponse response)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(response.Text))
            sb.AppendLine(response.Text);

        if (response.Messages != null)
        {
            foreach (var msg in response.Messages)
            {
                foreach (var c in msg.Contents)
                {
                    if (c is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                        sb.AppendLine(tc.Text);
                }

                if (msg.AdditionalProperties != null)
                {
                    foreach (var key in new[] { "reasoning", "reasoning_content", "reasoning_text" })
                    {
                        if (msg.AdditionalProperties.TryGetValue(key, out var val) && val != null)
                        {
                            var s = val as string ?? val.ToString();
                            if (!string.IsNullOrWhiteSpace(s))
                                sb.AppendLine(s);
                        }
                    }
                }

                TryAppendRawReasoning(msg.RawRepresentation, sb);
            }
        }

        return sb.ToString().Trim();
    }

    private static void TryAppendRawReasoning(object? raw, StringBuilder sb)
    {
        if (raw is null) return;
        try
        {
            if (raw is JsonElement el)
            {
                AppendReasoningFromJson(el, sb, 0);
                return;
            }

            var json = JsonSerializer.Serialize(raw);
            using var doc = JsonDocument.Parse(json);
            AppendReasoningFromJson(doc.RootElement, sb, 0);
        }
        catch { /* ignore */ }
    }

    private static void AppendReasoningFromJson(JsonElement el, StringBuilder sb, int depth)
    {
        if (depth > 8) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Name.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Equals("content", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Equals("text", StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            var s = p.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                                sb.AppendLine(s);
                        }
                    }
                    AppendReasoningFromJson(p.Value, sb, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    AppendReasoningFromJson(item, sb, depth + 1);
                break;
        }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
