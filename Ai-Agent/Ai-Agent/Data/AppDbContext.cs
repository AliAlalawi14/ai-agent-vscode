using Ai_Agent.Models;
using Microsoft.EntityFrameworkCore;

namespace Ai_Agent.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<ConversationMemory> ConversationMemories { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConversationMemory>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.SessionId);
                entity.HasIndex(e => e.CreatedAt);
                entity.Property(e => e.UserRequest).HasMaxLength(2000);
                entity.Property(e => e.Summary).HasMaxLength(2000);
                entity.Property(e => e.KeyTerms).HasMaxLength(500);
                entity.Property(e => e.FilesModified).HasMaxLength(1000);
            });
        }
    }
}