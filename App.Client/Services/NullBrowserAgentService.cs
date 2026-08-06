using App.Core.Browser;

namespace App.Client.Services;

public sealed class NullBrowserAgentService : IBrowserAgentService
{
    public bool IsAvailable => false;

    public string CurrentUrl => "";
    public string PageTitle => "";
    public bool CanGoBack => false;
    public bool CanGoForward => false;
    public bool IsLoading => false;

    public event Action<string>? UrlChanged;
    public event Action<string>? TitleChanged;
    public event Action<bool>? LoadingChanged;

    public Task NavigateAsync(string url, CancellationToken ct = default) => NotSupported();
    public Task<bool> NavigateHomeAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task GoBackAsync(CancellationToken ct = default) => NotSupported();
    public Task GoForwardAsync(CancellationToken ct = default) => NotSupported();
    public Task RefreshAsync(CancellationToken ct = default) => NotSupported();
    public Task OpenExternallyAsync(CancellationToken ct = default) => NotSupported();
    public Task<string> GetPageTextAsync(CancellationToken ct = default) => NotSupportedString();
    public Task<string> GetPageHtmlAsync(CancellationToken ct = default) => NotSupportedString();
    public Task ClickElementAsync(string selector, CancellationToken ct = default) => NotSupported();
    public Task FillInputAsync(string selector, string value, CancellationToken ct = default) => NotSupported();
    public Task<string> EvaluateScriptAsync(string js, CancellationToken ct = default) => NotSupportedString();

    private static Task NotSupported() =>
        Task.FromException(new PlatformNotSupportedException("Browser agent is only available in the MAUI app."));

    private static Task<string> NotSupportedString() =>
        Task.FromException<string>(new PlatformNotSupportedException("Browser agent is only available in the MAUI app."));
}