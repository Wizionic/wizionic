namespace App.Core.Connectors;

/// <summary>
/// Carries OAuth deep-link / handoff query and status into the Tools page
/// (MAUI in-app browser intercept, protocol activation, or web query string).
/// </summary>
public sealed class OAuthReturnBridge
{
    private string? _pendingQuery;
    private string? _statusMessage;
    private bool _statusIsError;

    public event Action? Changed;

    /// <summary>Raw query string (without leading ?) e.g. oauth_session=abc&amp;oauth_connector=github</summary>
    public string? PendingQuery => _pendingQuery;

    public string? StatusMessage => _statusMessage;
    public bool StatusIsError => _statusIsError;

    public void SetFromUri(Uri uri)
    {
        if (uri is null) return;
        var q = uri.Query;
        if (string.IsNullOrWhiteSpace(q))
        {
            // Some activations put params in Fragment
            q = uri.Fragment.StartsWith('#') ? uri.Fragment[1..] : uri.Fragment;
        }
        if (q.StartsWith('?'))
            q = q[1..];
        if (string.IsNullOrWhiteSpace(q))
            return;
        _pendingQuery = q;
        Changed?.Invoke();
    }

    public void SetFromQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        _pendingQuery = query.StartsWith('?') ? query[1..] : query;
        Changed?.Invoke();
    }

    public string? TakePendingQuery()
    {
        var q = _pendingQuery;
        _pendingQuery = null;
        return q;
    }

    public void SetStatus(string message, bool isError)
    {
        _statusMessage = message;
        _statusIsError = isError;
        Changed?.Invoke();
    }

    public (string? Message, bool IsError)? TakeStatus()
    {
        if (string.IsNullOrWhiteSpace(_statusMessage))
            return null;
        var r = (_statusMessage, _statusIsError);
        _statusMessage = null;
        _statusIsError = false;
        return r;
    }
}
