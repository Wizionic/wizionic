using Microsoft.EntityFrameworkCore;

namespace ChatfishApp.Data;

public class ChatfishDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
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

        modelBuilder.Entity<User>()
            .HasMany(u => u.Conversations)
            .WithOne(c => c.User)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Conversation>()
            .HasMany(c => c.Messages)
            .WithOne(m => m.Conversation)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

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
