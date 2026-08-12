using System.Collections.Concurrent;

namespace App.Services.OAuth;

/// <summary>
/// In-memory store for OAuth CSRF state and one-shot token handoff sessions.
/// Tokens are never written to the database.
/// </summary>
public sealed class OAuthSessionStore
{
    private static readonly TimeSpan AuthTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, OAuthPendingAuth> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OAuthSessionResult> _sessions = new(StringComparer.Ordinal);

    public void PutPending(OAuthPendingAuth auth) =>
        _pending[auth.State] = auth;

    public OAuthPendingAuth? TakePending(string state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        if (!_pending.TryRemove(state, out var auth))
            return null;
        if (DateTimeOffset.UtcNow - auth.CreatedAtUtc > AuthTtl)
            return null;
        return auth;
    }

    public string PutSession(OAuthSessionResult result)
    {
        if (string.IsNullOrWhiteSpace(result.SessionId))
            result.SessionId = Guid.NewGuid().ToString("N");
        _sessions[result.SessionId] = result;
        return result.SessionId;
    }

    public OAuthSessionResult? TakeSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        if (!_sessions.TryRemove(sessionId, out var session))
            return null;
        if (DateTimeOffset.UtcNow - session.CreatedAtUtc > SessionTtl)
            return null;
        return session;
    }

    public void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _pending)
        {
            if (now - kv.Value.CreatedAtUtc > AuthTtl)
                _pending.TryRemove(kv.Key, out _);
        }
        foreach (var kv in _sessions)
        {
            if (now - kv.Value.CreatedAtUtc > SessionTtl)
                _sessions.TryRemove(kv.Key, out _);
        }
    }
}
