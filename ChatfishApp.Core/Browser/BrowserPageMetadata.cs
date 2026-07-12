namespace ChatfishApp.Core.Browser;

/// <summary>
/// Helpers for reading page title/favicon from an embedded browser document.
/// Blazor/SPA sites often set <c>document.title</c> after the first navigation event.
/// </summary>
public static class BrowserPageMetadata
{
    /// <summary>
    /// Script that returns a JSON string: best-effort page title (title, og:title, or hostname).
    /// </summary>
    public const string ReadTitleScript =
        """
        (function () {
          try {
            var t = (document.title || "").trim();
            if (!t) {
              var og = document.querySelector('meta[property="og:title"]');
              if (og && og.content) t = (og.content || "").trim();
            }
            if (!t) {
              var app = document.querySelector('meta[name="apple-mobile-web-app-title"]');
              if (app && app.content) t = (app.content || "").trim();
            }
            if (!t && location && location.hostname) t = location.hostname;
            return t || "";
          } catch (e) { return ""; }
        })()
        """;

    /// <summary>
    /// Script that returns absolute href of the best link[rel=icon] (largest preferred), or favicon.ico.
    /// </summary>
    public const string ReadFaviconHrefScript =
        """
        (function () {
          try {
            var links = Array.prototype.slice.call(
              document.querySelectorAll('link[rel~="icon"], link[rel="shortcut icon"], link[rel="apple-touch-icon"], link[rel="apple-touch-icon-precomposed"]')
            );
            function score(link) {
              var s = 0;
              var rel = (link.rel || "").toLowerCase();
              if (rel.indexOf("apple-touch") >= 0) s += 50;
              if (rel.indexOf("shortcut") >= 0) s += 10;
              var sizes = (link.sizes && link.sizes.value) ? link.sizes.value : (link.getAttribute("sizes") || "");
              var m = /(\d+)x(\d+)/i.exec(sizes);
              if (m) s += Math.min(parseInt(m[1], 10) || 0, 256);
              var type = (link.type || "").toLowerCase();
              if (type.indexOf("png") >= 0 || type.indexOf("svg") >= 0) s += 20;
              if (type.indexOf("ico") >= 0) s += 5;
              return s;
            }
            links.sort(function (a, b) { return score(b) - score(a); });
            for (var i = 0; i < links.length; i++) {
              var href = links[i].href;
              if (href && href.indexOf("data:") !== 0) return href;
            }
            if (location && location.origin) return location.origin + "/favicon.ico";
            return "";
          } catch (e) { return ""; }
        })()
        """;

    /// <summary>
    /// Prefer a human title; avoid using a raw URL when title failed to load.
    /// </summary>
    public static string ResolveBookmarkTitle(string? pageTitle, string? pageUrl)
    {
        var title = (pageTitle ?? "").Trim();
        var url = (pageUrl ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(title)
            && !title.Equals(url, StringComparison.OrdinalIgnoreCase)
            && !title.StartsWith("Script error:", StringComparison.OrdinalIgnoreCase)
            && !title.Equals("null", StringComparison.OrdinalIgnoreCase))
            return title;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return string.IsNullOrWhiteSpace(url) ? "Bookmark" : url;
    }
}
