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
