using ChatfishApp.Data;
using ChatfishApp.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChatfishApp.Hubs;

public class ChatHub : Hub
{
    private readonly AiChatService _ai;
    private readonly ChatfishDbContext _db;

    public ChatHub(AiChatService ai, ChatfishDbContext db)
    {
        _ai = ai;
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

        var reply = await _ai.GetBotReply(model, history);

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
