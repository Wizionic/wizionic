using ChatfishApp.Contracts;
using Microsoft.Extensions.AI;
using System.Net.Http.Json;
using System.Text.Json;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIContent = Microsoft.Extensions.AI.AIContent;
using TextContent = Microsoft.Extensions.AI.TextContent;
using DataContent = Microsoft.Extensions.AI.DataContent;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;
using StoreChatMessage = ChatfishApp.Client.Services.WasmConversationStore.ChatMessage;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Shared chat completion logic used by Chat.razor (local) and WasmSyncService (remote AI proxy server).
/// </summary>
public class WasmChatCompletionService
{
    private readonly WasmAiProviderService _aiProvider;
    private readonly WasmKeyStore _keyStore;
    private readonly ChatfishApp.Services.Tools.IToolProvider _toolProvider;
    private static readonly HttpClient OllamaHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    public WasmChatCompletionService(
        WasmAiProviderService aiProvider,
        WasmKeyStore keyStore,
        ChatfishApp.Services.Tools.IToolProvider toolProvider)
    {
        _aiProvider = aiProvider;
        _keyStore = keyStore;
        _toolProvider = toolProvider;
    }

    public record ChatCompletionResult(string Text, string ToolTrace, string? Error);

    public async Task<ChatCompletionResult> CompleteAsync(
        string modelId,
        IReadOnlyList<StoreChatMessage> messages,
        string? currentUser = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(modelId))
            return new ChatCompletionResult("", "", "No model selected.");

        var modelInfo = _aiProvider.GetAvailableModels().FirstOrDefault(m => m.Id == modelId);
        bool supportsTools = modelInfo?.SupportsTools ?? ProviderCatalog.GetCapabilitiesForModel(modelId).SupportsTools;
        bool supportsVision = modelInfo?.SupportsVision ?? ProviderCatalog.GetCapabilitiesForModel(modelId).SupportsVision;

