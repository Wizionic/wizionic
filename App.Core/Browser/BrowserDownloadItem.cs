namespace App.Core.Browser;

public enum BrowserDownloadState
{
    InProgress,
    Completed,
    Failed,
    Cancelled
}

/// <summary>In-memory record of a browser download for the toolbar UI.</summary>
public sealed class BrowserDownloadItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public string? FilePath { get; set; }
    public string Url { get; set; } = "";
    public BrowserDownloadState State { get; set; } = BrowserDownloadState.InProgress;
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }

    public double? ProgressFraction =>
        TotalBytes is > 0 ? Math.Clamp(BytesReceived / (double)TotalBytes.Value, 0, 1) : null;
}
