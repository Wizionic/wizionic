using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace App.Data;

/// <summary>
/// DbContext that also serves as the storage for ASP.NET Core DataProtection keys.
/// This lets us keep the (small) DP key material in the same SQLite database as Users etc.
/// No separate volume or filesystem persistence worries for the hosting provider.
/// </summary>
public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserProviderKey> ProviderKeys => Set<UserProviderKey>();
    public DbSet<OAuthProvider> OAuthProviders => Set<OAuthProvider>();
    public DbSet<Connector> Connectors => Set<Connector>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<PendingLoginCode> PendingLoginCodes => Set<PendingLoginCode>();

    /// <summary>
    /// ASP.NET DataProtection stores its key rings (XML) here. The table is created by a migration.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
        //Database.EnsureCreated();
        Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasIndex(u => u.TwoFactorChallengeHash);

        modelBuilder.Entity<AuthSession>()
            .HasIndex(s => s.SessionHash)
            .IsUnique();

        modelBuilder.Entity<AuthSession>()
            .HasIndex(s => new { s.UserId, s.RevokedAt });

        modelBuilder.Entity<AuthSession>()
            .HasOne(s => s.User)
            .WithMany(u => u.Sessions)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserDevice>()
            .HasIndex(d => new { d.UserId, d.DeviceId })
            .IsUnique();

        modelBuilder.Entity<UserDevice>()
            .HasOne(d => d.User)
            .WithMany(u => u.Devices)
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PendingLoginCode>()
            .HasIndex(p => p.Email)
            .IsUnique();

        modelBuilder.Entity<UserProviderKey>()
            .HasOne(k => k.User)
            .WithMany()
            .HasForeignKey(k => k.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserProviderKey>()
            .HasIndex(k => new { k.UserId, k.ProviderId })
            .IsUnique();

        modelBuilder.Entity<OAuthProvider>()
            .HasIndex(p => p.ProviderId)
            .IsUnique();

        modelBuilder.Entity<Connector>()
            .HasIndex(c => c.ConnectorId)
            .IsUnique();

        modelBuilder.Entity<Connector>()
            .HasIndex(c => new { c.Featured, c.SortOrder });
    }
}
