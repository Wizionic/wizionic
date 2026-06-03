using ChatfishApp.Contracts;
using ChatfishApp.Data;
using ChatfishApp.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using System.ClientModel;

namespace ChatfishApp.Hubs;

public class ChatHub : Hub
{
    private readonly AiProviderService _aiProvider;
    private readonly ChatfishDbContext _db;

    public ChatHub(AiProviderService aiProvider, ChatfishDbContext db)
    {
        _aiProvider = aiProvider;
        _db = db;
    }

public async Task SendMessage(string message, string model)
    {

        var email = Context.User?.Identity?.Name;

        if (email == null)
        {
            Console.WriteLine("Unauthenticated user tried to send message");
            return;
        }

        // Get user
        var user = await _db.Users
            .Include(u => u.Conversations)
            .ThenInclude(c => c.Messages)
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user == null)
        {
            Console.WriteLine("User not found!");
            return;
        }
        else
        {
            Console.WriteLine($"User found: {user.Id}");
        }

        // Get or create conversation
        var convo = user.Conversations.OrderByDescending(c => c.Id).FirstOrDefault();

        if (convo == null)
        {
            convo = new Conversation { UserId = user.Id };
            _db.Conversations.Add(convo);
            await _db.SaveChangesAsync();
        }

        // Add user message
        var userMsg = new Message
        {
            ConversationId = convo.Id,
            Role = "user",
            Content = message,
            Model = model
        };

        _db.Messages.Add(userMsg);
        await _db.SaveChangesAsync();

        await Clients.All.SendAsync("ReceiveMessage", email, message, model);
        await Clients.All.SendAsync("BotTyping", "ChatFish");

        // Build context
        var history = convo.Messages
            .Select(m => (m.Role, m.Content))
            .ToList();

        history.Add(("user", message));

        // Use the pluggable provider (IChatClient) - duplicated conversion for the dead hub path.
        string reply;
        try
        {
            var client = await _aiProvider.GetChatClientForModelAsync(model);
            var chatHistory = history
                .Select(h => new Microsoft.Extensions.AI.ChatMessage(
                    string.Equals(h.Role, "user", StringComparison.OrdinalIgnoreCase)
                        ? Microsoft.Extensions.AI.ChatRole.User
                        : Microsoft.Extensions.AI.ChatRole.Assistant,
                    h.Content))
                .ToList();
            var response = await client.GetResponseAsync(chatHistory);
            reply = response.Text ?? "I'm not sure how to respond.";
        }
        catch (ClientResultException ex)
        {
            var status = ex.Status;
            var entry = ProviderCatalog.GetModel(model);
            string providerName = entry?.Provider.DisplayName ?? "the provider";
            reply = status switch
            {
                429 => $"Rate limit (429) from {providerName}. This provider/key has low rate limits. Wait a bit, check your provider dashboard, or add credits if needed.",
                401 or 403 => $"Auth error for {providerName}. Check key in Settings.",
                _ => $"Provider error ({status}) for {providerName}."
            };
        }
        catch (Exception ex)
        {
            reply = "ChatFish error: " + ex.Message;
        }

        // Save bot reply
        var botMsg = new Message
        {
            ConversationId = convo.Id,
            Role = "assistant",
            Content = reply,
            Model = model
        };

        _db.Messages.Add(botMsg);
        await _db.SaveChangesAsync();

        await Clients.All.SendAsync("ReceiveMessage", "ChatFish", reply, model);
        await Clients.All.SendAsync("BotDone");
    }

    public async Task NewConversation(string email)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null) return;

        var convo = new Conversation { UserId = user.Id };
        _db.Conversations.Add(convo);
        await _db.SaveChangesAsync();
    }
}
