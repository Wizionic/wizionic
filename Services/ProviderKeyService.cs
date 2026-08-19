using App.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

/// <summary>
/// Manages per-user API keys stored on the host (unused by the current Cloud Providers page).
/// </summary>
public class ProviderKeyService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly KeyProtectionService _protector;

    public ProviderKeyService(AppDbContext db, IHttpContextAccessor http, KeyProtectionService protector)
    {
        _db = db;
        _http = http;
        _protector = protector;
    }

    private async Task<User> GetCurrentUserAsync()
    {
        var email = _http.HttpContext?.User?.Identity?.Name;
        if (string.IsNullOrEmpty(email))
            throw new InvalidOperationException("User not authenticated.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
            throw new InvalidOperationException("User not found.");

        return user;
    }

    public async Task<List<UserProviderKey>> GetUserKeysAsync()
    {
        var user = await GetCurrentUserAsync();
        return await _db.ProviderKeys
            .Where(k => k.UserId == user.Id)
            .OrderBy(k => k.ProviderId)
            .ToListAsync();
    }

    public async Task<UserProviderKey?> GetKeyAsync(string providerId)
    {
        var user = await GetCurrentUserAsync();
        return await _db.ProviderKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ProviderId == providerId);
    }

    public async Task SetKeyAsync(string providerId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider id is required.", nameof(providerId));

        var user = await GetCurrentUserAsync();

        var existing = await _db.ProviderKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ProviderId == providerId);

        if (existing == null)
        {
            existing = new UserProviderKey
            {
                UserId = user.Id,
                ProviderId = providerId,
                Key = _protector.Protect(apiKey),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.ProviderKeys.Add(existing);
        }
        else
        {
            existing.Key = _protector.Protect(apiKey);
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }

    public async Task DeleteKeyAsync(string providerId)
    {
        var user = await GetCurrentUserAsync();
        var existing = await _db.ProviderKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ProviderId == providerId);

        if (existing != null)
        {
            _db.ProviderKeys.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Returns true if the current user has a (non-empty) key configured for the provider.
    /// </summary>
    public async Task<bool> HasKeyAsync(string providerId)
    {
        var key = await GetKeyAsync(providerId);
        return key != null && !string.IsNullOrWhiteSpace(key.Key);
    }

    /// <summary>
    /// Returns true if the provider has a key AND is marked as Enabled for chat use.
    /// </summary>
    public async Task<bool> IsProviderEnabledForChatAsync(string providerId)
    {
        var key = await GetKeyAsync(providerId);
        return key != null && !string.IsNullOrWhiteSpace(key.Key) && key.Enabled;
    }

    /// <summary>
    /// Toggle the Enabled flag for a provider (requires existing key).
    /// </summary>
    public async Task ToggleEnabledAsync(string providerId, bool enabled)
    {
        var user = await GetCurrentUserAsync();
        var key = await _db.ProviderKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ProviderId == providerId);

        if (key != null)
        {
            key.Enabled = enabled;
            key.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }
}
