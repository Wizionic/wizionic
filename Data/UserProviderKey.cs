namespace App.Data;

public class UserProviderKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Provider identifier from ProviderCatalog (e.g. "groq", "gemini").
    /// </summary>
    public string ProviderId { get; set; } = "";

    /// <summary>API key for the provider. Older rows may still be raw; new writes go through KeyProtectionService.</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Whether this provider is enabled for use in the chat model selector.
    /// Allows users to temporarily disable providers without deleting the key.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
