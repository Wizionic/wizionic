using Microsoft.EntityFrameworkCore;

namespace ChatfishApp.Data;

public class ChatfishDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserProviderKey> ProviderKeys => Set<UserProviderKey>();

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
