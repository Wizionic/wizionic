using ChatfishApp.Core.Browser;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Stub browser context until MAUI WebView integration is built.
/// </summary>
public sealed class NullBrowserContext : IBrowserContext
{
    public bool IsAvailable => false;

    public Task<string> NavigateAsync(string url, CancellationToken ct = default) =>
        Unavailable();

    public Task<string> GetPageContentAsync(CancellationToken ct = default) =>
        Unavailable();

    public Task<string> ClickElementAsync(string selector, CancellationToken ct = default) =>
        Unavailable();

    public Task<string> FillFieldAsync(string selector, string value, CancellationToken ct = default) =>
        Unavailable();

    private static Task<string> Unavailable() =>
        Task.FromResult("Browser agent is not available. No embedded browser is active.");
}