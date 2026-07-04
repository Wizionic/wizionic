namespace ChatfishApp.Core.Browser;

public enum BrowserSearchEngineKind
{
    Brave,
    DuckDuckGo,
    Bing,
    Google,
    Custom
}

public enum BrowserHomepageKind
{
    SearchEngine,
    Bookmarks,
    CustomUrl
}

public enum BrowserNewWindowBehavior
{
    NewTab,
    ExternalBrowser
}

public sealed class BrowserSettings
{
    public BrowserSearchEngineKind SearchEngine { get; set; } = BrowserSearchEngineKind.Brave;
    public string CustomSearchUrl { get; set; } = "";
    public BrowserHomepageKind Homepage { get; set; } = BrowserHomepageKind.SearchEngine;
    public string CustomHomepageUrl { get; set; } = "";
    public BrowserNewWindowBehavior NewWindowBehavior { get; set; } = BrowserNewWindowBehavior.NewTab;
    public bool AskBeforeDownloading { get; set; } = true;
    public bool ClearCookiesOnExit { get; set; }
    public bool ClearCacheOnExit { get; set; }
    public bool ClearHistoryOnExit { get; set; }
    public bool ShowBookmarksBar { get; set; }

    public BrowserSettings Clone() => new()
    {
        SearchEngine = SearchEngine,
        CustomSearchUrl = CustomSearchUrl,
        Homepage = Homepage,
        CustomHomepageUrl = CustomHomepageUrl,
        NewWindowBehavior = NewWindowBehavior,
        AskBeforeDownloading = AskBeforeDownloading,
        ClearCookiesOnExit = ClearCookiesOnExit,
        ClearCacheOnExit = ClearCacheOnExit,
        ClearHistoryOnExit = ClearHistoryOnExit,
        ShowBookmarksBar = ShowBookmarksBar
    };
}