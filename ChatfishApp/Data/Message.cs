namespace ChatfishApp.Data;

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    public string Role { get; set; } = "";   // "user" or "assistant"
    public string Content { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
