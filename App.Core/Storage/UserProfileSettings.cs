namespace App.Core.Storage;

public sealed class UserProfileSettings
{
    public bool CustomizationEnabled { get; set; }
    public string PreferredName { get; set; } = "";
    public string Occupation { get; set; } = "";
    /// <summary>Wake word / assistant name for voice mode and Home Assistant chat routing.</summary>
    public string AssistantName { get; set; } = "";

    /// <summary>
    /// When true, after a wake-word command Voice mode accepts follow-ups without the wake word for ~30s.
    /// Default false: every command needs the wake word (avoids transcribing music / background noise).
    /// </summary>
    public bool VoiceFollowUpWithoutWake { get; set; }

    public UserProfileSettings Clone() => new()
    {
        CustomizationEnabled = CustomizationEnabled,
        PreferredName = PreferredName,
        Occupation = Occupation,
        AssistantName = AssistantName,
        VoiceFollowUpWithoutWake = VoiceFollowUpWithoutWake
    };
}

public sealed record UserMemory(string Id, string Text, DateTime CreatedAtUtc)
{
    public UserMemory WithText(string text) => this with { Text = text.Trim() };
}