using App.Contracts;
using App.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

/// <summary>
/// Manages per-user API keys for providers defined in ProviderCatalog.
/// Keys are currently stored plaintext (see entity for TODO on encryption).
/// </summary>
public class ProviderKeyService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _http;

    public ProviderKeyService(AppDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
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
        if (ProviderCatalog.GetProvider(providerId) == null)
            throw new ArgumentException($"Unknown provider '{providerId}'.");

        var user = await GetCurrentUserAsync();

        var existing = await _db.ProviderKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ProviderId == providerId);

        if (existing == null)
        {
            existing = new UserProviderKey
            {
                UserId = user.Id,
                ProviderId = providerId,
                Key = apiKey,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.ProviderKeys.Add(existing);
        }
        else
        {
            existing.Key = apiKey;
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
