using ChatfishApp.Data;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;

namespace ChatfishApp.Services;

public class ConversationService
{
    private readonly ChatfishDbContext _db;
    private readonly AiChatService _ai;
    private readonly IHttpContextAccessor _http;

    public ConversationService(ChatfishDbContext db, AiChatService ai, IHttpContextAccessor http)
    {
        _db = db;
        _ai = ai;
        _http = http;
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

        // Get full reply (non-streaming for now)
        var fullReply = await _ai.GetBotReply(model, history);

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
