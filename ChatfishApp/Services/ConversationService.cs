using ChatfishApp.Contracts;
using ChatfishApp.Data;
using ChatfishApp.Services.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.Runtime.CompilerServices;

namespace ChatfishApp.Services;

public class ConversationService
{
    private readonly ChatfishDbContext _db;
    private readonly AiProviderService _aiProvider;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<ConversationService> _logger;
    private readonly IToolProvider _toolProvider;

    public ConversationService(ChatfishDbContext db, AiProviderService aiProvider, IHttpContextAccessor http, ILogger<ConversationService> logger, IToolProvider toolProvider)
    {
        _db = db;
        _aiProvider = aiProvider;
        _http = http;
        _logger = logger;
        _toolProvider = toolProvider;
    }

    private async Task<User> GetCurrentUserAsync()
    {
        var email = _http.HttpContext?.User?.Identity?.Name;
        if (string.IsNullOrEmpty(email))
            throw new InvalidOperationException("User not authenticated.");

        var user = await _db.Users
            .Include(u => u.Conversations)
            .ThenInclude(c => c.Messages)
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user == null)
            throw new InvalidOperationException("User not found.");

        return user;
    }

    // -----------------------------
    // Conversation List
    // -----------------------------
    public async Task<List<Conversation>> GetUserConversationsAsync()
    {
        var user = await GetCurrentUserAsync();

        return user.Conversations
            .OrderByDescending(c => c.CreatedAt)
            .ToList();
    }

    // -----------------------------
    // Load Messages
    // -----------------------------
    public async Task<List<Message>> GetConversationMessagesAsync(Guid conversationId)
    {
        var user = await GetCurrentUserAsync();

        var convo = await _db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == user.Id);

        if (convo == null)
            throw new InvalidOperationException("Conversation not found.");

        return convo.Messages
            .OrderBy(m => m.Timestamp)
            .ToList();
    }

    // -----------------------------
    // New Conversation
    // -----------------------------
    public async Task<Guid> StartNewConversationAsync()
    {
        var user = await GetCurrentUserAsync();

        var convo = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
            Title = "(empty)"
        };

        _db.Conversations.Add(convo);
        await _db.SaveChangesAsync();

        return convo.Id;
    }

    public static string GenerateTitle(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "(empty)";

        content = content.Trim();

        return content.Length <= 20
            ? content
            : content.Substring(0, 20) + "...";
    }


    // -----------------------------
    // Delete Conversation
    // -----------------------------
    public async Task DeleteConversationAsync(Guid conversationId)
    {
        var user = await GetCurrentUserAsync();

        var convo = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == user.Id);

        if (convo == null)
            return;

        _db.Conversations.Remove(convo);
        await _db.SaveChangesAsync();
    }

    // -----------------------------
    // Streaming AI Response
    // -----------------------------
    public async IAsyncEnumerable<string> StreamMessageAsync(
        Guid conversationId,
        string message,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();

        var convo = await _db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == user.Id);

        if (convo == null)
            throw new InvalidOperationException("Conversation not found.");

        // Save user message
        var userMsg = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = convo.Id,
            Role = "user",
            Content = message,
            Model = model,
            Timestamp = DateTime.UtcNow
        };

        _db.Messages.Add(userMsg);
        await _db.SaveChangesAsync();

        // Build history
        var history = convo.Messages
            .OrderBy(m => m.Timestamp)
            .Select(m => (m.Role, m.Content))
            .ToList();

        history.Add(("user", message));

        // Get full reply via the pluggable IChatClient (ME.AI abstraction).
        // History converted to proper ChatMessage roles. Fake streaming is preserved below.
        string fullReply;
        try
        {
            _logger.LogInformation("Calling LLM for model {Model} with {HistoryCount} history messages (user: {User})", 
                model, history.Count, user.Email);

            var baseClient = await _aiProvider.GetChatClientForModelAsync(model);

            var chatHistory = history
                .Select(h => new ChatMessage(
                    string.Equals(h.Role, "user", StringComparison.OrdinalIgnoreCase)
                        ? ChatRole.User
                        : ChatRole.Assistant,
                    h.Content))
                .ToList();

            var modelEntry = ProviderCatalog.GetModel(model);
            bool supportsTools = modelEntry?.Model.SupportsTools ?? false;  // default conservative

            ChatOptions chatOptions = new();
            IChatClient client = baseClient;

            if (supportsTools)
            {
                // Wrap with ME.AI function invocation so the model can autonomously use tools
                // (web_search, summarize_url, get_current_time, etc.) when it "needs to".
                // This is the .NET equivalent of agent loops (similar to what OpenRouter's Agent SDK provides in TS/Python).
                // The middleware handles the full loop: model emits tool call(s) → we execute the C# AIFunction(s) → feed results back → repeat until final text.
                client = baseClient
                    .AsBuilder()
                    .UseFunctionInvocation()   // middleware that auto-executes tools when the model calls them
                    .Build();

                chatOptions = new ChatOptions { Tools = _toolProvider.GetTools().ToList() };
                _logger.LogInformation("Tools enabled for model {Model}", model);
            }
            else
            {
                _logger.LogInformation("Tools disabled for model {Model} (not supported or not marked)", model);
            }

            var response = await client.GetResponseAsync(chatHistory, chatOptions, cancellationToken: cancellationToken);
            fullReply = response.Text ?? "I'm not sure how to respond.";

            _logger.LogInformation("LLM call for {Model} succeeded, response length {Length} (tools may have been used)", model, fullReply.Length);
        }
        catch (ClientResultException ex)
        {
            // HTTP-level errors from the provider (via the OpenAI compat client).
            var status = ex.Status;
            var entry = ProviderCatalog.GetModel(model);
            string providerName = entry?.Provider.DisplayName ?? "the provider";

            _logger.LogWarning(ex, "LLM call failed for model {Model} with status {Status}", model, status);

            fullReply = status switch
            {
                429 => $"Rate limit (429) from {providerName}. This provider/key has low rate limits. Wait a bit before retrying, check your provider dashboard (e.g. Rate Limit tab), or add credits if needed. See Settings page for guidance.",
                401 or 403 => $"Authentication failed for {providerName} (status {status}). Please verify your API key in Settings.",
                >= 400 and < 500 => $"Request error from {providerName} (status {status}). Check the model and key in Settings. Details: {ex.Message}",
                _ => $"Service error from {providerName} (status {status}). Try again later."
            };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No API key", StringComparison.OrdinalIgnoreCase))
        {
            fullReply = ex.Message; // Already user-friendly from AiProviderService
        }
        catch (Exception ex)
        {
            fullReply = $"ChatFish had trouble contacting the model: {ex.Message}";
        }

        // Save bot message
        var botMsg = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = convo.Id,
            Role = "assistant",
            Content = fullReply,
            Model = model,
            Timestamp = DateTime.UtcNow
        };

        _db.Messages.Add(botMsg);
        await _db.SaveChangesAsync();

        // Fake streaming: reveal in chunks
        var chunkSize = 40;
        for (int i = chunkSize; i <= fullReply.Length; i += chunkSize)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            yield return fullReply[..i];
            await Task.Delay(40, cancellationToken);
        }

        if (fullReply.Length % chunkSize != 0)
            yield return fullReply;
    }

    public async Task<string> GetConversationTitleAsync(Guid conversationId)
    {
        var firstMessage = await _db.Messages
            .Where(m => m.ConversationId == conversationId && m.Role == "user")
            .OrderBy(m => m.Timestamp)
            .FirstOrDefaultAsync();

        if (firstMessage == null)
            return "(empty)";

        var text = firstMessage.Content.Trim();

        return text.Length <= 25 ? text : text.Substring(0, 25) + " ..";
    }


}
