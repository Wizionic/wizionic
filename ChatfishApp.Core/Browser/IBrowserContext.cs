namespace ChatfishApp.Core.Browser;

/// <summary>
/// Bridge to an embedded browser/WebView for agentic browsing (MAUI target).
/// </summary>
public interface IBrowserContext
{
    bool IsAvailable { get; }

    Task<string> NavigateAsync(string url, CancellationToken ct = default);

    Task<string> GetPageContentAsync(CancellationToken ct = default);

    Task<string> ClickElementAsync(string selector, CancellationToken ct = default);

    Task<string> FillFieldAsync(string selector, string value, CancellationToken ct = default);
}