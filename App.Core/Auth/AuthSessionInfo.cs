namespace App.Core.Auth;

public sealed class AuthSessionInfo
{
    public string Id { get; init; } = "";
    public string? DeviceName { get; init; }
    public string? DeviceId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastSeenAt { get; init; }
    public bool IsCurrent { get; init; }
    public string? UserAgent { get; init; }
}
