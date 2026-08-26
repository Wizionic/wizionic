using App.Core.Auth;
using App.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

public static class UserLookup
{
    public static Task<User?> ByEmailAsync(AppDbContext db, string? email, CancellationToken ct = default)
    {
        var key = EmailNormalizer.Normalize(email);
        if (string.IsNullOrEmpty(key))
            return Task.FromResult<User?>(null);
        return db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == key, ct);
    }
}
