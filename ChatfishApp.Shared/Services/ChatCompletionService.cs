using ChatfishApp.Contracts;
using ChatfishApp.Core.Storage;
using Microsoft.Extensions.AI;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StoreAttachment = ChatfishApp.Core.Storage.Attachment;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIContent = Microsoft.Extensions.AI.AIContent;
using TextContent = Microsoft.Extensions.AI.TextContent;
using DataContent = Microsoft.Extensions.AI.DataContent;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;
using StoreChatMessage = ChatfishApp.Core.Storage.ChatMessage;

using ChatfishApp.Core.Browser;
using ChatfishApp.Core.Chat;
using ChatfishApp.Core.Tools;
using ChatfishApp.Core.UI;
using ChatfishApp.Shared.Services.Tools;

namespace ChatfishApp.Shared.Services;

/// <summary>
/// Shared chat completion logic used by ChatPage and sync AI relay.
/// </summary>
public sealed class ChatCompletionService : IChatCompletionService
{
    private readonly ChatModelCatalogService _catalog;
    private readonly IKeyStore _keyStore;
    private readonly IToolProvider _toolProvider;
    private readonly IToolExecutionTrace _trace;
    private readonly IRequestRouter _router;
    private readonly IRoutingSessionStore _sessions;
    private readonly IBrowserPanelState _browserPanel;
    private readonly IBrowserAgentService _browserAgent;
    private IReadOnlyList<AITool> _currentTools = [];
    private RequestRoute? _currentRoute;
    private static readonly HttpClient OllamaHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    public ChatCompletionService(
        ChatModelCatalogService catalog,
        IKeyStore keyStore,
        IToolProvider toolProvider,
        IToolExecutionTrace trace,
        IRequestRouter router,
        IRoutingSessionStore sessions,
        IBrowserPanelState browserPanel,
        IBrowserAgentService browserAgent)
    {
        _catalog = catalog;
        _keyStore = keyStore;
        _toolProvider = toolProvider;
        _trace = trace;
        _router = router;
        _sessions = sessions;
        _browserPanel = browserPanel;
        _browserAgent = browserAgent;
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        string modelId,
        IReadOnlyList<StoreChatMessage> messages,
        string? currentUser = null,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(modelId))
            return new ChatCompletionResult("", "", "No model selected.");

        var modelInfo = _catalog.GetAvailableModels().FirstOrDefault(m => m.Id == modelId);
        bool supportsTools = modelInfo?.SupportsTools ?? ResolveOllamaCapability(modelId, tools: true);
        bool supportsVision = modelInfo?.SupportsVision ?? ResolveOllamaCapability(modelId, tools: false);
        int contextSize = modelInfo?.ContextSize ?? ResolveOllamaContextSize(modelId);
        bool serverVisionProxy = !supportsVision &&
            !string.IsNullOrWhiteSpace(modelInfo?.VisionProxyModelId ?? _catalog.GetProxiedVisionProxyModelId(modelId));
        bool includeImagesInHistory = supportsVision || serverVisionProxy;


