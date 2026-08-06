namespace App.Core.Storage;

public sealed class HomeAssistantConfig
{
    public string BaseUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string AssistantName { get; set; } = "Home";
    public string CachedDeviceSummary { get; set; } = "";
    public DateTime? DeviceSummaryUpdatedAt { get; set; }
}