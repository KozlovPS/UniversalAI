using Microsoft.EntityFrameworkCore;
using UniversalAI.Data.Entities;

namespace UniversalAI.Data;

public class AppDbContext : DbContext
{
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();

    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<ProviderConnectionEntity> ProviderConnections =>
    Set<ProviderConnectionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ConversationEntity>()
            .HasMany(c => c.Messages)
            .WithOne(m => m.Conversation)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ChatMessageEntity>()
            .HasIndex(m => new { m.ConversationId, m.CreatedAt });

        modelBuilder.Entity<ConversationEntity>()
            .HasOne(c => c.ProviderConnection)
            .WithMany()
            .HasForeignKey(c => c.ProviderConnectionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}