        try
        {
            var (effectiveMessages, visionProxyTrace) = await ApplyVisionProxyAsync(
                modelId, messages, currentUser, supportsVision, ct);
            var chatHistory = BuildChatHistory(effectiveMessages, currentUser, includeImagesInHistory);
            PrependSystemPrompt(chatHistory);

            if (_browserPanel.IsOpen)
                AppendSystemInstruction(chatHistory, BuildBrowserPrompt());

            _trace.Clear();
            if (!string.IsNullOrWhiteSpace(visionProxyTrace))
                _trace.Record(visionProxyTrace);
            else if (serverVisionProxy && MessagesContainVisionAttachments(effectiveMessages, currentUser))
            {
                var proxyId = modelInfo?.VisionProxyModelId ?? _catalog.GetProxiedVisionProxyModelId(modelId);
                _trace.Record(
                    $"👁️ Routing image(s) through server vision proxy ({proxyId})...");
            }

            var lastUserMessage = ExtractLastUserMessage(effectiveMessages);
            var baseClient = _catalog.GetChatClientForModel(modelId);
            IChatClient client = baseClient;
            ChatOptions? chatOptions = null;
            _currentRoute = null;

            if (supportsTools)
            {
                client = baseClient
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();

                var activeModules = _toolProvider.GetActiveModules();
                var route = _router.ClassifyRequest(lastUserMessage, activeModules, conversationId);
                _currentRoute = route;
                _currentTools = route.TargetModule != null
                    ? _toolProvider.GetToolsForModules([route.TargetModule, "Native"])
                    : _toolProvider.GetTools();

                _trace.Record(route.TargetModule != null
                    ? $"🧭 Route: {route.Type} → {route.TargetModule}"
                    : $"🧭 Route: {route.Type}");

                if (route.TargetModule == "HomeAssistant")
                {
                    var session = _sessions.Get(conversationId);
                    var assistantName = _keyStore.HomeAssistantAssistantName;
                    if (!ContextualRequestRouter.ContainsWakeWord(lastUserMessage, assistantName) &&
                        session.IsActive("HomeAssistant", ContextualRequestRouter.SessionTtl))
                    {
                        _trace.Record("🧭 Session: continuing active Home Assistant conversation");
                    }
                }

                var toolNames = _currentTools.OfType<AIFunction>().Select(f => f.Name).ToList();
                _trace.Record(toolNames.Count > 0
                    ? $"🔧 Tools ({toolNames.Count}): {string.Join(", ", toolNames)}"
                    : "🔧 Tools: none available");

                if (ContextualRequestRouter.ShouldEnforceHomeAssistantTools(route))
                {
                    var session = _sessions.Get(conversationId);
                    AppendSystemInstruction(chatHistory, BuildHomeAssistantPrompt(session));
                }

                chatOptions = new ChatOptions { Tools = _currentTools.ToList() };
            }
            else
            {
                _currentTools = [];
            }

            const int maxAttempts = 3;
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var response = await client.GetResponseAsync(chatHistory, chatOptions ?? new ChatOptions(), ct);

                    var reasoningTrace = CollectReasoningTrace(response);
                    var responseText = ExtractFinalDisplayText(response);
                    string? extractSource = null;

                    // Local Ollama only: raw JSON fallback when content is empty (proxied Ollama uses ServerProxyChatClient).
                    if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(responseText) || IsBogusExtractedText(responseText)))
                    {
                        var historyForFallback = BuildFallbackHistory(chatHistory, response.Messages);
                        var (fallbackText, fallbackSource) = await TryOllamaRawCompletionAsync(
                            modelId, historyForFallback, supportsTools, contextSize, ct);
                        if (!string.IsNullOrWhiteSpace(fallbackText))
                        {
                            responseText = fallbackText;
                            extractSource = fallbackSource;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(responseText))
                    {
                        var (extracted, source) = ExtractContentOnlyFromResponse(response);
                        if (!string.IsNullOrWhiteSpace(extracted) && !IsBogusExtractedText(extracted))
                        {
                            responseText = extracted;
                            extractSource = source;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(extractSource))
                        _trace.Record($"ℹ️ Extracted final response from {extractSource}.");

                    if (ContextualRequestRouter.ShouldEnforceHomeAssistantTools(_currentRoute) &&
                        !HaToolsWereInvoked())
                    {
                        _trace.Record("⚠️ Model replied without calling Home Assistant tools — retrying with tool-required prompt.");

                        if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
                        {
                            var retryHistory = BuildRetryHistoryForHomeAssistant(chatHistory);
                            var (retryText, retrySource) = await TryOllamaRawCompletionAsync(
                                modelId, retryHistory, supportsTools: true, contextSize, ct);
                            if (!string.IsNullOrWhiteSpace(retryText))
                            {
                                responseText = retryText;
                                extractSource = retrySource;
                            }
                        }
                        else
                        {
                            var retryHistory = BuildRetryHistoryForHomeAssistant(chatHistory);
                            var retryResponse = await client.GetResponseAsync(retryHistory, chatOptions ?? new ChatOptions(), ct);
                            var retryText = ExtractFinalDisplayText(retryResponse);
                            if (!string.IsNullOrWhiteSpace(retryText) && !IsBogusExtractedText(retryText))
                                responseText = retryText;
                        }

                        if (!HaToolsWereInvoked())
                        {
                            _trace.Record("⚠️ Home Assistant action was NOT performed — no tool was called.");
                            if (ResponseClaimsHomeAssistantAction(responseText))
                            {
                                var assistantName = _keyStore.HomeAssistantAssistantName;
                                responseText =
                                    $"I wasn't able to control your Home Assistant devices — the model answered without calling the Home Assistant tools. " +
                                    $"Start with your assistant name (e.g. \"{assistantName}, turn off the kitchen light\") or continue within 15 minutes of a successful command. " +
                                    "Try a model with stronger tool support (e.g. Llama 3.3 or Qwen) if this keeps happening.";
                            }
                        }
                    }

                    RecordHomeAssistantSessionIfNeeded(conversationId);

                    var toolTrace = string.Join("\n", _trace.GetCurrentTrace());
                    if (!string.IsNullOrWhiteSpace(reasoningTrace))
                    {
                        toolTrace = string.IsNullOrWhiteSpace(toolTrace)
                            ? $"💭 Model reasoning:\n{reasoningTrace}"
                            : $"💭 Model reasoning:\n{reasoningTrace}\n\n{toolTrace}";
                    }

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
                        _trace.Record($"⚠️ Provider rate limit (429) on attempt {attempt}. Waiting {delayMs}ms before retry...");
                        await Task.Delay(delayMs, ct);
                        continue;
                    }
                    throw;
                }
            }

            var exhaustedTrace = string.Join("\n", _trace.GetCurrentTrace());
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

    private static string ExtractLastUserMessage(IReadOnlyList<StoreChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                return messages[i].Content ?? "";
        }
        return "";
    }

    private async Task<(List<StoreChatMessage> Messages, string? Trace)> ApplyVisionProxyAsync(
        string modelId,
        IReadOnlyList<StoreChatMessage> messages,
        string? currentUser,
        bool supportsVision,
        CancellationToken ct)
    {
        // Server-proxied providers (e.g. free-chat) handle vision proxying in AiProviderProxyService.
        if (_catalog.IsProxiedModel(modelId))
            return (messages.ToList(), null);

        if (supportsVision || !MessagesContainVisionAttachments(messages, currentUser))
            return (messages.ToList(), null);

        var proxyName = _keyStore.GetVisionProxyModelName();
        if (string.IsNullOrWhiteSpace(proxyName))
            return (messages.ToList(), null);

        var proxyModelId = $"ollama/{proxyName}";
        var enriched = new List<StoreChatMessage>();
        int describedCount = 0;
        int lastUserVisionIndex = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (IsUserVisionMessage(messages[i], currentUser, out _))
            {
                lastUserVisionIndex = i;
                break;
            }
        }

        for (int index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (index != lastUserVisionIndex || !IsUserVisionMessage(message, currentUser, out var visionAttachments))
            {
                enriched.Add(message);
                continue;
            }

            var descriptions = new List<string>();
            foreach (var attachment in visionAttachments)
            {
                var description = await DescribeAttachmentViaVisionProxyAsync(proxyModelId, attachment, ct);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    var label = string.IsNullOrWhiteSpace(attachment.Name) ? "attachment" : attachment.Name;
                    descriptions.Add($"[{label}]: {description.Trim()}");
                    describedCount++;
                }
            }

            if (descriptions.Count == 0)
            {
                enriched.Add(message);
                continue;
            }

            var prefix = string.IsNullOrWhiteSpace(message.Content) ? "" : CleanTextForLlm(message.Content) + "\n\n";
            var injected = prefix +
                "[Image context — described by vision proxy model '" + proxyName + "']\n" +
                string.Join("\n\n", descriptions);

            enriched.Add(message with { Content = injected.Trim(), Attachments = null });
        }

        if (describedCount == 0)
            return (messages.ToList(), null);

        var trace = $"👁️ Vision proxy ({proxyName}) described {describedCount} attachment(s) for the text-only model.";
        return (enriched, trace);
    }

    private static bool MessagesContainVisionAttachments(IReadOnlyList<StoreChatMessage> messages, string? currentUser) =>
        messages.Any(m => IsUserVisionMessage(m, currentUser, out _));

    private static bool IsUserVisionMessage(
        StoreChatMessage message,
        string? currentUser,
        out List<StoreAttachment> visionAttachments)
    {
        visionAttachments = new List<StoreAttachment>();
        if (message.Attachments is not { Count: > 0 })
            return false;

        var roleStr = message.Role ?? (message.User == currentUser ? "user" : "assistant");
        if (!roleStr.Equals("user", StringComparison.OrdinalIgnoreCase))
            return false;

        visionAttachments = message.Attachments
            .Where(a =>
                a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                a.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return visionAttachments.Count > 0;
    }

    private async Task<string?> DescribeAttachmentViaVisionProxyAsync(
        string proxyModelId,
        StoreAttachment attachment,
        CancellationToken ct)
    {
        bool isPdf = attachment.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
        var prompt = isPdf
            ? "Summarize this document in detail for use as context in a follow-up text-only conversation. Include key facts, structure, and any visible text."
            : "Describe this image in detail for use as context in a follow-up text-only conversation. Include objects, text, colors, layout, and anything relevant to answering questions about it.";

        try
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(attachment.DataBase64);
            }
            catch
            {
                return null;
            }

            var contents = new List<AIContent>
            {
                new TextContent(prompt),
                new DataContent(bytes, attachment.ContentType)
            };

            var client = _catalog.GetChatClientForModel(proxyModelId);
            var response = await client.GetResponseAsync(
                [new AiChatMessage(ChatRole.User, contents)],
                new ChatOptions(),
                ct);

            var text = ExtractFinalDisplayText(response);
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            if (proxyModelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            {
                var (rawText, _) = await TryOllamaVisionDescriptionAsync(proxyModelId, prompt, bytes, attachment.ContentType, ct);
                return rawText;
            }
        }
        catch (Exception ex)
        {
            return $"[Vision proxy could not describe this attachment: {ex.Message}]";
        }

        return null;
    }

    private async Task<(string? Text, string? Source)> TryOllamaVisionDescriptionAsync(
        string proxyModelId,
        string prompt,
        byte[] bytes,
        string contentType,
        CancellationToken ct)
    {
        try
        {
            var ollamaModel = proxyModelId.Split('/', 2)[1];
            var baseUrl = _keyStore.OllamaBaseUrl.TrimEnd('/') + "/v1/chat/completions";
            var contextSize = ResolveOllamaContextSize(proxyModelId);
            var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";

            var body = new Dictionary<string, object?>
            {
                ["model"] = ollamaModel,
                ["stream"] = false,
                ["messages"] = new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["role"] = "user",
                        ["content"] = new List<Dictionary<string, object?>>
                        {
                            new() { ["type"] = "text", ["text"] = prompt },
                            new()
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new Dictionary<string, object?> { ["url"] = dataUrl }
                            }
                        }
                    }
                }
            };

            if (contextSize > 0)
                body["options"] = new Dictionary<string, object?> { ["num_ctx"] = contextSize };

            using var resp = await OllamaHttp.PostAsJsonAsync(baseUrl, body, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, null);

            var json = await resp.Content.ReadAsStringAsync(ct);
            var (text, _) = ParseOllamaCompletionFull(json);
            return (text, "Ollama vision proxy");
        }
        catch
        {
            return (null, null);
        }
    }

    private void PrependSystemPrompt(List<AiChatMessage> chatHistory)
    {
        var systemText = ResolveSystemPrompt();
        if (string.IsNullOrWhiteSpace(systemText))
            return;

        chatHistory.Insert(0, new AiChatMessage(ChatRole.System, systemText));
    }

    private static void AppendSystemInstruction(List<AiChatMessage> chatHistory, string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            return;

        var systemIndex = chatHistory.FindIndex(m => m.Role == ChatRole.System);
        if (systemIndex >= 0)
        {
            var existing = string.Join("\n",
                chatHistory[systemIndex].Contents.OfType<TextContent>().Select(t => t.Text));
            chatHistory[systemIndex] = new AiChatMessage(
                ChatRole.System,
                string.IsNullOrWhiteSpace(existing) ? instruction : $"{existing}\n\n{instruction}");
            return;
        }

        chatHistory.Insert(0, new AiChatMessage(ChatRole.System, instruction));
    }

    private List<AiChatMessage> BuildRetryHistoryForHomeAssistant(IList<AiChatMessage> chatHistory)
    {
        var retryHistory = chatHistory.ToList();
        var assistantName = _keyStore.HomeAssistantAssistantName;
        AppendSystemInstruction(
            retryHistory,
            $"CRITICAL: Call ListLights or ControlLight now to perform the user's request for assistant '{assistantName}'. " +
            "Do not answer with text only — you must invoke a Home Assistant tool.");
        return retryHistory;
    }

    private string BuildBrowserPrompt()
    {
        var url = string.IsNullOrWhiteSpace(_browserAgent.CurrentUrl) ? "(none)" : _browserAgent.CurrentUrl;
        var title = string.IsNullOrWhiteSpace(_browserAgent.PageTitle) ? "(none)" : _browserAgent.PageTitle;

        return $"""
            Current browser URL: {url}
            Page title: "{title}"
            The user may ask you to summarize this page, extract information,
            or navigate to other URLs. Use the available browser tools.
            """;
    }

    private string BuildHomeAssistantPrompt(RoutingSession session)
    {
        var assistantName = _keyStore.HomeAssistantAssistantName;
        var sb = new StringBuilder();
        sb.AppendLine($"You have access to a Home Assistant integration named \"{assistantName}\".");
        sb.AppendLine($"When the user addresses \"{assistantName}\" directly, or continues an active smart-home session, use the Home Assistant tools (ListLights, ControlLight, GetEntityState, CallService) to fulfill the request.");
        sb.AppendLine("You MUST call a tool before claiming you changed a device. If you do not know the entity_id, call ListLights first.");

        var summary = _keyStore.HomeAssistantDeviceSummary;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine();
            sb.AppendLine("Current known devices:");
            sb.AppendLine(summary);
        }

        if (session.IsActive("HomeAssistant", ContextualRequestRouter.SessionTtl))
        {
            sb.AppendLine();
            sb.AppendLine("ACTIVE SESSION: You are currently controlling home devices in this conversation.");
            if (!string.IsNullOrWhiteSpace(session.LastEntityActedOn))
                sb.AppendLine($"Last entity: {session.LastEntityActedOn}.");
            if (!string.IsNullOrWhiteSpace(session.LastAction))
                sb.AppendLine($"Last action: {session.LastAction}.");
            sb.AppendLine("Follow-up messages about the same device should use Home Assistant tools.");
        }

        return sb.ToString().Trim();
    }

    private void RecordHomeAssistantSessionIfNeeded(string? conversationId)
    {
        if (!HaToolsWereInvoked() || string.IsNullOrWhiteSpace(conversationId))
            return;

        string? entity = null;
        string? action = null;
        foreach (var line in _trace.GetCurrentTrace())
        {
            if (!line.Contains("control_light", StringComparison.OrdinalIgnoreCase))
                continue;

            entity = ExtractTraceParam(line, "entity") ?? entity;
            action = ExtractTraceParam(line, "state") ?? action;
        }

        _sessions.RecordToolInvocation(conversationId, "HomeAssistant", entity, action);
    }

    private static string? ExtractTraceParam(string line, string paramName)
    {
        var match = Regex.Match(line, $"{paramName}=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private bool HaToolsWereInvoked() =>
        _trace.GetCurrentTrace().Any(line => line.Contains("🏠", StringComparison.Ordinal));

    private static bool ResponseClaimsHomeAssistantAction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        string[] markers =
        [
            "turned on", "turned off", "turned the", "i have turned", "i've turned",
            "light is now", "light has been", "done!", "all set", "successfully turned",
            "is now off", "is now on", "is off", "is on"
        ];

        foreach (var marker in markers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private string ResolveSystemPrompt()
    {
        var template = _keyStore.GetSystemPrompt();
        if (string.IsNullOrWhiteSpace(template))
            return "";

        var now = DateTime.Now;
        var formatted = now.ToString("dddd, MMMM d, yyyy h:mm tt", CultureInfo.CurrentCulture) + " (local)";
        var text = template.Replace("{{datetime}}", formatted, StringComparison.OrdinalIgnoreCase).Trim();

        var userContext = _keyStore.BuildUserContextForPrompt();
        if (!string.IsNullOrWhiteSpace(userContext))
            text = string.IsNullOrWhiteSpace(text) ? userContext : $"{text}\n\n{userContext}";

        return text.Trim();
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
    /// User-visible answer from the last assistant turn (no pending tool calls).
    /// Prefers content; falls back to reasoning only on that final turn (thinking models like lfm2.5).
    /// Intermediate tool-planning reasoning stays out of the bubble — see CollectReasoningTrace.
    /// </summary>
    private static string ExtractFinalDisplayText(ChatResponse response)
    {
        if (response.Messages != null)
        {
            var finalMsg = response.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .Reverse()
                .FirstOrDefault(m => !m.Contents.OfType<FunctionCallContent>().Any());

            if (finalMsg != null)
            {
                var text = string.Join("\n",
                    finalMsg.Contents
                        .OfType<TextContent>()
                        .Select(t => t.Text)
                        .Where(t => !string.IsNullOrWhiteSpace(t)));

                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();

                var reasoning = GetReasoningFromMessage(finalMsg);
                if (!string.IsNullOrWhiteSpace(reasoning))
                    return reasoning.Trim();
            }
        }

        var fromRaw = TryExtractContentFieldFromObject(response.RawRepresentation);
        if (!string.IsNullOrWhiteSpace(fromRaw))
            return fromRaw!;

        if (!ResponseRawHasToolCalls(response.RawRepresentation))
        {
            var fromRawReasoning = TryExtractReasoningFieldFromObject(response.RawRepresentation);
            if (!string.IsNullOrWhiteSpace(fromRawReasoning))
                return fromRawReasoning.Trim();
        }

        var fallback = response.Text?.Trim();
        return string.IsNullOrWhiteSpace(fallback) ? "" : fallback;
    }

    /// <summary>
    /// Collects model chain-of-thought from all assistant turns for the collapsible trace (not main chat).
    /// </summary>
    private static string CollectReasoningTrace(ChatResponse response)
    {
        var parts = new List<string>();
        var displayText = ExtractFinalDisplayText(response);

        if (response.Messages != null)
        {
            var assistantMsgs = response.Messages.Where(m => m.Role == ChatRole.Assistant).ToList();
            int step = 0;

            for (int i = 0; i < assistantMsgs.Count; i++)
            {
                var msg = assistantMsgs[i];
                bool isFinalTurn = i == assistantMsgs.Count - 1 && !msg.Contents.OfType<FunctionCallContent>().Any();

                var reasoning = GetReasoningFromMessage(msg);
                if (string.IsNullOrWhiteSpace(reasoning))
                    continue;

                // Final-turn reasoning is shown in the chat bubble when content is empty; skip duplicating it here.
                if (isFinalTurn && string.Equals(reasoning.Trim(), displayText.Trim(), StringComparison.Ordinal))
                    continue;

                step++;
                parts.Add(step > 1 ? $"[Step {step}]\n{reasoning}" : reasoning);
            }
        }

        return string.Join("\n\n", parts);
    }

    private static bool ResponseRawHasToolCalls(object? raw)
    {
        if (raw == null) return false;
        try
        {
            var json = JsonSerializer.Serialize(raw);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return false;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("tool_calls", out var tc) &&
                    tc.ValueKind == JsonValueKind.Array &&
                    tc.GetArrayLength() > 0)
                    return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    private static string? GetReasoningFromMessage(AiChatMessage msg)
    {
        if (msg.AdditionalProperties != null)
        {
            foreach (var key in new[] { "reasoning", "reasoning_content", "reasoning_text" })
            {
                if (msg.AdditionalProperties.TryGetValue(key, out var val))
                {
                    var s = CoerceToString(val);
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
        }

        return TryExtractReasoningFieldFromObject(msg.RawRepresentation);
    }

    /// <summary>
    /// Fallback extraction using only the standard content field (never reasoning).
    /// </summary>
    private static (string Text, string? Source) ExtractContentOnlyFromResponse(ChatResponse response)
    {
        if (response.Messages != null)
        {
            foreach (var msg in response.Messages.Where(m => m.Role == ChatRole.Assistant).Reverse())
            {
                if (msg.Contents.OfType<FunctionCallContent>().Any())
                    continue;

                var contentText = string.Join("\n",
                    msg.Contents
                        .OfType<TextContent>()
                        .Select(t => t.Text)
                        .Where(t => !string.IsNullOrWhiteSpace(t)));

                if (!string.IsNullOrWhiteSpace(contentText))
                    return (contentText, "message content");
            }
        }

        var fromRaw = TryExtractContentFieldFromObject(response.RawRepresentation);
        if (!string.IsNullOrWhiteSpace(fromRaw))
            return (fromRaw!, "raw response content");

        return ("", null);
    }

    private static string? TryExtractContentFieldFromObject(object? raw)
    {
        if (raw == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(raw);
            using var doc = JsonDocument.Parse(json);
            return FindContentStringInJson(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindContentStringInJson(JsonElement element, int depth = 0)
    {
        if (depth > 12) return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (!choice.TryGetProperty("message", out var message)) continue;
                    if (message.TryGetProperty("content", out var contentEl) &&
                        contentEl.ValueKind == JsonValueKind.String)
                    {
                        var s = contentEl.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            return s;
                    }
                }
            }

            if (element.TryGetProperty("content", out var directContent) &&
                directContent.ValueKind == JsonValueKind.String)
            {
                var s = directContent.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return null;
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
    private bool ResolveOllamaCapability(string modelId, bool tools)
    {
        if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
        {
            var name = modelId.Split('/', 2)[1];
            var settings = _keyStore.GetOllamaModelSettings(name);
            if (settings != null)
                return tools ? settings.SupportsTools : settings.SupportsVision;
        }

        var caps = ProviderCatalog.GetCapabilitiesForModel(modelId);
        return tools ? caps.SupportsTools : caps.SupportsVision;
    }

    private int ResolveOllamaContextSize(string modelId)
    {
        if (!modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            return 0;

        var name = modelId.Split('/', 2)[1];
        return _keyStore.GetOllamaModelSettings(name)?.ContextSize ?? 0;
    }

    private async Task<(string Text, string? Source)> TryOllamaRawCompletionAsync(
        string modelId,
        IList<AiChatMessage> messages,
        bool supportsTools,
        int contextSize,
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
                if (contextSize > 0)
                    body["options"] = new Dictionary<string, object?> { ["num_ctx"] = contextSize };
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
            _trace.Record($"[debug] Ollama raw fallback failed: {ex.Message}");
            return ("", null);
        }
    }

    private List<Dictionary<string, object?>> BuildOllamaToolDefinitions()
    {
        var tools = new List<Dictionary<string, object?>>();
        foreach (var tool in _currentTools)
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
        var fn = _currentTools.OfType<AIFunction>().FirstOrDefault(f => f.Name == name);
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