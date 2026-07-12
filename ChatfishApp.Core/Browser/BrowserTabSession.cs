namespace ChatfishApp.Core.Browser;

/// <summary>
/// In-memory placeholder for a browser tab. Only the active tab is loaded in the WebView;
/// background tabs store URL/history/chrome metadata until activated.
/// </summary>
public sealed class BrowserTabSession
{
    public BrowserTabSession(string? id = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
    }

    public string Id { get; }

    public string Title { get; set; } = "";

    public string Url { get; set; } = "";

    public string? FaviconUrl { get; set; }

    /// <summary>Per-tab back/forward stack (managed in C#, not native WebView history).</summary>
    public List<string> History { get; } = [];

    public int HistoryIndex { get; set; } = -1;

    public double ScrollX { get; set; }

    public double ScrollY { get; set; }

    /// <summary>True when this tab shows the bookmarks start page instead of a WebView URL.</summary>
    public bool IsStartPage { get; set; }

    public DateTimeOffset LastActivatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayTitle
    {
        get
        {
            if (IsStartPage)
                return "Bookmarks";
            if (!string.IsNullOrWhiteSpace(Title))
                return Title;
            if (!string.IsNullOrWhiteSpace(Url))
                return Url;
            return "New tab";
        }
    }
}
