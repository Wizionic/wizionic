using ChatfishApp.Contracts;
using Microsoft.Extensions.AI;
using System.Net.Http.Json;
using System.Text.Json;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using TextContent = Microsoft.Extensions.AI.TextContent;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;
using DataContent = Microsoft.Extensions.AI.DataContent;

namespace ChatfishApp.Shared.Services;

/// <summary>
/// IChatClient that routes completions through the ASP.NET /api/proxy/chat endpoint.
/// </summary>
internal sealed class ServerProxyChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _providerId;
    private readonly string _modelId;

    public ServerProxyChatClient(HttpClient http, string providerId, string modelId)
    {
        _http = http;
        _providerId = providerId;
        _modelId = modelId;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<AiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ProxiedProviderContracts.ProxyChatRequest(
            _providerId,
            _modelId,
            BuildOpenAiMessages(messages),
            BuildOpenAiTools(options),
            options?.ToolMode == ChatToolMode.RequireAny ? "required" :
            options?.ToolMode == ChatToolMode.None ? "none" : null);

        using var response = await _http.PostAsJsonAsync("/api/proxy/chat", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Proxy returned {(int)response.StatusCode}: {body}");

        return ParseOpenAiChatResponse(body);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Streaming is not supported for server-proxied providers.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static List<Dictionary<string, object?>> BuildOpenAiMessages(IEnumerable<AiChatMessage> messages)
    {
        var result = new List<Dictionary<string, object?>>();

        foreach (var msg in messages)
        {
            var role = msg.Role == ChatRole.Tool ? "tool" : msg.Role.Value.ToLowerInvariant();
            var entry = new Dictionary<string, object?> { ["role"] = role };

            var textParts = new List<string>();
            var imageParts = new List<Dictionary<string, object?>>();
            List<Dictionary<string, object?>>? toolCalls = null;
            string? toolCallId = null;

            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                        textParts.Add(tc.Text);
                        break;
                    case DataContent dc:
                        var mime = string.IsNullOrWhiteSpace(dc.MediaType) ? "application/octet-stream" : dc.MediaType;
                        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(dc.Data.ToArray())}";
                        imageParts.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object?> { ["url"] = dataUrl }
                        });
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
                                ["arguments"] = OpenAiFunctionArgumentJson.SerializeArguments(fcc.Arguments)
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
                if (imageParts.Count > 0)
                {
                    var parts = new List<Dictionary<string, object?>>();
                    if (textParts.Count > 0)
                    {
                        parts.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = string.Join("\n", textParts)
                        });
                    }
                    parts.AddRange(imageParts);
                    entry["content"] = parts;
                }
                else
                {
                    entry["content"] = string.Join("\n", textParts);
                }

                if (toolCalls is { Count: > 0 })
                    entry["tool_calls"] = toolCalls;
            }

            result.Add(entry);
        }

        return result;
    }

    private static List<Dictionary<string, object?>>? BuildOpenAiTools(ChatOptions? options)
    {
        if (options?.Tools == null || options.Tools.Count == 0)
            return null;

        var tools = new List<Dictionary<string, object?>>();
        foreach (var tool in options.Tools)
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

        return tools.Count > 0 ? tools : null;
    }

    private static ChatResponse ParseOpenAiChatResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("Proxy response contained no choices.");

        var message = choices[0].GetProperty("message");
        var contents = new List<AIContent>();
        var reasoning = ExtractReasoningString(message);

        if (message.TryGetProperty("content", out var contentEl) &&
            contentEl.ValueKind != JsonValueKind.Null)
        {
            var content = contentEl.GetString();
            if (!string.IsNullOrWhiteSpace(content))
                contents.Add(new TextContent(content));
        }

        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in toolCalls.EnumerateArray())
            {
                if (!tc.TryGetProperty("function", out var fnEl)) continue;
                var name = fnEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var callId = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");

                var args = fnEl.TryGetProperty("arguments", out var argsEl)
                    ? OpenAiFunctionArgumentJson.ParseArgumentsJsonElement(argsEl)
                    : new Dictionary<string, object?>();

                if (!string.IsNullOrEmpty(name))
                    contents.Add(new FunctionCallContent(callId, name, args));
            }
        }

        if (contents.Count == 0)
            contents.Add(new TextContent(""));

        var assistantMessage = new AiChatMessage(ChatRole.Assistant, contents);
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            assistantMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            assistantMessage.AdditionalProperties["reasoning"] = reasoning;
        }

        return new ChatResponse(assistantMessage) { RawRepresentation = json };
    }

    private static string? ExtractReasoningString(JsonElement message)
    {
        foreach (var prop in message.EnumerateObject())
        {
            if (prop.Name.Equals("reasoning", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("reasoning_content", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("reasoning_text", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
        }

        return null;
    }
}