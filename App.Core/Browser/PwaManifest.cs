namespace App.Core.Browser;

public record PwaIcon(
    string Src,
    string? Sizes,
    string? Type
);

public record PwaManifest(
    string? Name,
    string? ShortName,
    string? StartUrl,
    string? Display,
    string? Description,
    List<PwaIcon> Icons,
    List<string> Categories,
    string? BackgroundColor,
    string? ThemeColor,
    string SourceUrl
);