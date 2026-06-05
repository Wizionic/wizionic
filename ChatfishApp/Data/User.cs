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
    /// Per-user random key (base64) used by WASM clients for encrypting local history blobs
    /// (and live sync payloads) in browser storage. Protected at rest on the server via IDataProtector.
    /// Fetched by authenticated WASM instances so that multiple live devices for the same email
    /// can encrypt/decrypt data for live cross-device sync (server acts only as auth + signaling;
    /// history blobs are never stored in SQLite for the WASM path).
    /// </summary>
    public string? LocalEncryptionKey { get; set; }
}
