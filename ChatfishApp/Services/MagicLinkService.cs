using ChatfishApp.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace ChatfishApp.Services;

public class MagicLinkService
{
    private readonly ChatfishDbContext _db;

    public MagicLinkService(ChatfishDbContext db)
    {
        _db = db;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes);
    }

    public async Task<string> CreateMagicLinkTokenAsync(string email)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user == null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                CreatedAt = DateTime.UtcNow
            };

            _db.Users.Add(user);
        }

        user.MagicLinkToken = GenerateToken();
        user.MagicLinkExpiresAt = DateTime.UtcNow.AddMinutes(15);

        await _db.SaveChangesAsync();

        return user.MagicLinkToken;
    }

    public async Task<User?> ValidateMagicLinkAsync(string token)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.MagicLinkToken == token);

        if (user == null)
            return null;

        if (user.MagicLinkExpiresAt == null || user.MagicLinkExpiresAt < DateTime.UtcNow)
            return null;

        user.MagicLinkToken = null;
        user.MagicLinkExpiresAt = null;

        await _db.SaveChangesAsync();

        return user;
    }

}
