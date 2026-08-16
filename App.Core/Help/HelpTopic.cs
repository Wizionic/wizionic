namespace App.Core.Help;

public sealed class HelpTopic
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string File { get; init; } = "";
    public string Audience { get; init; } = "howto";
    public string? Anchor { get; init; }
    public bool DesktopOnly { get; init; }
    public bool ShowInToc { get; init; } = true;
    public IReadOnlyList<string> Routes { get; init; } = Array.Empty<string>();
}
