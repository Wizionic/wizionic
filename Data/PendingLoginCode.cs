namespace App.Data;

/// <summary>
/// Login code for an email that does not yet have a User row.
/// The account (and encryption key) is created only when the code is used.
/// </summary>
public class PendingLoginCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