        try
        {
            var chatHistory = BuildChatHistory(messages, currentUser, supportsVision);

            ChatfishApp.Services.Tools.ToolExecutionTrace.Clear();

            var baseClient = _aiProvider.GetChatClientForModel(modelId);
            IChatClient client = baseClient;
            ChatOptions? chatOptions = null;

            if (supportsTools)
            {
                client = baseClient
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();

                ChatfishApp.Services.Tools.ToolExecutionTrace.Clear();
                chatOptions = new ChatOptions { Tools = _toolProvider.GetTools().ToList() };
            }

            const int maxAttempts = 3;
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var response = await client.GetResponseAsync(chatHistory, chatOptions ?? new ChatOptions(), ct);

                    var toolTrace = string.Join("\n", ChatfishApp.Services.Tools.ToolExecutionTrace.GetCurrentTrace());

                    var responseText = response.Text ?? "";
                    string? extractSource = null;

                    // Ollama reasoning models put answers in a "reasoning" JSON field the OpenAI SDK drops.
                    // Query Ollama directly (raw JSON) before any heuristic JSON mining that can mis-read
                    // fields like reasoning_effort: 0 as the literal answer "0".
                    if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(responseText) || IsBogusExtractedText(responseText)))
                    {
                        var historyForFallback = BuildFallbackHistory(chatHistory, response.Messages);
                        var (fallbackText, fallbackSource) = await TryOllamaRawCompletionAsync(
                            modelId, historyForFallback, supportsTools, ct);
                        if (!string.IsNullOrWhiteSpace(fallbackText))
                        {
                            responseText = fallbackText;
                            extractSource = fallbackSource;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(responseText))
                    {
                        var (extracted, source) = ExtractResponseText(response);
                        if (!string.IsNullOrWhiteSpace(extracted) && !IsBogusExtractedText(extracted))
                        {
                            responseText = extracted;
                            extractSource = source;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(extractSource))
                        ChatfishApp.Services.Tools.ToolExecutionTrace.Record($"ℹ️ Extracted final response from {extractSource}.");

                    toolTrace = string.Join("\n", ChatfishApp.Services.Tools.ToolExecutionTrace.GetCurrentTrace());
                    var text = string.IsNullOrWhiteSpace(responseText) ? "No response." : responseText;
                    return new ChatCompletionResult(text, toolTrace, null);
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    lastEx = ex;
                    string msg = ex.Message.ToLowerInvariant();
                    if (msg.Contains("429") || msg.Contains("too many requests") || msg.Contains("rate limit"))
                    {
                        int delayMs = attempt * 1200;
                        ChatfishApp.Services.Tools.ToolExecutionTrace.Record($"⚠️ Provider rate limit (429) on attempt {attempt}. Waiting {delayMs}ms before retry...");
                        await Task.Delay(delayMs, ct);
                        continue;
                    }
                    throw;
                }
            }

            var exhaustedTrace = string.Join("\n", ChatfishApp.Services.Tools.ToolExecutionTrace.GetCurrentTrace());
            return new ChatCompletionResult(
                "",
                exhaustedTrace,
                $"Error calling provider after retries: {lastEx?.Message ?? "unknown"}");
        }
        catch (Exception ex)
        {
            return new ChatCompletionResult("", "", $"Error calling provider: {ex.Message}");
        }
    }

    private static List<AiChatMessage> BuildChatHistory(
        IReadOnlyList<StoreChatMessage> messages,
        string? currentUser,
        bool supportsVision)
    {
        var chatHistory = new List<AiChatMessage>();

        foreach (var m in messages)
        {
            bool hasContent = !string.IsNullOrWhiteSpace(m.Content);
            bool hasAttachments = m.Attachments != null && m.Attachments.Any();
            if (!hasContent && !hasAttachments) continue;

            var roleStr = m.Role ?? (m.User == currentUser ? "user" : "assistant");
            var aiRole = roleStr.Equals("user", StringComparison.OrdinalIgnoreCase) ? ChatRole.User : ChatRole.Assistant;

            if (aiRole == ChatRole.User && hasAttachments && supportsVision)
            {
                var contents = new List<AIContent>();
                if (hasContent)
                    contents.Add(new TextContent(CleanTextForLlm(m.Content)));

                foreach (var att in m.Attachments!)
                {
                    if (att.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                        att.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(att.DataBase64);
                            contents.Add(new DataContent(bytes, att.ContentType));
                        }
                        catch { /* skip bad attachment */ }
                    }
                }

                if (contents.Count > 0)
                    chatHistory.Add(new AiChatMessage(aiRole, contents));
            }
            else if (hasContent)
            {
                chatHistory.Add(new AiChatMessage(aiRole, CleanTextForLlm(m.Content)));
            }
        }

        return chatHistory;
    }

    private static string CleanTextForLlm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (!text.Contains('<')) return text;
        var plain = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(plain).Trim();
    }

    /// <summary>
    /// Ollama reasoning models (e.g. LFM 2.5) often return an empty standard "content" field
    /// and put the user-visible answer in "reasoning" / "reasoning_content". ME.AI's response.Text
    /// only sees standard content, so we fall back through several extraction paths.
    /// </summary>
    private static (string Text, string? Source) ExtractResponseText(ChatResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Text))
            return (response.Text, null);

        if (response.Messages != null)
        {
            foreach (var msg in response.Messages.Where(m => m.Role == ChatRole.Assistant).Reverse())
            {
                var (text, source) = ExtractTextFromAssistantMessage(msg);
                if (!string.IsNullOrWhiteSpace(text))
                    return (text, source);
            }
        }

        var fromRaw = TryExtractReasoningFieldFromObject(response.RawRepresentation);
        if (!string.IsNullOrWhiteSpace(fromRaw))
            return (fromRaw!, "raw response");

        var fromResponse = TryExtractReasoningFieldFromObject(response);
        if (!string.IsNullOrWhiteSpace(fromResponse))
            return (fromResponse!, "serialized response");

        return ("", null);
    }

    private static (string Text, string? Source) ExtractTextFromAssistantMessage(AiChatMessage msg)
    {
        var contentText = string.Join("\n",
            msg.Contents
                .OfType<TextContent>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

        if (!string.IsNullOrWhiteSpace(contentText))
            return (contentText, "message content");

        if (msg.AdditionalProperties != null)
        {
            foreach (var key in new[] { "reasoning", "reasoning_content", "reasoning_text" })
            {
                if (msg.AdditionalProperties.TryGetValue(key, out var val))
                {
                    var s = CoerceToString(val);
                    if (!string.IsNullOrWhiteSpace(s))
                        return (s!, $"field '{key}'");
                }
            }

        }

        var fromRaw = TryExtractReasoningFieldFromObject(msg.RawRepresentation);
        if (!string.IsNullOrWhiteSpace(fromRaw))
            return (fromRaw!, "message raw representation");

        return ("", null);
    }

    private static string? TryExtractReasoningFieldFromObject(object? raw)
    {
        if (raw == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(raw);
            using var doc = JsonDocument.Parse(json);
            return FindReasoningStringInJson(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindReasoningStringInJson(JsonElement element, int depth = 0)
    {
        if (depth > 12) return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Prefer well-known reasoning keys on this object (any casing) before recursing.
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsReasoningFieldName(prop.Name))
                    {
                        var s = CoerceReasoningString(prop.Value);
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }

                foreach (var prop in element.EnumerateObject())
                {
                    var found = FindReasoningStringInJson(prop.Value, depth + 1);
                    if (!string.IsNullOrWhiteSpace(found)) return found;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var found = FindReasoningStringInJson(item, depth + 1);
                    if (!string.IsNullOrWhiteSpace(found)) return found;
                }
                break;
        }

        return null;
    }

    private static bool IsReasoningFieldName(string name) =>
        name.Equals("reasoning", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("reasoning_content", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("reasoning_text", StringComparison.OrdinalIgnoreCase);

    private static bool IsBogusExtractedText(string text)
    {
        var t = text.Trim();
        return t is "0" or "1" or "stop" or "length" or "tool_calls" or "auto" or "none";
    }

    private static string? CoerceToString(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;
        if (value is JsonElement el)
            return CoerceReasoningString(el);
        return value.ToString();
    }

    private static string? CoerceReasoningString(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }

    private static List<AiChatMessage> BuildFallbackHistory(
        IList<AiChatMessage> chatHistory,
        IList<AiChatMessage>? responseMessages)
    {
        var merged = new List<AiChatMessage>(chatHistory);
        if (responseMessages == null) return merged;

        foreach (var msg in responseMessages)
        {
            if (IsEmptyAssistant(msg)) continue;
            merged.Add(msg);
        }
        return merged;
    }

    /// <summary>
    /// Direct Ollama /v1/chat/completions call to read raw JSON (content + reasoning fields).
    /// The OpenAI SDK used by ME.AI drops Ollama's "reasoning" field during deserialization.
    /// </summary>
    private async Task<(string Text, string? Source)> TryOllamaRawCompletionAsync(
        string modelId,
        IList<AiChatMessage> messages,
        bool supportsTools,
        CancellationToken ct)
    {
        try
        {
            var ollamaModel = modelId.Split('/', 2)[1];
            var baseUrl = _keyStore.OllamaBaseUrl.TrimEnd('/') + "/v1/chat/completions";

            var ollamaMessages = BuildOllamaMessages(messages);
            if (ollamaMessages.Count == 0)
                return ("", null);

            var toolDefs = supportsTools ? BuildOllamaToolDefinitions() : null;

            for (int round = 0; round < 5; round++)
            {
                var body = new Dictionary<string, object?>
                {
                    ["model"] = ollamaModel,
                    ["messages"] = ollamaMessages,
                    ["stream"] = false
                };
                if (toolDefs is { Count: > 0 })
                    body["tools"] = toolDefs;

                using var resp = await OllamaHttp.PostAsJsonAsync(baseUrl, body, ct);
                if (!resp.IsSuccessStatusCode)
                    return ("", null);

                var json = await resp.Content.ReadAsStringAsync(ct);
                var (text, toolCalls) = ParseOllamaCompletionFull(json);

                if (!string.IsNullOrWhiteSpace(text))
                    return (text, "Ollama raw response");

                if (toolCalls is not { Count: > 0 })
                    return ("", null);

                ollamaMessages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = "",
                    ["tool_calls"] = toolCalls.Select(tc => new Dictionary<string, object?>
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = tc.ArgumentsJson
                        }
                    }).ToList()
                });

                foreach (var tc in toolCalls)
                {
                    var result = await InvokeToolAsync(tc.Name, tc.ArgumentsJson, ct);
                    ollamaMessages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = tc.Id,
                        ["content"] = result
                    });
                }
            }

            return ("", null);
        }
        catch (Exception ex)
        {
            ChatfishApp.Services.Tools.ToolExecutionTrace.Record($"[debug] Ollama raw fallback failed: {ex.Message}");
            return ("", null);
        }
    }

    private List<Dictionary<string, object?>> BuildOllamaToolDefinitions()
    {
        var tools = new List<Dictionary<string, object?>>();
        foreach (var tool in _toolProvider.GetTools())
        {
            if (tool is not AIFunction fn) continue;
            tools.Add(new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = fn.Name,
                    ["description"] = fn.Description ?? fn.Name,
                    ["parameters"] = fn.JsonSchema.ValueKind != JsonValueKind.Undefined
                        ? JsonSerializer.Deserialize<object>(fn.JsonSchema.GetRawText()) ?? new { type = "object", properties = new { } }
                        : new { type = "object", properties = new { } }
                }
            });
        }
        return tools;
    }

    private async Task<string> InvokeToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        var fn = _toolProvider.GetTools().OfType<AIFunction>().FirstOrDefault(f => f.Name == name);
        if (fn == null)
            return $"Tool '{name}' is not available.";

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var args = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();

            var result = await fn.InvokeAsync(new AIFunctionArguments(args), ct);
            return result?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            return $"Tool '{name}' failed: {ex.Message}";
        }
    }

    private record OllamaToolCall(string Id, string Name, string ArgumentsJson);

    private static List<Dictionary<string, object?>> BuildOllamaMessages(IList<AiChatMessage> messages)
    {
        var result = new List<Dictionary<string, object?>>();

        foreach (var msg in messages)
        {
            if (IsEmptyAssistant(msg))
                continue;

            var role = msg.Role.Value.ToLowerInvariant();
            if (msg.Role == ChatRole.Tool)
                role = "tool";

            var entry = new Dictionary<string, object?> { ["role"] = role };

            var textParts = new List<string>();
            List<Dictionary<string, object?>>? toolCalls = null;
            string? toolCallId = null;

            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                        textParts.Add(tc.Text);
                        break;
                    case FunctionCallContent fcc:
                        toolCalls ??= new List<Dictionary<string, object?>>();
                        toolCalls.Add(new Dictionary<string, object?>
                        {
                            ["id"] = fcc.CallId,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = fcc.Name,
                                ["arguments"] = JsonSerializer.Serialize(fcc.Arguments)
                            }
                        });
                        break;
                    case FunctionResultContent frc:
                        toolCallId = frc.CallId;
                        textParts.Add(frc.Result?.ToString() ?? "");
                        break;
                }
            }

            if (role == "tool")
            {
                entry["content"] = string.Join("\n", textParts);
                if (toolCallId != null) entry["tool_call_id"] = toolCallId;
            }
            else
            {
                entry["content"] = string.Join("\n", textParts);
                if (toolCalls is { Count: > 0 })
                    entry["tool_calls"] = toolCalls;
            }

            result.Add(entry);
        }

        // Drop a trailing empty assistant left by the SDK after tool rounds.
        while (result.Count > 0)
        {
            var last = result[^1];
            if (last.TryGetValue("role", out var r) && r?.ToString() == "assistant" &&
                (!last.ContainsKey("tool_calls") || last["tool_calls"] is null) &&
                string.IsNullOrWhiteSpace(last.GetValueOrDefault("content")?.ToString()))
            {
                result.RemoveAt(result.Count - 1);
                continue;
            }
            break;
        }

        return result;
    }

    private static bool IsEmptyAssistant(AiChatMessage msg)
    {
        if (msg.Role != ChatRole.Assistant) return false;
        bool hasText = msg.Contents.OfType<TextContent>().Any(t => !string.IsNullOrWhiteSpace(t.Text));
        bool hasTools = msg.Contents.OfType<FunctionCallContent>().Any();
        return !hasText && !hasTools;
    }

    private static (string? Text, List<OllamaToolCall>? ToolCalls) ParseOllamaCompletionFull(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return (null, null);

            var message = choices[0].GetProperty("message");

            string? text = null;
            if (message.TryGetProperty("content", out var contentEl))
            {
                var content = contentEl.GetString();
                if (!string.IsNullOrWhiteSpace(content))
                    text = content;
            }

            if (text == null)
            {
                foreach (var prop in message.EnumerateObject())
                {
                    if (IsReasoningFieldName(prop.Name))
                    {
                        text = CoerceReasoningString(prop.Value);
                        if (!string.IsNullOrWhiteSpace(text)) break;
                    }
                }
            }

            List<OllamaToolCall>? toolCalls = null;
            if (message.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcArr.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    if (!tc.TryGetProperty("function", out var fnEl)) continue;
                    var name = fnEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var args = fnEl.TryGetProperty("arguments", out var argsEl) ? argsEl.GetRawText() : "{}";
                    if (string.IsNullOrEmpty(name)) continue;
                    toolCalls ??= new List<OllamaToolCall>();
                    toolCalls.Add(new OllamaToolCall(id, name, args));
                }
            }

            return (text, toolCalls);
        }
        catch
        {
            return (null, null);
        }
    }
}