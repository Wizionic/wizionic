using Markdig;

namespace App.Services;

/// <summary>
/// Serves /privacy and /terms as static HTML from docs/*.md so crawlers
/// (Google OAuth, SignPath) do not need the WASM client.
/// </summary>
public static class LegalDocumentEndpoints
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static void MapLegalDocuments(this WebApplication app)
    {
        app.MapGet("/privacy", (IWebHostEnvironment env) => Render(env, "privacy.md", "Privacy Policy"));
        app.MapGet("/terms", (IWebHostEnvironment env) => Render(env, "terms.md", "Terms of Service"));
    }

    private static IResult Render(IWebHostEnvironment env, string fileName, string title)
    {
        var path = Resolve(env, fileName);
        if (path is null)
            return Results.NotFound();

        var markdown = File.ReadAllText(path);
        var body = Markdown.ToHtml(markdown, Pipeline);
        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{{title}} · Wizionic</title>
              <style>
                body { font-family: "Segoe UI", system-ui, sans-serif; line-height: 1.55; color: #171717; background: #fafafa; margin: 0; }
                header, main, footer { max-width: 44rem; margin: 0 auto; padding: 1.25rem 1.25rem; }
                header { display: flex; align-items: center; gap: 0.75rem; border-bottom: 1px solid #e5e5e5; }
                header img { width: 36px; height: 36px; }
                header a { color: inherit; text-decoration: none; font-weight: 650; }
                main { background: #fff; border-left: 1px solid #eee; border-right: 1px solid #eee; }
                h1, h2, h3 { line-height: 1.25; }
                table { border-collapse: collapse; width: 100%; margin: 1rem 0; }
                th, td { border: 1px solid #e5e5e5; padding: 0.4rem 0.55rem; text-align: left; vertical-align: top; }
                a { color: #0a0a0a; }
                footer { color: #525252; font-size: 0.9rem; border-top: 1px solid #e5e5e5; }
                footer a { color: inherit; }
              </style>
            </head>
            <body>
              <header>
                <a href="/"><img src="/images/app50.png" alt="Wizionic" /></a>
                <a href="/">Wizionic</a>
              </header>
              <main>{{body}}</main>
              <footer>
                <a href="/privacy">Privacy</a> ·
                <a href="/terms">Terms</a> ·
                <a href="https://github.com/Wizionic/wizionic/blob/main/SECURITY.md">Security</a> ·
                <a href="https://github.com/Wizionic/wizionic">Source</a>
              </footer>
            </body>
            </html>
            """;

        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static string? Resolve(IWebHostEnvironment env, string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(env.ContentRootPath, "docs", fileName),
            Path.Combine(AppContext.BaseDirectory, "docs", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "docs", fileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
