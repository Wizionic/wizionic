using App.Contracts;
using App.Core.Storage;
using Microsoft.Extensions.AI;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StoreAttachment = App.Core.Storage.Attachment;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIContent = Microsoft.Extensions.AI.AIContent;
using TextContent = Microsoft.Extensions.AI.TextContent;
using DataContent = Microsoft.Extensions.AI.DataContent;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;
using StoreChatMessage = App.Core.Storage.ChatMessage;

using App.Core.Browser;
using App.Core.Chat;
using App.Core.Skills;
using App.Core.SmartHome;
using App.Core.Tools;
using App.Core.UI;
using App.Shared.Services.Lemonade;
using App.Shared.Services.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace App.Shared.Services;

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
    private readonly ISmartHomeService _smartHome;
    private readonly IToolConversationContext _toolConvo;
    private readonly IConversationMediaBuffer _mediaBuffer;
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;
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
        IBrowserAgentService browserAgent,
        ISmartHomeService smartHome,
        IToolConversationContext toolConvo,
        IConversationMediaBuffer mediaBuffer,
        IServiceProvider services,
        IServiceScopeFactory scopeFactory)
    {
        _catalog = catalog;
        _keyStore = keyStore;
        _toolProvider = toolProvider;
        _trace = trace;
        _router = router;
        _sessions = sessions;
        _browserPanel = browserPanel;
        _browserAgent = browserAgent;
        _smartHome = smartHome ?? throw new ArgumentNullException(nameof(smartHome));
        _toolConvo = toolConvo ?? throw new ArgumentNullException(nameof(toolConvo));
        _mediaBuffer = mediaBuffer ?? throw new ArgumentNullException(nameof(mediaBuffer));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        string modelId,
        IReadOnlyList<StoreChatMessage> messages,
        string? currentUser = null,
        string? conversationId = null,
        CancellationToken ct = default,
        Func<string, Task>? onPartialText = null)
    {
        if (string.IsNullOrEmpty(modelId))
            return new ChatCompletionResult("", "", "No model selected.");

        var wallStart = DateTime.UtcNow;
        var modelInfo = _catalog.GetAvailableModels().FirstOrDefault(m => m.Id == modelId);
        bool isOmniCollection = modelInfo?.IsOmniCollection == true || IsLemonadeOmniModel(modelId);
        bool supportsTools = modelInfo?.SupportsTools ?? ResolveLocalCapability(modelId, tools: true);
        // Omni collections run server-side multimodal tools; client function-invocation would compete.
        if (isOmniCollection)
            supportsTools = false;
        bool supportsVision = modelInfo?.SupportsVision ?? ResolveLocalCapability(modelId, tools: false)
                              || isOmniCollection;
        int contextSize = modelInfo?.ContextSize ?? ResolveLocalContextSize(modelId);
        bool serverVisionProxy = !supportsVision &&
            !string.IsNullOrWhiteSpace(modelInfo?.VisionProxyModelId ?? _catalog.GetProxiedVisionProxyModelId(modelId));
        bool includeImagesInHistory = supportsVision || serverVisionProxy;

        // Cap runaway generations (repetition loops) unless a smaller limit is already set.
        const int defaultMaxOutputTokens = 4096;
        int messagesTrimmed = 0;
        int contextLimit = contextSize > 0 ? contextSize : 8192;

        try
        {
            _toolConvo.ConversationId = string.IsNullOrWhiteSpace(conversationId) ? "_default" : conversationId;

            var (effectiveMessages, visionProxyTrace) = await ApplyVisionProxyAsync(
                modelId, messages, currentUser, supportsVision, ct);
            var chatHistory = BuildChatHistory(effectiveMessages, currentUser, includeImagesInHistory);
            PrependSystemPrompt(chatHistory);

            if (_browserPanel.IsOpen)
                AppendSystemInstruction(chatHistory, BuildBrowserPrompt());

            // Leave room for the reply; drop oldest non-system turns if the history is too large.
            if (contextSize > 0)
                contextLimit = contextSize;
            int reserveOutput = Math.Min(defaultMaxOutputTokens, Math.Max(512, contextLimit / 4));
            chatHistory = TrimHistoryToContext(chatHistory, contextLimit, reserveOutput, out messagesTrimmed);

            _trace.Clear();
            if (messagesTrimmed > 0)
            {
                _trace.Record(
                    $"✂️ Context: dropped {messagesTrimmed} older message(s) to fit ~{contextLimit} token window " +
                    $"(reserved ~{reserveOutput} for the reply).");
            }
            if (isOmniCollection)
            {
                _trace.Record(
                    "🍋 Omni collection: Lemonade runs image/edit/TTS tools server-side. " +
                    "Client tools disabled for this turn so the collection planner owns the loop.");
            }
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
                var skillCtx = App.Shared.Services.Skills.SkillExecutionContext.Current;
                RequestRoute route;
                if (skillCtx is not null)
                {
                    // Forced skill run: bypass PureChat router; use skill modules + instructions.
                    route = RequestRoute.WithModules(
                        skillCtx.Modules,
                        reason: "skill " + skillCtx.SkillName,
                        targetModule: null,
                        includeMcp: skillCtx.IncludeMcp,
                        source: "Skill");
                    _currentRoute = route;
                    _currentTools = _toolProvider.GetToolsForModules(skillCtx.Modules, includeMcp: skillCtx.IncludeMcp);
                    _trace.Record($"🧭 Route: Skill → {skillCtx.SkillName} [{string.Join("+", skillCtx.Modules)}] mcp={skillCtx.IncludeMcp}");
                    AppendSystemInstruction(chatHistory, skillCtx.SystemInstructions);
                }
                else
                {
                    var activeModules = _toolProvider.GetActiveModules();
                    route = await _router.ClassifyRequestAsync(
                        lastUserMessage, activeModules, conversationId, ct);
                    _currentRoute = route;

                    // All tool-set decisions live in IRequestRouter (rules / AI / hybrid).
                    _currentTools = route.HasTools
                        ? _toolProvider.GetToolsForModules(route.Modules, includeMcp: route.IncludeMcp)
                        : [];

                    var src = string.IsNullOrWhiteSpace(route.Source) ? "Rules" : route.Source;
                    var reason = string.IsNullOrWhiteSpace(route.Reason) ? "" : $" ({route.Reason})";
                    if (route.HasTools)
                    {
                        var modList = string.Join("+", route.Modules);
                        _trace.Record(
                            route.TargetModule != null
                                ? $"🧭 Route: {src} → {route.Type} → {route.TargetModule} [{modList}]{reason}"
                                : $"🧭 Route: {src} → {route.Type} → {modList}{reason}");
                    }
                    else
                    {
                        _trace.Record($"🧭 Route: {src} → PureChat{reason}");
                    }

                    if (route.TargetModule == "HomeAssistant"
                        && route.Reason?.Contains("active session", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _trace.Record("🧭 Session: continuing active Home Assistant conversation (rules stickiness)");
                    }

                    // Slash skill: same full instruction block as SkillRunner (not a thin wrapper).
                    if (!string.IsNullOrWhiteSpace(route.SkillId) &&
                        _services.GetService(typeof(App.Core.Skills.ISkillStore)) is App.Core.Skills.ISkillStore skillStore)
                    {
                        var rec = skillStore.Get(route.SkillId);
                        if (rec is not null)
                        {
                            try
                            {
                                var doc = App.Core.Skills.SkillMarkdown.Parse(rec.Markdown);
                                var res = App.Core.Skills.SkillToolResolver.Resolve(doc.AllowedTools);
                                var skillSys = BuildChatSkillSystemPrompt(doc, res, modelId);
                                AppendSystemInstruction(chatHistory, skillSys);
                                _trace.Record($"🎯 Skill instructions injected: {doc.Name} · model {modelId}");
                                _trace.Record($"🔧 Skill modules: [{string.Join(", ", res.Modules)}] mcp={res.IncludeMcp}");
                            }
                            catch { /* ignore parse */ }
                        }
                    }
                }

                var toolNames = _currentTools.OfType<AIFunction>().Select(f => f.Name).ToList();
                _trace.Record(toolNames.Count > 0
                    ? $"🔧 Tools ({toolNames.Count}): {string.Join(", ", toolNames)}"
                    : "🔧 Tools: none");

                if (ContextualRequestRouter.ShouldEnforceHomeAssistantTools(route))
                {
                    var session = _sessions.Get(conversationId);
                    AppendSystemInstruction(chatHistory, BuildHomeAssistantPrompt(session));
                }

                if (ContextualRequestRouter.ShouldEnforceBrowserTools(route))
                    AppendSystemInstruction(chatHistory, BuildBrowserToolEnforcementPrompt());

                // Pure chat / empty tools: skip UseFunctionInvocation entirely (big TTFT win).
                if (_currentTools.Count == 0)
                {
                    client = baseClient;
                    chatOptions = new ChatOptions { MaxOutputTokens = defaultMaxOutputTokens };
                    supportsTools = false;
                }
                else
                {
                    client = baseClient
                        .AsBuilder()
                        .UseFunctionInvocation()
                        .Build();
                    chatOptions = new ChatOptions
                    {
                        Tools = _currentTools.ToList(),
                        MaxOutputTokens = defaultMaxOutputTokens
                    };
                }
            }
            else
            {
                _currentTools = [];
                chatOptions = new ChatOptions { MaxOutputTokens = defaultMaxOutputTokens };
                _trace.Record("🧭 Route: PureChat (model tools disabled)");
            }

            var prepMs = (DateTime.UtcNow - wallStart).TotalMilliseconds;
            const int maxAttempts = 3;
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var options = chatOptions ?? new ChatOptions { MaxOutputTokens = defaultMaxOutputTokens };
                    if (options.MaxOutputTokens is null or <= 0)
                        options.MaxOutputTokens = defaultMaxOutputTokens;

                    var requestStart = DateTime.UtcNow;
                    var (responseText, response, streamed, ttftMs, cancelled) =
                        await GetResponsePossiblyStreamingAsync(
                            client, chatHistory, options, modelId, onPartialText, ct, requestStart);

                    var reasoningTrace = response != null ? CollectReasoningTrace(response) : "";
                    string? extractSource = streamed ? "streaming response" : null;

                    // Local Ollama only: raw JSON fallback when content is empty (proxied Ollama uses ServerProxyChatClient).
                    if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(responseText) || IsBogusExtractedText(responseText)))
                    {
                        var historyForFallback = response != null
                            ? BuildFallbackHistory(chatHistory, response.Messages)
                            : chatHistory;
                        var (fallbackText, fallbackSource) = await TryOllamaRawCompletionAsync(
                            modelId, historyForFallback, supportsTools, contextSize, ct);
                        if (!string.IsNullOrWhiteSpace(fallbackText))
                        {
                            responseText = fallbackText;
                            extractSource = fallbackSource;
                            if (onPartialText != null)
                                await onPartialText(responseText);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(responseText) && response != null)
                    {
                        var (extracted, source) = ExtractContentOnlyFromResponse(response);
                        if (!string.IsNullOrWhiteSpace(extracted) && !IsBogusExtractedText(extracted))
                        {
                            responseText = extracted;
                            extractSource = source;
                            if (onPartialText != null)
                                await onPartialText(responseText);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(extractSource))
                        _trace.Record($"ℹ️ Extracted final response from {extractSource}.");

                    // Only force HA retry when this turn actually looks like device control.
                    // Avoid turning a weather answer into Assist fallback after a prior light command.
                    if (ContextualRequestRouter.ShouldEnforceHomeAssistantTools(_currentRoute) &&
                        !HaToolsWereInvoked() &&
                        ContextualRequestRouter.MessageSuggestsHomeAssistant(lastUserMessage))
                    {
                        _trace.Record("⚠️ Model replied without calling Home Assistant tools — retrying with tool-required prompt.");

                        // Always re-drive the function-invocation client so tools can actually run
                        // (raw Ollama path returns on first text and skips tool execution).
                        var retryHistory = BuildRetryHistoryForHomeAssistant(chatHistory);
                        try
                        {
                            var retryResponse = await client.GetResponseAsync(retryHistory, chatOptions ?? new ChatOptions(), ct);
                            var retryText = ExtractFinalDisplayText(retryResponse);
                            if (!string.IsNullOrWhiteSpace(retryText) && !IsBogusExtractedText(retryText))
                                responseText = retryText;
                        }
                        catch (Exception retryEx)
                        {
                            _trace.Record($"⚠️ HA tool retry failed: {retryEx.Message}");
                        }

                        if (!HaToolsWereInvoked())
                        {
                            // 1) Structured REST fallback (volume/media/lights) — survives weak tool-calling models
                            // 2) Clean Assist sentence (no raw entity_ids, domain-correct target)
                            // 3) Honest failure
                            var recovered = await RecoverHomeAssistantWithoutModelToolsAsync(
                                lastUserMessage, conversationId, ct);
                            if (!string.IsNullOrWhiteSpace(recovered))
                            {
                                responseText = recovered;
                            }
                            else
                            {
                                _trace.Record("⚠️ Home Assistant action was NOT performed — model, structured fallback, and Assist all failed.");
                                var assistantName = _keyStore.HomeAssistantAssistantName;
                                responseText =
                                    $"I wasn't able to control your Home Assistant devices — the model did not call tools, " +
                                    "and automatic recovery (direct control + Assist) could not complete the request. " +
                                    $"Try naming the device clearly (e.g. \"{assistantName}, set volume to 40 on Helios Denon\"), " +
                                    "or use a model with stronger tool calling.";
                            }
                        }
                    }

                    if (ContextualRequestRouter.ShouldEnforceBrowserTools(_currentRoute) &&
                        !BrowserToolsWereInvoked() &&
                        LooksLikeBrowserNavigationRequest(lastUserMessage))
                    {
                        _trace.Record("⚠️ Model replied without calling browser tools — retrying with tool-required prompt.");

                        var retryHistory = BuildRetryHistoryForBrowser(chatHistory);
                        if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
                        {
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
                            var retryResponse = await client.GetResponseAsync(retryHistory, chatOptions ?? new ChatOptions(), ct);
                            var retryText = ExtractFinalDisplayText(retryResponse);
                            if (!string.IsNullOrWhiteSpace(retryText) && !IsBogusExtractedText(retryText))
                                responseText = retryText;
                        }

                        if (!BrowserToolsWereInvoked())
                        {
                            _trace.Record("⚠️ Browser navigation was NOT performed — no browser tool was called.");
                            if (ResponseClaimsCannotBrowse(responseText))
                            {
                                responseText =
                                    "I wasn't able to drive the embedded browser — the model answered without calling NavigateTo. " +
                                    "Keep the browser panel open and try again, or use a model with stronger tool calling.";
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

                    var text = string.IsNullOrWhiteSpace(responseText)
                        ? (cancelled ? "(Generation stopped.)" : "No response.")
                        : responseText;
                    if (cancelled && !string.IsNullOrWhiteSpace(responseText) &&
                        !responseText.Contains("stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        text = responseText.TrimEnd() + "\n\n*(Generation stopped.)*";
                    }

                    List<StoreAttachment>? resultAttachments = null;

                    // Omni (and any response) may embed data-URI images/audio — extract to attachments.
                    if (OmniMediaExtractor.LooksLikeEmbeddedMedia(text))
                    {
                        var extracted = OmniMediaExtractor.Extract(text);
                        text = string.IsNullOrWhiteSpace(extracted.CleanText)
                            ? (extracted.Attachments.Count > 0 ? "Generated media:" : text)
                            : extracted.CleanText;
                        if (extracted.Attachments.Count > 0)
                        {
                            resultAttachments = extracted.Attachments;
                            BufferExtractedImages(conversationId, extracted.Attachments);
                            _trace.Record(
                                $"🍋 Omni media: extracted {extracted.ImageCount} image(s), {extracted.AudioCount} audio clip(s).");
                        }
                    }

                    // Tool-generated images (lemonade_generate/edit) never re-enter the model as base64;
                    // pull them from the conversation media buffer for the chat UI.
                    var turnBuffered = CollectBufferedImageAttachments(
                        conversationId, sinceUtc: wallStart);
                    if (turnBuffered.Count > 0)
                    {
                        resultAttachments ??= new List<StoreAttachment>();
                        foreach (var att in turnBuffered)
                        {
                            if (!resultAttachments.Any(a =>
                                    a.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                                    && a.DataBase64 == att.DataBase64))
                                resultAttachments.Add(att);
                        }
                        _trace.Record($"🖼️ Attached {turnBuffered.Count} buffered image(s) to chat result.");
                    }

                    // Omni / weak tool models: if the user asked to save to an album and we have an image,
                    // persist without relying on client save_to_gallery (Omni has client tools disabled).
                    var galleryIntent = ContextualRequestRouter.MessageSuggestsGalleryTools(lastUserMessage);
                    var alreadySaved = toolTrace.Contains("save_to_gallery", StringComparison.OrdinalIgnoreCase)
                                       && toolTrace.Contains("✅", StringComparison.Ordinal);
                    if (!cancelled && galleryIntent && !alreadySaved
                        && resultAttachments?.Any(a =>
                            a.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                            && !string.IsNullOrWhiteSpace(a.DataBase64)) == true)
                    {
                        var autoMsg = await TryAutoSaveGalleryAsync(
                            lastUserMessage, conversationId, resultAttachments, ct);
                        if (!string.IsNullOrWhiteSpace(autoMsg))
                        {
                            text = string.IsNullOrWhiteSpace(text) || text == "Generated media:"
                                ? autoMsg
                                : text.TrimEnd() + "\n\n" + autoMsg;
                        }
                    }

                    toolTrace = string.Join("\n", _trace.GetCurrentTrace());
                    if (!string.IsNullOrWhiteSpace(reasoningTrace))
                    {
                        toolTrace = string.IsNullOrWhiteSpace(toolTrace)
                            ? $"💭 Model reasoning:\n{reasoningTrace}"
                            : $"💭 Model reasoning:\n{reasoningTrace}\n\n{toolTrace}";
                    }

                    var totalMs = (DateTime.UtcNow - requestStart).TotalMilliseconds;
                    var (promptTok, completionTok, totalTok) = ExtractUsage(response, text, chatHistory);
                    var contextUsed = (promptTok ?? 0) + (completionTok ?? 0);
                    if (contextUsed <= 0 && promptTok == null)
                        contextUsed = EstimateMessageListTokens(chatHistory) + EstimateTokens(text);
                    var stats = new ChatCompletionStats(
                        PrepMs: prepMs,
                        TtftMs: ttftMs,
                        TotalMs: totalMs,
                        PromptTokens: promptTok,
                        CompletionTokens: completionTok,
                        TotalTokens: totalTok,
                        Streamed: streamed,
                        Cancelled: cancelled,
                        ContextLimit: contextLimit > 0 ? contextLimit : null,
                        ContextUsed: contextUsed > 0 ? contextUsed : null,
                        MessagesTrimmed: messagesTrimmed);

                    // Chat /skill path: persist run history on Skills page (same store as manual/workflow).
                    await TryLogChatSkillRunAsync(
                        modelId, text, toolTrace, cancelled, wallStart, ct);

                    return new ChatCompletionResult(text, toolTrace, null, resultAttachments, stats);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    var totalMs = (DateTime.UtcNow - wallStart).TotalMilliseconds;
                    var stats = new ChatCompletionStats(
                        PrepMs: prepMs,
                        TtftMs: null,
                        TotalMs: totalMs,
                        PromptTokens: null,
                        CompletionTokens: null,
                        TotalTokens: null,
                        Streamed: true,
                        Cancelled: true,
                        ContextLimit: contextLimit > 0 ? contextLimit : null,
                        ContextUsed: null,
                        MessagesTrimmed: messagesTrimmed);
                    return new ChatCompletionResult(
                        "(Generation stopped.)",
                        string.Join("\n", _trace.GetCurrentTrace()),
                        null,
                        null,
                        stats);
                }
                catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ChatCompletionResult(
                "(Generation stopped.)",
                string.Join("\n", _trace.GetCurrentTrace()),
                null,
                null,
                new ChatCompletionStats(0, null, 0, null, null, null, Streamed: true, Cancelled: true));
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

        var proxyModelId = _keyStore.GetVisionProxyModelName();
        if (string.IsNullOrWhiteSpace(proxyModelId))
            return (messages.ToList(), null);

        // Legacy: bare name without provider prefix → treat as Ollama.
        if (!proxyModelId.Contains('/'))
            proxyModelId = "ollama/" + proxyModelId;

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
                "[Image context — described by vision proxy model '" + proxyModelId + "']\n" +
                string.Join("\n\n", descriptions);

            enriched.Add(message with { Content = injected.Trim(), Attachments = null });
        }

        if (describedCount == 0)
            return (messages.ToList(), null);

        var trace = $"👁️ Vision proxy ({proxyModelId}) described {describedCount} attachment(s) for the text-only model.";
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
            var contextSize = ResolveLocalContextSize(proxyModelId);
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
            $"CRITICAL: You must invoke a Home Assistant tool now for assistant '{assistantName}'. " +
            "For media volume/play/pause use ControlMediaPlayer (volume_percent is 0-100). " +
            "For lights use ControlLight. Otherwise ListEntities then CallService. " +
            "Do not answer with text only. Never claim volume, power, or play state changed without a tool call.");
        return retryHistory;
    }

    /// <summary>
    /// When the model skips tools: structured REST control first, then clean Assist NLU.
    /// </summary>
    private async Task<string?> RecoverHomeAssistantWithoutModelToolsAsync(
        string lastUserMessage,
        string? conversationId,
        CancellationToken ct)
    {
        if (!_smartHome.IsConfigured)
            return null;

        var session = _sessions.Get(conversationId);
        var catalog = _keyStore.HomeAssistantDeviceSummary ?? "";
        var assistantName = _keyStore.HomeAssistantAssistantName;

        // --- Structured REST (volume / media / lights) ---
        try
        {
            var (msg, entityId, action) = await HomeAssistantFallback.TryStructuredAsync(
                _smartHome, _trace, lastUserMessage, assistantName, session, catalog, ct);
            if (!string.IsNullOrWhiteSpace(msg))
            {
                _sessions.RecordToolInvocation(conversationId, "HomeAssistant", entityId, action);
                return msg;
            }
        }
        catch (Exception ex)
        {
            _trace.Record($"⚠️ Structured HA fallback error: {ex.Message}");
        }

        // --- Assist with cleaned natural language (never append raw entity_ids) ---
        var command = HomeAssistantFallback.BuildAssistCommand(
            lastUserMessage, assistantName, session, catalog);
        if (string.IsNullOrWhiteSpace(command))
            return null;

        _trace.Record($"🏠 process_conversation(auto-fallback, text=\"{TruncateForTrace(command, 120)}\")");
        try
        {
            var result = await _smartHome.ProcessConversationAsync(command, conversationId: null, ct);
            var preview = TruncateForTrace(result.Replace('\n', ' '), 400);
            var ok = IsAssistFallbackSuccess(result);
            _trace.Record($"   {(ok ? "✅" : "❌")} {preview}");

            if (!ok)
                return null;

            // Prefer domain-correct entity for session continuity
            var entityForSession = HomeAssistantFallback.ResolveEntity(
                lastUserMessage, "media_player", session.LastMediaPlayerEntity, catalog)
                ?? HomeAssistantFallback.ResolveEntity(
                    lastUserMessage, "light", session.LastLightEntity, catalog)
                ?? session.LastEntityActedOn;
            _sessions.RecordToolInvocation(conversationId, "HomeAssistant", entityForSession, "process_conversation");
            return result;
        }
        catch (Exception ex)
        {
            _trace.Record($"   ❌ Assist fallback error: {ex.Message}");
            return null;
        }
    }

    private static bool IsAssistFallbackSuccess(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return false;
        if (result.StartsWith("Assist could not handle", StringComparison.OrdinalIgnoreCase))
            return false;
        if (result.StartsWith("HA error", StringComparison.OrdinalIgnoreCase))
            return false;
        if (result.StartsWith("Connection", StringComparison.OrdinalIgnoreCase))
            return false;
        if (result.StartsWith("Home Assistant is not configured", StringComparison.OrdinalIgnoreCase))
            return false;
        if (result.StartsWith("Smart home integration", StringComparison.OrdinalIgnoreCase))
            return false;
        if (result.StartsWith("Conversation text is required", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static string TruncateForTrace(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? "";
        return text[..max] + "…";
    }

    private static List<AiChatMessage> BuildRetryHistoryForBrowser(IList<AiChatMessage> chatHistory)
    {
        var retryHistory = chatHistory.ToList();
        AppendSystemInstruction(
            retryHistory,
            "CRITICAL: Call NavigateTo now with the full http(s) URL the user requested. " +
            "Do not answer with text only — you must invoke NavigateTo. " +
            "Never claim you cannot navigate or open websites.");
        return retryHistory;
    }

    private string BuildBrowserPrompt()
    {
        var url = string.IsNullOrWhiteSpace(_browserAgent.CurrentUrl) ? "(none)" : _browserAgent.CurrentUrl;
        var title = string.IsNullOrWhiteSpace(_browserAgent.PageTitle) ? "(none)" : _browserAgent.PageTitle;

        return $"""
            EMBEDDED BROWSER IS ACTIVE in this Wizionic desktop app.
            Current browser URL: {url}
            Page title: "{title}"

            You control that real embedded browser via tools (NavigateTo, GetPageContent, ClickElement, FillField).
            When the user asks to open, go to, visit, load, or navigate to a site or URL:
            - You MUST call NavigateTo with a full http(s) URL (prepend https:// if the user omitted the scheme).
            - Do NOT say you cannot browse, navigate, or open websites.
            - Do NOT only use SearchWeb for a navigation request — call NavigateTo first.
            After NavigateTo succeeds, confirm the destination briefly. Use GetPageContent when asked about the page.
            """;
    }

    private async Task TryLogChatSkillRunAsync(
        string modelId,
        string text,
        string toolTrace,
        bool cancelled,
        DateTime wallStart,
        CancellationToken ct)
    {
        var skillId = _currentRoute?.SkillId;
        if (string.IsNullOrWhiteSpace(skillId)) return;
        // Avoid double-log when SkillRunner already owns the ambient skill context.
        if (App.Shared.Services.Skills.SkillExecutionContext.Current is not null) return;

        try
        {
            var logs = _services.GetService(typeof(ISkillRunLogStore)) as ISkillRunLogStore;
            if (logs is null) return;
            var store = _services.GetService(typeof(ISkillStore)) as ISkillStore;
            var rec = store?.Get(skillId);
            var name = rec?.Name ?? skillId;
            var log = new SkillRunLog
            {
                SkillId = skillId,
                SkillName = name,
                ModelId = modelId,
                Source = SkillRunSource.Chat,
                TriggerDetail = "chat",
                StartedAtUtc = new DateTimeOffset(wallStart, TimeSpan.Zero),
                EndedAtUtc = DateTimeOffset.UtcNow,
                Success = !cancelled && !string.IsNullOrWhiteSpace(text),
                Error = cancelled ? "cancelled" : null,
                ResultText = text,
                LogLines = string.IsNullOrWhiteSpace(toolTrace)
                    ? new List<string>()
                    : toolTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList()
            };
            await logs.AddAsync(log, ct);
        }
        catch
        {
            // non-fatal
        }
    }

    private static string BuildChatSkillSystemPrompt(
        App.Core.Skills.SkillDocument doc,
        App.Core.Skills.SkillToolResolver.Resolution resolution,
        string modelId)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are executing the Agent Skill `{doc.Name}`.");
        sb.AppendLine($"Active model: {modelId}");
        sb.AppendLine("Follow the skill instructions exactly. Prefer real tool calls over claiming success without tools.");
        sb.AppendLine("As you work, use tools for each concrete step (gallery, notes, calendar, HA, MCP, etc.).");
        sb.AppendLine("Do not invent a different interpretation of the user message — the skill definition is authoritative.");
        sb.AppendLine($"Skill description: {doc.Description}");
        if (resolution.Modules.Count > 0)
            sb.AppendLine("Preferred tool modules: " + string.Join(", ", resolution.Modules));
        if (resolution.IncludeMcp)
            sb.AppendLine("MCP and OAuth connector tools may also be available.");
        sb.AppendLine();
        sb.AppendLine("## Skill instructions");
        sb.AppendLine();
        sb.Append((doc.BodyMarkdown ?? "").Trim());
        return sb.ToString();
    }

    private static string BuildBrowserToolEnforcementPrompt() =>
        """
        CRITICAL: Browser tools are enabled for this turn. For any request to open or navigate to a website,
        you MUST invoke NavigateTo (and optionally GetPageContent). Never claim you lack browser capability.
        """;

    private string BuildHomeAssistantPrompt(RoutingSession session)
    {
        var assistantName = _keyStore.HomeAssistantAssistantName;
        var sb = new StringBuilder();
        sb.AppendLine($"You have access to a Home Assistant integration named \"{assistantName}\".");
        sb.AppendLine($"When the user addresses \"{assistantName}\" directly, or continues an active smart-home session, control ANY Home Assistant device (lights, media players, switches, climate, covers, scenes, scripts, locks, fans, etc.) using tools.");
        sb.AppendLine();
        sb.AppendLine("PRIMARY workflow (REST tools — preferred for precise control):");
        sb.AppendLine("1. If entity_id is unknown, call ListEntities with domain and/or search from the user's words (room, device name, brand).");
        sb.AppendLine("2. Optionally GetEntityState to confirm current state.");
        sb.AppendLine("3. Lights: ControlLight (include brightness and color when the user asks).");
        sb.AppendLine("4. Media players (play/pause/volume/source): ControlMediaPlayer — volume_percent is 0-100 (e.g. 50 → half volume).");
        sb.AppendLine("5. Everything else: CallService(domain, service, service_data JSON with entity_id).");
        sb.AppendLine("6. If unsure of service names, call ListServices(domain).");
        sb.AppendLine();
        sb.AppendLine("SECONDARY: ProcessConversation sends natural language to HA Assist (good for area phrases). If Assist fails, use ListEntities + CallService/ControlMediaPlayer. Never claim success without a tool result.");
        sb.AppendLine();
        sb.AppendLine("You MUST call a tool before claiming you changed a device. Never invent entity_ids. Never refuse a device type without first calling ListEntities. Never invent volume or color changes.");
        sb.AppendLine();
        sb.AppendLine("Service cheat sheet:");
        sb.AppendLine("- light: ControlLight with state on/off, optional brightness 0-255, color name or #hex");
        sb.AppendLine("- media_player: ControlMediaPlayer (play/pause/stop/on/off/volume/select_source); or CallService volume_set with volume_level 0.0-1.0");
        sb.AppendLine("- switch: turn_on / turn_off");
        sb.AppendLine("- climate: set_temperature, set_hvac_mode");
        sb.AppendLine("- cover: open_cover, close_cover, stop_cover");
        sb.AppendLine("- scene / script: turn_on");
        sb.AppendLine("- fan: turn_on, turn_off, set_percentage; lock: lock, unlock");

        var summary = _keyStore.HomeAssistantDeviceSummary;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine();
            sb.AppendLine("Current known devices (may be truncated; use ListEntities for live search):");
            sb.AppendLine(summary);
        }

        if (session.IsActive("HomeAssistant", ContextualRequestRouter.SessionTtl))
        {
            sb.AppendLine();
            sb.AppendLine("ACTIVE SESSION: You are currently controlling home devices in this conversation.");
            if (!string.IsNullOrWhiteSpace(session.LastMediaPlayerEntity))
                sb.AppendLine($"Last media_player (use for volume/play/pause follow-ups): {session.LastMediaPlayerEntity}.");
            if (!string.IsNullOrWhiteSpace(session.LastLightEntity))
                sb.AppendLine($"Last light (use for light follow-ups): {session.LastLightEntity}.");
            if (!string.IsNullOrWhiteSpace(session.LastEntityActedOn))
                sb.AppendLine($"Most recent entity: {session.LastEntityActedOn}.");
            if (!string.IsNullOrWhiteSpace(session.LastAction))
                sb.AppendLine($"Last action: {session.LastAction}.");
            sb.AppendLine("For 'the volume' / 'the music' always use the last media_player entity, never a light.");
            sb.AppendLine("Follow-up messages MUST call tools (ControlMediaPlayer for volume).");
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
            if (line.Contains("control_media_player", StringComparison.OrdinalIgnoreCase))
            {
                entity = ExtractTraceParam(line, "entity") ?? entity;
                action = ExtractTraceParam(line, "action") ?? action;
            }
            else if (line.Contains("control_light", StringComparison.OrdinalIgnoreCase))
            {
                entity = ExtractTraceParam(line, "entity") ?? entity;
                action = ExtractTraceParam(line, "state") ?? action;
            }
            else if (line.Contains("call_service", StringComparison.OrdinalIgnoreCase))
            {
                var svcMatch = Regex.Match(line, @"call_service\(([^,\)]+)", RegexOptions.IgnoreCase);
                if (svcMatch.Success)
                    action = svcMatch.Groups[1].Value.Trim();

                // data={"entity_id": "media_player.foo"}
                var entityMatch = Regex.Match(
                    line,
                    @"entity_id""\s*:\s*""([^""]+)""",
                    RegexOptions.IgnoreCase);
                if (entityMatch.Success)
                    entity = entityMatch.Groups[1].Value;
            }
            else if (line.Contains("process_conversation", StringComparison.OrdinalIgnoreCase))
            {
                action = "process_conversation";
            }
            else if (line.Contains("list_entities", StringComparison.OrdinalIgnoreCase) && action is null)
            {
                action = "list_entities";
            }
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

    private bool BrowserToolsWereInvoked() =>
        _trace.GetCurrentTrace().Any(line => line.Contains("🌐", StringComparison.Ordinal));

    private static bool LooksLikeBrowserNavigationRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lower = message.ToLowerInvariant();
        string[] markers =
        [
            "navigate", "go to", "open ", "open\t", "visit ", "browse to", "load ",
            "take me to", "bring up", "pull up", "http://", "https://", "www."
        ];
        if (markers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            return true;

        // Bare domain-ish tokens: google.com, usatt.org
        return Regex.IsMatch(
            message,
            @"\b[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)+\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool ResponseClaimsCannotBrowse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        string[] markers =
        [
            "can't navigate", "cannot navigate", "can't browse", "cannot browse",
            "don't have the capability", "do not have the capability",
            "not designed to", "can't open websites", "cannot open websites",
            "i'm not able to navigate", "i am not able to navigate",
            "can't directly navigate", "cannot directly navigate"
        ];
        return markers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    private static bool ResponseClaimsHomeAssistantAction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        string[] markers =
        [
            "turned on", "turned off", "turned the", "i have turned", "i've turned",
            "light is now", "light has been", "done!", "all set", "successfully turned",
            "is now off", "is now on", "is off", "is on",
            "playing", "started playing", "now playing", "volume set", "volume has been set",
            "has been set to", "set the volume", "set to 50", "set to 60",
            "set the temperature", "opened", "closed", "activated the scene", "called the service",
            "music is now", "enjoy your music"
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

    /// <summary>Stash extracted images so save_to_gallery can find them after Omni / tool turns.</summary>
    private void BufferExtractedImages(string? conversationId, IReadOnlyList<StoreAttachment> attachments)
    {
        var convoId = string.IsNullOrWhiteSpace(conversationId) ? "_default" : conversationId;
        foreach (var att in attachments)
        {
            if (att.ContentType == null
                || !att.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(att.DataBase64))
                continue;
            try
            {
                var id = _mediaBuffer.AddImage(
                    convoId, att.DataBase64, att.ContentType, att.Name, "omni");
                _trace.Record($"🖼️ Buffered chat image id={id} for save_to_gallery");
            }
            catch (Exception ex)
            {
                _trace.Record($"🖼️ Buffer image failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Images produced this turn via lemonade tools (short tool results; bytes live in media buffer).
    /// </summary>
    private List<StoreAttachment> CollectBufferedImageAttachments(string? conversationId, DateTime sinceUtc)
    {
        var convoId = string.IsNullOrWhiteSpace(conversationId) ? "_default" : conversationId;
        var list = new List<StoreAttachment>();
        foreach (var summary in _mediaBuffer.ListRecent(convoId, 8))
        {
            if (summary.CreatedUtc < sinceUtc.AddSeconds(-2))
                continue;
            if (!_mediaBuffer.TryGetImage(convoId, summary.GenerationId, out var img) || img == null)
                continue;
            list.Add(new StoreAttachment(
                Name: img.Name ?? "generated-image.png",
                ContentType: img.ContentType,
                DataBase64: img.Base64,
                Size: (long)(img.Base64.Length * 0.75)));
        }
        return list;
    }

    /// <summary>
    /// When client tools are off (Omni) or the model skips save_to_gallery, still honor "save to X album".
    /// </summary>
    private async Task<string?> TryAutoSaveGalleryAsync(
        string lastUserMessage,
        string? conversationId,
        IReadOnlyList<StoreAttachment> attachments,
        CancellationToken ct)
    {
        var albumName = ContextualRequestRouter.TryExtractAlbumName(lastUserMessage);
        if (string.IsNullOrWhiteSpace(albumName))
        {
            // Fallback: "gallery" alone → default album name "Gallery"
            if (lastUserMessage.Contains("gallery", StringComparison.OrdinalIgnoreCase)
                || lastUserMessage.Contains("album", StringComparison.OrdinalIgnoreCase))
                albumName = "Gallery";
            else
                return null;
        }

        var image = attachments.LastOrDefault(a =>
            a.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
            && !string.IsNullOrWhiteSpace(a.DataBase64));
        if (image == null)
            return null;

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(image.DataBase64);
        }
        catch
        {
            _trace.Record("🖼️ Auto-save failed: invalid image base64");
            return null;
        }

        IServiceScope? ownedScope = null;
        try
        {
            IGalleryStore gallery;
            IGallerySyncBridge? syncBridge;
            IStorageQuotaService? quota;
            try
            {
                gallery = _services.GetService<IGalleryStore>()
                          ?? throw new InvalidOperationException("no ambient gallery");
                syncBridge = _services.GetService<IGallerySyncBridge>();
                quota = _services.GetService<IStorageQuotaService>();
            }
            catch
            {
                ownedScope = _scopeFactory.CreateScope();
                gallery = ownedScope.ServiceProvider.GetRequiredService<IGalleryStore>();
                syncBridge = ownedScope.ServiceProvider.GetService<IGallerySyncBridge>();
                quota = ownedScope.ServiceProvider.GetService<IStorageQuotaService>();
            }

            var (ok, msg) = await GallerySaveHelper.SaveImageAsync(
                gallery, syncBridge, quota, albumName, raw,
                image.ContentType ?? "image/png",
                image.Name ?? "generated-image.png",
                ct);

            if (ok)
            {
                _trace.Record("🖼️ Auto-save: " + msg);
                return msg;
            }

            _trace.Record("🖼️ Auto-save failed: " + msg);
            return "Could not auto-save to gallery: " + msg;
        }
        catch (Exception ex)
        {
            _trace.Record("🖼️ Auto-save error: " + ex.Message);
            return null;
        }
        finally
        {
            ownedScope?.Dispose();
        }
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
    private bool IsLemonadeOmniModel(string modelId)
    {
        if (!modelId.StartsWith("lemonade/", StringComparison.OrdinalIgnoreCase))
            return false;
        var name = modelId.Split('/', 2)[1];
        var settings = _keyStore.GetLemonadeModelSettings(name);
        if (settings?.IsOmniCollection == true)
            return true;
        // Heuristic for models not yet refreshed with recipe metadata.
        return name.Contains("Omni", StringComparison.OrdinalIgnoreCase)
               || name.Contains("collection.omni", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Streams tokens for OpenAI-compatible clients (Ollama, Lemonade, user-keyed).
    /// Falls back to non-streaming for proxied providers or when streaming is unsupported.
    /// </summary>
    private async Task<(string Text, ChatResponse? Response, bool Streamed, double? TtftMs, bool Cancelled)>
        GetResponsePossiblyStreamingAsync(
            IChatClient client,
            IList<AiChatMessage> chatHistory,
            ChatOptions options,
            string modelId,
            Func<string, Task>? onPartialText,
            CancellationToken ct,
            DateTime requestStart)
    {
        // Prefer streaming for local OpenAI-compatible endpoints and direct cloud keys.
        // Server proxy currently has no SSE path.
        bool tryStream = onPartialText != null && !_catalog.IsProxiedModel(modelId);

        if (tryStream)
        {
            var sb = new StringBuilder();
            var updates = new List<ChatResponseUpdate>();
            double? ttftMs = null;
            bool gotText = false;

            try
            {
                await foreach (var update in client.GetStreamingResponseAsync(chatHistory, options, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    updates.Add(update);
                    var delta = ExtractTextFromUpdate(update);
                    if (string.IsNullOrEmpty(delta))
                        continue;

                    if (!gotText)
                    {
                        gotText = true;
                        ttftMs = (DateTime.UtcNow - requestStart).TotalMilliseconds;
                    }

                    sb.Append(delta);
                    await onPartialText!(sb.ToString());
                }

                ChatResponse? asResponse = null;
                try
                {
                    asResponse = updates.ToChatResponse();
                }
                catch
                {
                    // Older or incomplete update sets — text alone is enough for the UI.
                }

                var text = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text) && asResponse != null)
                    text = ExtractFinalDisplayText(asResponse);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (onPartialText != null)
                        await onPartialText(text);
                    return (text, asResponse, Streamed: true, ttftMs, Cancelled: false);
                }

                _trace.Record("ℹ️ Streaming returned no text; falling back to non-streaming completion.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var partial = sb.ToString().Trim();
                return (partial, null, Streamed: true, ttftMs, Cancelled: true);
            }
            catch (NotSupportedException)
            {
                // e.g. ServerProxyChatClient
            }
            catch (Exception ex) when (
                ex is InvalidOperationException ||
                ex.Message.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("SSE", StringComparison.OrdinalIgnoreCase))
            {
                _trace.Record($"ℹ️ Streaming unavailable ({ex.Message}); using non-streaming completion.");
            }
        }

        ct.ThrowIfCancellationRequested();
        var response = await client.GetResponseAsync(chatHistory, options, ct);
        var fullText = ExtractFinalDisplayText(response);
        var nonStreamTtft = (DateTime.UtcNow - requestStart).TotalMilliseconds;
        if (onPartialText != null && !string.IsNullOrWhiteSpace(fullText))
            await onPartialText(fullText);
        return (fullText, response, Streamed: false, TtftMs: nonStreamTtft, Cancelled: false);
    }

    private static string ExtractTextFromUpdate(ChatResponseUpdate update)
    {
        if (update.Contents is { Count: > 0 })
        {
            var parts = update.Contents
                .OfType<TextContent>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrEmpty(t));
            return string.Concat(parts);
        }

        return update.Text ?? "";
    }

    private static (int? Prompt, int? Completion, int? Total) ExtractUsage(
        ChatResponse? response,
        string completionText,
        IList<AiChatMessage> chatHistory)
    {
        // Prefer provider-reported usage when ME.AI exposes it.
        try
        {
            if (response?.Usage is { } usage)
            {
                int? pin = usage.InputTokenCount is long pinL and > 0 ? (int)pinL : null;
                int? pout = usage.OutputTokenCount is long poutL and > 0 ? (int)poutL : null;
                int? tot = usage.TotalTokenCount is long totL and > 0
                    ? (int)totL
                    : (pin is int a && pout is int b ? a + b : null);
                if (pin != null || pout != null)
                    return (pin, pout, tot);
            }
        }
        catch
        {
            // Usage shape varies by package version.
        }

        // Fallback: rough char/4 estimate (not model-accurate, still useful for local models).
        int estOut = EstimateTokens(completionText);
        int estIn = 0;
        foreach (var m in chatHistory)
        {
            foreach (var c in m.Contents.OfType<TextContent>())
                estIn += EstimateTokens(c.Text ?? "");
        }
        if (estIn <= 0 && estOut <= 0)
            return (null, null, null);
        return (estIn > 0 ? estIn : null, estOut > 0 ? estOut : null, estIn + estOut);
    }

    private static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // ~4 chars/token is a common English heuristic for GGUF models.
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    private static int EstimateMessageListTokens(IList<AiChatMessage> history)
    {
        int n = 0;
        foreach (var m in history)
        {
            n += 4; // role / framing overhead
            foreach (var c in m.Contents)
            {
                if (c is TextContent tc)
                    n += EstimateTokens(tc.Text);
                else if (c is DataContent)
                    n += 256; // rough image/blob tax
            }
        }
        return n;
    }

    /// <summary>
    /// Drops oldest non-system messages until estimated tokens fit under <paramref name="budget"/>.
    /// Always keeps system messages and the most recent user turn when possible.
    /// </summary>
    private static List<AiChatMessage> TrimHistoryToContext(
        List<AiChatMessage> history,
        int contextLimit,
        int reserveOutput,
        out int droppedMessages)
    {
        droppedMessages = 0;
        if (history.Count == 0 || contextLimit <= 0)
            return history;

        int budget = Math.Max(512, contextLimit - Math.Max(256, reserveOutput));
        if (EstimateMessageListTokens(history) <= budget)
            return history;

        var system = history.Where(m => m.Role == ChatRole.System).ToList();
        var rest = history.Where(m => m.Role != ChatRole.System).ToList();

        while (rest.Count > 1)
        {
            var candidate = system.Concat(rest).ToList();
            if (EstimateMessageListTokens(candidate) <= budget)
                break;

            // Drop oldest non-system message
            rest.RemoveAt(0);
            droppedMessages++;
        }

        // If still over budget, drop more but keep last user message if present
        while (rest.Count > 1 && EstimateMessageListTokens(system.Concat(rest).ToList()) > budget)
        {
            rest.RemoveAt(0);
            droppedMessages++;
        }

        return system.Concat(rest).ToList();
    }

    private bool ResolveLocalCapability(string modelId, bool tools)
    {
        if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
        {
            var name = modelId.Split('/', 2)[1];
            var settings = _keyStore.GetOllamaModelSettings(name);
            if (settings != null)
                return tools ? settings.SupportsTools : settings.SupportsVision;
        }

        if (modelId.StartsWith("lemonade/", StringComparison.OrdinalIgnoreCase))
        {
            var name = modelId.Split('/', 2)[1];
            var settings = _keyStore.GetLemonadeModelSettings(name);
            if (settings != null)
            {
                if (settings.IsOmniCollection && !tools)
                    return true; // vision for omni
                return tools ? settings.SupportsTools : settings.SupportsVision;
            }
        }

        var caps = ProviderCatalog.GetCapabilitiesForModel(modelId);
        return tools ? caps.SupportsTools : caps.SupportsVision;
    }

    private int ResolveLocalContextSize(string modelId)
    {
        if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
        {
            var name = modelId.Split('/', 2)[1];
            return _keyStore.GetOllamaModelSettings(name)?.ContextSize ?? 0;
        }

        if (modelId.StartsWith("lemonade/", StringComparison.OrdinalIgnoreCase))
        {
            var name = modelId.Split('/', 2)[1];
            return _keyStore.GetLemonadeModelSettings(name)?.ContextSize ?? 0;
        }

        return 0;
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