namespace ChatfishApp.Core.Browser;

/// <summary>
/// Bridge to an embedded browser/WebView for UI navigation and agentic browsing (MAUI target).
/// </summary>
public interface IBrowserAgentService
{
    bool IsAvailable { get; }

    string CurrentUrl { get; }
    string PageTitle { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    bool IsLoading { get; }

    Task NavigateAsync(string url, CancellationToken ct = default);
    /// <summary>Navigate to configured homepage. Returns false when homepage is the bookmarks grid.</summary>
    Task<bool> NavigateHomeAsync(CancellationToken ct = default);
    Task GoBackAsync(CancellationToken ct = default);
    Task GoForwardAsync(CancellationToken ct = default);
    Task RefreshAsync(CancellationToken ct = default);
    Task OpenExternallyAsync(CancellationToken ct = default);

    Task<string> GetPageTextAsync(CancellationToken ct = default);
    Task<string> GetPageHtmlAsync(CancellationToken ct = default);
    Task ClickElementAsync(string selector, CancellationToken ct = default);
    Task FillInputAsync(string selector, string value, CancellationToken ct = default);
    Task<string> EvaluateScriptAsync(string js, CancellationToken ct = default);

    event Action<string>? UrlChanged;
    event Action<string>? TitleChanged;
    event Action<bool>? LoadingChanged;
}