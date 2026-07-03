using ChatfishApp.Core.Browser;
using ChatfishApp.Core.UI;

namespace ChatfishApp.Maui.Services;

public sealed class MauiBrowserContext : IBrowserContext
{
    private readonly MauiBrowserAgentService _agent;
    private readonly IBrowserPanelState _panel;

    public MauiBrowserContext(MauiBrowserAgentService agent, IBrowserPanelState panel)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
    }

    public bool IsAvailable => _panel.IsOpen && _agent.IsAvailable;

    public async Task<string> NavigateAsync(string url, CancellationToken ct = default)
    {
        await _agent.NavigateAsync(url, ct);
        return string.IsNullOrWhiteSpace(_agent.CurrentUrl)
            ? "Navigation failed."
            : $"Navigated to {_agent.CurrentUrl}";
    }

    public async Task<string> GetPageContentAsync(CancellationToken ct = default)
    {
        var text = await _agent.GetPageTextAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? "No page content available." : text;
    }

    public async Task<string> ClickElementAsync(string selector, CancellationToken ct = default)
    {
        await _agent.ClickElementAsync(selector, ct);
        return $"Clicked element: {selector}";
    }

    public async Task<string> FillFieldAsync(string selector, string value, CancellationToken ct = default)
    {
        await _agent.FillInputAsync(selector, value, ct);
        return $"Filled field: {selector}";
    }
}