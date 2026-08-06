using App.Core.Browser;

namespace App.Client.Services;

public sealed class NullBrowserSideAgentService : IBrowserSideAgentService
{
    public bool IsAvailable => false;
    public string CurrentUrl => "";
    public bool IsLoading => false;

    public event Action<string>? UrlChanged;
    public event Action<bool>? LoadingChanged;

    public Task NavigateAsync(string url, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
}