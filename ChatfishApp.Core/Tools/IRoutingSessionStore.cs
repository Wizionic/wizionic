namespace ChatfishApp.Core.Tools;

public sealed class RoutingSession
{
    public string? LastActiveModule { get; set; }
    public string? LastEntityActedOn { get; set; }
    public string? LastAction { get; set; }
    public DateTime? LastToolCallAt { get; set; }

    /// <summary>Last light.* entity successfully controlled in this conversation.</summary>
    public string? LastLightEntity { get; set; }

    /// <summary>Last media_player.* entity successfully controlled in this conversation.</summary>
    public string? LastMediaPlayerEntity { get; set; }

    public bool IsActive(string module, TimeSpan ttl)
    {
        if (LastActiveModule != module || !LastToolCallAt.HasValue)
            return false;

        return DateTime.UtcNow - LastToolCallAt.Value <= ttl;
    }
}

/// <summary>
/// Per-conversation routing session for follow-up tool routing without keyword lists.
/// </summary>
public interface IRoutingSessionStore
{
    RoutingSession Get(string? conversationId);
    void RecordToolInvocation(string? conversationId, string module, string? entityId, string? action);
    void Clear(string? conversationId);
}
