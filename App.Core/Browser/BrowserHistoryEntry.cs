namespace App.Core.Browser;

public record BrowserHistoryEntry(
    string Url,
    string Title,
    DateTime VisitedAtUtc
);