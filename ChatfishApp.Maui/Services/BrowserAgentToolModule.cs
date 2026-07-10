using System.ComponentModel;
using ChatfishApp.Core.Browser;
using ChatfishApp.Core.Tools;
using ChatfishApp.Shared.Services.Tools;
using Microsoft.Extensions.AI;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Browser agent tools for embedded WebView control. Unavailable until WebView bridge exists.
/// </summary>
public sealed class BrowserAgentToolModule : IToolModule
{
    private readonly IBrowserContext _browser;
    private readonly IToolExecutionTrace _trace;
    private readonly IReadOnlyList<AITool> _tools;

    public string ModuleName => "BrowserAgent";
    public bool IsAvailable => _browser.IsAvailable;

    public BrowserAgentToolModule(IBrowserContext browser, IToolExecutionTrace trace)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));

        _tools =
        [
            AIFunctionFactory.Create(NavigateTo, name: "NavigateTo"),
            AIFunctionFactory.Create(GetPageContent, name: "GetPageContent"),
            AIFunctionFactory.Create(ClickElement, name: "ClickElement"),
            AIFunctionFactory.Create(FillField, name: "FillField")
        ];
    }

    public IReadOnlyList<AITool> GetTools() => IsAvailable ? _tools : [];

    [Description("REQUIRED for navigation: open a URL in the Chatfish embedded browser panel. Pass a full http(s) URL (add https:// if the user only gave a domain like google.com).")]
    private async Task<string> NavigateTo(
        [Description("Full http(s) URL to open in the embedded browser")] string url)
    {
        _trace.Record($"🌐 navigate_to(url=\"{url}\")");
        if (string.IsNullOrWhiteSpace(url))
            return "Navigation failed: empty URL.";

        // Normalize bare domains so tool calls like "google.com" still work.
        var target = url.Trim();
        if (!target.Contains("://", StringComparison.Ordinal))
            target = "https://" + target.TrimStart('/');

        var result = await _browser.NavigateAsync(target);
        _trace.Record($"   {(result.Contains("not available", StringComparison.OrdinalIgnoreCase) ? "❌" : "✅")} {result}");
        return result;
    }

    [Description("Read the visible text of the page currently shown in the embedded browser.")]
    private async Task<string> GetPageContent()
    {
        _trace.Record("🌐 get_page_content()");
        var result = await _browser.GetPageContentAsync();
        _trace.Record($"   ✅ returned {result.Length} chars");
        return result;
    }

    [Description("Click an element on the current embedded-browser page by CSS selector.")]
    private async Task<string> ClickElement(
        [Description("CSS selector, e.g. '#submit' or 'button.login'")] string selector)
    {
        _trace.Record($"🌐 click_element(selector=\"{selector}\")");
        var result = await _browser.ClickElementAsync(selector);
        _trace.Record($"   ✅ {result}");
        return result;
    }

    [Description("Fill a form field on the current embedded-browser page by CSS selector.")]
    private async Task<string> FillField(
        [Description("CSS selector for the input field")] string selector,
        [Description("Value to type into the field")] string value)
    {
        _trace.Record($"🌐 fill_field(selector=\"{selector}\")");
        var result = await _browser.FillFieldAsync(selector, value);
        _trace.Record($"   ✅ {result}");
        return result;
    }
}