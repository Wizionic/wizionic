using System.Runtime.CompilerServices;
using System.Text.Json;
using App.Core.Chat;
using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace App.Shared.Services;

/// <summary>
/// Logs each inner HTTP round to <see cref="ChatHttpInspector"/> (before function-invocation looping).
/// </summary>
internal sealed class InspectingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly string _modelId;
    private readonly string _url;
    private int _round;

    public InspectingChatClient(IChatClient inner, string modelId, string url)
    {
        _inner = inner;
        _modelId = modelId;
        _url = url;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<AiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!ChatHttpInspector.Enabled)
            return await _inner.GetResponseAsync(messages, options, cancellationToken);

        var list = messages as IList<AiChatMessage> ?? messages.ToList();
        LogNewToolResults(list);
        var n = Interlocked.Increment(ref _round);
        var started = DateTime.UtcNow;
        try
        {
            var response = await _inner.GetResponseAsync(list, options, cancellationToken);
            ChatHttpInspector.Record(
                $"chat-{n}",
                "POST",
                _url,
                BuildRequest(list, options, n),
                JsonSerializer.Serialize(BuildResponse(response, cancelled: false)),
                200,
                (int)(DateTime.UtcNow - started).TotalMilliseconds,
                model: _modelId);
            return response;
        }
        catch (Exception ex)
        {
            ChatHttpInspector.Record(
                $"chat-{n}",
                "POST",
                _url,
                BuildRequest(list, options, n),
                null,
                null,
                (int)(DateTime.UtcNow - started).TotalMilliseconds,
                model: _modelId,
                error: ex.Message);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AiChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!ChatHttpInspector.Enabled)
        {
            await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
            yield break;
        }

        var list = messages as IList<AiChatMessage> ?? messages.ToList();
        LogNewToolResults(list);
        var n = Interlocked.Increment(ref _round);
        var started = DateTime.UtcNow;
        var updates = new List<ChatResponseUpdate>();
        Exception? error = null;
        var cancelled = false;

        IAsyncEnumerator<ChatResponseUpdate>? e = null;
        try
        {
            e = _inner.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool moved;
                try
                {
                    moved = await e.MoveNextAsync();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    error = ex;
                    break;
                }

                if (!moved)
                    break;
                updates.Add(e.Current);
                yield return e.Current;
            }
        }
        finally
        {
            if (e is not null)
                await e.DisposeAsync();

            ChatResponse? asResponse = null;
            try { asResponse = updates.Count > 0 ? updates.ToChatResponse() : null; }
            catch { /* incomplete stream */ }

            ChatHttpInspector.Record(
                $"chat-{n}",
                "POST",
                _url,
                BuildRequest(list, options, n),
                JsonSerializer.Serialize(BuildResponse(asResponse, cancelled, updates)),
                error is null ? 200 : null,
                (int)(DateTime.UtcNow - started).TotalMilliseconds,
                model: _modelId,
                error: error?.Message);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    private static void LogNewToolResults(IList<AiChatMessage> messages)
    {
        if (messages.Count == 0)
            return;

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            foreach (var fc in m.Contents.OfType<FunctionCallContent>())
            {
                if (!string.IsNullOrEmpty(fc.CallId))
                    names[fc.CallId] = fc.Name;
            }
        }

        var last = messages[^1];
        foreach (var fr in last.Contents.OfType<FunctionResultContent>())
        {
            names.TryGetValue(fr.CallId ?? "", out var name);
            RecordToolInvoke(name ?? fr.CallId ?? "tool", null, fr.Result);
        }
    }

    internal static void RecordToolInvoke(string name, object? arguments, object? result, string? error = null)
    {
        if (!ChatHttpInspector.Enabled)
            return;

        ChatHttpInspector.Record(
            "tool",
            "INVOKE",
            name,
            new Dictionary<string, object?>
            {
                ["name"] = name,
                ["arguments"] = arguments
            },
            result is null ? null : JsonSerializer.Serialize(result),
            error is null ? 200 : 0,
            0,
            error: error);
    }

    private static Dictionary<string, object?> BuildRequest(
        IList<AiChatMessage> messages,
        ChatOptions? options,
        int round)
    {
        var msgDump = messages.Select(DumpMessage).ToList();
        var tools = new List<object>();
        if (options?.Tools is { Count: > 0 } tlist)
        {
            foreach (var fn in tlist.OfType<AIFunction>())
            {
                object? schema = null;
                try
                {
                    if (fn.JsonSchema.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                        schema = JsonSerializer.Deserialize<object>(fn.JsonSchema.GetRawText());
                }
                catch { /* optional */ }

                tools.Add(new Dictionary<string, object?>
                {
                    ["name"] = fn.Name,
                    ["description"] = fn.Description,
                    ["parameters"] = schema
                });
            }
        }

        return new Dictionary<string, object?>
        {
            ["round"] = round,
            ["message_count"] = msgDump.Count,
            ["tool_count"] = tools.Count,
            ["max_tokens"] = options?.MaxOutputTokens,
            ["messages"] = msgDump,
            ["tools"] = tools
        };
    }

    private static Dictionary<string, object?> DumpMessage(AiChatMessage m)
    {
        var text = string.Join("\n", m.Contents.OfType<TextContent>().Select(t => t.Text ?? ""));
        var toolCalls = m.Contents.OfType<FunctionCallContent>().Select(fc => new Dictionary<string, object?>
        {
            ["id"] = fc.CallId,
            ["name"] = fc.Name,
            ["arguments"] = fc.Arguments
        }).ToList();
        var toolResults = m.Contents.OfType<FunctionResultContent>().Select(fr => new Dictionary<string, object?>
        {
            ["call_id"] = fr.CallId,
            ["result"] = fr.Result
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["role"] = m.Role.Value,
            ["chars"] = text.Length,
            ["content"] = string.IsNullOrEmpty(text) ? null : text,
            ["tool_calls"] = toolCalls.Count > 0 ? toolCalls : null,
            ["tool_results"] = toolResults.Count > 0 ? toolResults : null,
            ["binary"] = m.Contents.Any(c => c is DataContent) ? "omitted" : null
        };
    }

    private static Dictionary<string, object?> BuildResponse(
        ChatResponse? response,
        bool cancelled,
        IReadOnlyList<ChatResponseUpdate>? updates = null)
    {
        var text = response?.Text;
        if (string.IsNullOrWhiteSpace(text) && updates is { Count: > 0 })
            text = string.Concat(updates.Select(u => u.Text));

        var toolCalls = new List<Dictionary<string, object?>>();
        if (response is not null)
        {
            foreach (var m in response.Messages)
            {
                foreach (var fc in m.Contents.OfType<FunctionCallContent>())
                    toolCalls.Add(new Dictionary<string, object?>
                    {
                        ["id"] = fc.CallId,
                        ["name"] = fc.Name,
                        ["arguments"] = fc.Arguments
                    });
            }
        }
        else if (updates is not null)
        {
            foreach (var u in updates)
            {
                foreach (var fc in u.Contents.OfType<FunctionCallContent>())
                    toolCalls.Add(new Dictionary<string, object?>
                    {
                        ["id"] = fc.CallId,
                        ["name"] = fc.Name,
                        ["arguments"] = fc.Arguments
                    });
            }
        }

        return new Dictionary<string, object?>
        {
            ["cancelled"] = cancelled,
            ["chars"] = text?.Length ?? 0,
            ["text"] = text,
            ["tool_calls"] = toolCalls.Count > 0 ? toolCalls : null,
            ["input_tokens"] = response?.Usage?.InputTokenCount,
            ["output_tokens"] = response?.Usage?.OutputTokenCount,
            ["total_tokens"] = response?.Usage?.TotalTokenCount
        };
    }
}
