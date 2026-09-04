namespace App.Core.Support;

public enum ContentReportSurface
{
    Chat,
    Image,
    Speech,
    NotesAi,
    BrowserAgent,
    HomeAssistant
}

public static class ContentReportSurfaces
{
    public static string ToWire(ContentReportSurface surface) => surface switch
    {
        ContentReportSurface.Chat => "chat",
        ContentReportSurface.Image => "image",
        ContentReportSurface.Speech => "speech",
        ContentReportSurface.NotesAi => "notes-AI",
        ContentReportSurface.BrowserAgent => "browser-agent",
        ContentReportSurface.HomeAssistant => "HA",
        _ => "chat"
    };

    public static string ToLabel(ContentReportSurface surface) => surface switch
    {
        ContentReportSurface.Chat => "Chat",
        ContentReportSurface.Image => "Image",
        ContentReportSurface.Speech => "Speech",
        ContentReportSurface.NotesAi => "Notes AI",
        ContentReportSurface.BrowserAgent => "Browser agent",
        ContentReportSurface.HomeAssistant => "Home Assistant",
        _ => "Chat"
    };
}

public sealed class ContentReport
{
    public required string WhatHappened { get; init; }
    public string? ExtraDetail { get; init; }
    public ContentReportSurface Surface { get; init; }
    public string? ModelId { get; init; }
    public string? AppVersion { get; init; }
}
