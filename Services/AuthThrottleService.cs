using System.Collections.Concurrent;
using App.Core.Auth;
using App.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Services;

/// <summary>
/// Per-email login-code send limits and temporary lockout after failed password/code attempts.
/// In-memory counters plus User.LockoutUntil so a restart does not instantly clear a lock.
/// </summary>
public sealed class AuthThrottleService
{
    public const int MaxCodesPerEmailPerWindow = 5;
    public const int MaxFailuresBeforeLockout = 8;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public const string GenericAuthFail = "Invalid email or password.";
    public const string GenericCodeFail = "Invalid or expired login code.";
    public const string LockedMessage = "Too many attempts. Try again in a few minutes.";
    public const string CodeRateMessage = "Please wait before requesting another login code.";

    private readonly ConcurrentDictionary<string, List<DateTime>> _codeSends = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<DateTime>> _failures = new(StringComparer.Ordinal);

    public bool IsCodeSendAllowed(string email)
    {
        var key = EmailNormalizer.Normalize(email);
        if (string.IsNullOrEmpty(key))
            return false;

        var now = DateTime.UtcNow;
        var list = _codeSends.GetOrAdd(key, _ => new List<DateTime>());
        lock (list)
        {
            list.RemoveAll(t => now - t > Window);
            if (list.Count >= MaxCodesPerEmailPerWindow)
                return false;
            list.Add(now);
            return true;
        }
    }

    public async Task<(bool Locked, string? Message)> CheckLockoutAsync(AppDbContext db, string email, CancellationToken ct = default)
    {
        var key = EmailNormalizer.Normalize(email);
        if (IsMemoryLocked(key))
            return (true, LockedMessage);

        var user = await FindUserAsync(db, key, ct);
        if (user?.LockoutUntil is { } until && until > DateTime.UtcNow)
            return (true, LockedMessage);

        return (false, null);
    }

    public async Task RegisterFailureAsync(AppDbContext db, string email, CancellationToken ct = default)
    {
        var key = EmailNormalizer.Normalize(email);
        var now = DateTime.UtcNow;
        var list = _failures.GetOrAdd(key, _ => new List<DateTime>());
        int count;
        lock (list)
        {
            list.RemoveAll(t => now - t > Window);
            list.Add(now);
            count = list.Count;
        }

        var user = await FindUserAsync(db, key, ct);
        if (user != null)
        {
            user.FailedAuthCount = count;
            if (count >= MaxFailuresBeforeLockout)
                user.LockoutUntil = now.Add(LockoutDuration);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RegisterSuccessAsync(AppDbContext db, User user, CancellationToken ct = default)
    {
        var key = EmailNormalizer.Normalize(user.Email);
        _failures.TryRemove(key, out _);
        user.FailedAuthCount = 0;
        user.LockoutUntil = null;
        await db.SaveChangesAsync(ct);
    }

    private bool IsMemoryLocked(string key)
    {
        if (!_failures.TryGetValue(key, out var list))
            return false;
        lock (list)
        {
            var now = DateTime.UtcNow;
            list.RemoveAll(t => now - t > Window);
            return list.Count >= MaxFailuresBeforeLockout;
        }
    }

    public static Task<User?> FindUserAsync(AppDbContext db, string email, CancellationToken ct = default)
    {
        var key = EmailNormalizer.Normalize(email);
        return db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == key, ct);
    }
}
