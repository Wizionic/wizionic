namespace ChatfishApp.Data;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? MagicLinkToken { get; set; }
    public DateTime? MagicLinkExpiresAt { get; set; }

    /// <summary>
    /// Per-user random AES-256 key (base64, 32 raw bytes) used by WASM/MAUI clients for IndexedDB
    /// encryption and live sync payloads. Stored as plaintext in SQLite; created once per user.
    /// Fetched over HTTPS+cookie auth so all devices for the same email share one stable key.
    /// </summary>
    public string? LocalEncryptionKey { get; set; }
}
