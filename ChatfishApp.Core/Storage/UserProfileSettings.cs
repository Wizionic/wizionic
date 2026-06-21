namespace ChatfishApp.Core.Storage;

public sealed class UserProfileSettings
{
    public bool CustomizationEnabled { get; set; }
    public string PreferredName { get; set; } = "";
    public string Occupation { get; set; } = "";

    public UserProfileSettings Clone() => new()
    {
        CustomizationEnabled = CustomizationEnabled,
        PreferredName = PreferredName,
        Occupation = Occupation
    };
}

public sealed record UserMemory(string Id, string Text, DateTime CreatedAtUtc)
{
    public UserMemory WithText(string text) => this with { Text = text.Trim() };
}