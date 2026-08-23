namespace App.Core.Storage;

public sealed class HomeAssistantConfig
{
    public string BaseUrl { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>Legacy copy. Source of truth is <see cref="UserProfileSettings.AssistantName"/>.</summary>
    public string AssistantName { get; set; } = "";
    public string CachedDeviceSummary { get; set; } = "";
    public DateTime? DeviceSummaryUpdatedAt { get; set; }
}