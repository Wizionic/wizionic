using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChatfishApp.Data;

/// <summary>
/// DbContext that also serves as the storage for ASP.NET Core DataProtection keys.
/// This lets us keep the (small) DP key material in the same SQLite database as Users etc.
/// No separate volume or filesystem persistence worries for the hosting provider.
/// </summary>
public class ChatfishDbContext : DbContext, IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserProviderKey> ProviderKeys => Set<UserProviderKey>();

    /// <summary>
    /// ASP.NET DataProtection stores its key rings (XML) here. The table is created by a migration.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    public ChatfishDbContext(DbContextOptions<ChatfishDbContext> options)
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
    }
}
