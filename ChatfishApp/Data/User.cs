namespace ChatfishApp.Data;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? MagicLinkToken { get; set; }
    public DateTime? MagicLinkExpiresAt { get; set; }

    public List<Conversation> Conversations { get; set; } = new();
}
