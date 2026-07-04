namespace ChatfishApp.Core.Browser;

public interface IBrowserSideAgentService
{
    bool IsAvailable { get; }
    string CurrentUrl { get; }
    bool IsLoading { get; }

    Task NavigateAsync(string url, CancellationToken ct = default);
    Task RefreshAsync(CancellationToken ct = default);

    event Action<string>? UrlChanged;
    event Action<bool>? LoadingChanged;
}