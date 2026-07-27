using System.Collections.Concurrent;
using ChatfishApp.Core.Tools;

namespace ChatfishApp.Shared.Services.Tools;

public sealed class InMemoryRoutingSessionStore : IRoutingSessionStore
{
    private readonly ConcurrentDictionary<string, RoutingSession> _sessions = new(StringComparer.Ordinal);

    public RoutingSession Get(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return new RoutingSession();

        return _sessions.GetOrAdd(conversationId, _ => new RoutingSession());
    }

    public void RecordToolInvocation(string? conversationId, string module, string? entityId, string? action)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(module))
            return;

        var session = _sessions.GetOrAdd(conversationId, _ => new RoutingSession());
        session.LastActiveModule = module;
        if (!string.IsNullOrWhiteSpace(entityId))
        {
            session.LastEntityActedOn = entityId;
            if (entityId.StartsWith("media_player.", StringComparison.OrdinalIgnoreCase))
                session.LastMediaPlayerEntity = entityId;
            else if (entityId.StartsWith("light.", StringComparison.OrdinalIgnoreCase))
                session.LastLightEntity = entityId;
        }

        session.LastAction = action;
        session.LastToolCallAt = DateTime.UtcNow;
    }

    public void Clear(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return;

        _sessions.TryRemove(conversationId, out _);
    }
}
