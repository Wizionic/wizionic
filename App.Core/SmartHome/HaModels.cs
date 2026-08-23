namespace App.Core.SmartHome;

public sealed record HaInstanceInfo(
    bool Ok,
    string? LocationName,
    string? Version,
    string? TimeZone,
    string? Error);

public sealed record HaAreaInfo(string Id, string Name, IReadOnlyList<string> EntityIds);

public sealed record HaDeviceRow(
    string EntityId,
    string FriendlyName,
    string Domain,
    string State,
    string? AreaName,
    bool CanToggle);
