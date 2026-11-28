namespace App.Core.SmartHome;

/// <summary>
/// Shared classification of Home Assistant service/tool result strings.
/// Keeps "is this an error?" consistent between the HA service, the tool module,
/// and the chat trace.
/// </summary>
public static class HomeAssistantResult
{
    public static bool IsFailure(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return true;

        var text = result.TrimStart();

        return text.StartsWith("HA error", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Connection", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Home Assistant is not configured", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Home Assistant returned", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Enter both", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Domain and service", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Could not parse", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Smart home integration", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Invalid service_data", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Entity ID is required", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("State must be", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Action must be", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("volume_percent", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("source is required", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("temperature is required", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("hvac_mode is required", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("position (0-100)", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Assist could not handle", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Forbidden", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Not Found", StringComparison.OrdinalIgnoreCase);
    }
}
