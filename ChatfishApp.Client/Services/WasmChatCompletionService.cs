using ChatfishApp.Contracts;
using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIContent = Microsoft.Extensions.AI.AIContent;
using TextContent = Microsoft.Extensions.AI.TextContent;
using DataContent = Microsoft.Extensions.AI.DataContent;
using StoreChatMessage = ChatfishApp.Client.Services.WasmConversationStore.ChatMessage;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Shared chat completion logic used by Chat.razor (local) and WasmSyncService (remote AI proxy server).
/// </summary>
public class WasmChatCompletionService
{
    private readonly WasmAiProviderService _aiProvider;
    private readonly ChatfishApp.Services.Tools.IToolProvider _toolProvider;

    public WasmChatCompletionService(WasmAiProviderService aiProvider, ChatfishApp.Services.Tools.IToolProvider toolProvider)
    {
        _aiProvider = aiProvider;
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
                    string responseText = response.Text ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(responseText))
                    {
                        var lastAssistant = response.Messages?
                            .LastOrDefault(m => m.Role == ChatRole.Assistant);

                        if (lastAssistant?.AdditionalProperties != null)
                        {
                            var availableKeys = string.Join(", ", lastAssistant.AdditionalProperties.Keys);
                            ChatfishApp.Services.Tools.ToolExecutionTrace.Record($"[debug] AdditionalProperties keys on assistant message: [{availableKeys}]");

                            foreach (var key in lastAssistant.AdditionalProperties.Keys)
                            {
                                if (key.Contains("reason", StringComparison.OrdinalIgnoreCase))
                                {
                                    var value = lastAssistant.AdditionalProperties[key];
                                    string? reasonText = value as string;

                                    if (reasonText is null && value is System.Text.Json.JsonElement jsonEl && jsonEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                        reasonText = jsonEl.GetString();

                                    if (!string.IsNullOrWhiteSpace(reasonText))
                                    {
                                        responseText = reasonText!;
                                        ChatfishApp.Services.Tools.ToolExecutionTrace.Record($"ℹ️ Extracted final response from non-standard field '{key}'.");
                                        break;
                                    }
                                }
                            }
                        }
                    }

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
}