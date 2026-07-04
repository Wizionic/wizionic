namespace ChatfishApp.Core.Browser;

public record SidebarApp(
    string Id,
    string Name,
    string ShortName,
    string StartUrl,
    string? IconUrl,
    string? BackgroundColor,
    string? ThemeColor,
    bool IsBuiltIn,
    OpenTarget DefaultOpenTarget,
    DateTime PinnedAt,
    int SortOrder = 0,
    bool IsPwa = false
);