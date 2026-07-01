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
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(GetPageContent),
            AIFunctionFactory.Create(ClickElement),
            AIFunctionFactory.Create(FillField)
        ];
    }

    public IReadOnlyList<AITool> GetTools() => IsAvailable ? _tools : [];

    [Description("Navigate the embedded browser to a URL.")]
    private async Task<string> NavigateTo(
        [Description("Full http(s) URL to open")] string url)
    {
        _trace.Record($"🌐 navigate_to(url=\"{url}\")");
        var result = await _browser.NavigateAsync(url);
        _trace.Record($"   {(result.Contains("not available", StringComparison.OrdinalIgnoreCase) ? "❌" : "✅")} {result}");
        return result;
    }

    [Description("Get the text content of the current browser page.")]
    private async Task<string> GetPageContent()
    {
        _trace.Record("🌐 get_page_content()");
        var result = await _browser.GetPageContentAsync();
        _trace.Record($"   ✅ returned {result.Length} chars");
        return result;
    }

    [Description("Click an element on the current page by CSS selector.")]
    private async Task<string> ClickElement(
        [Description("CSS selector, e.g. '#submit' or 'button.login'")] string selector)
    {
        _trace.Record($"🌐 click_element(selector=\"{selector}\")");
        var result = await _browser.ClickElementAsync(selector);
        _trace.Record($"   ✅ {result}");
        return result;
    }

    [Description("Fill a form field on the current page by CSS selector.")]
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