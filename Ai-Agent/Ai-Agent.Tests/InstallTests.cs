using Ai_Agent.Agent.Services;
using Ai_Agent.Data;
using Ai_Agent.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ai_Agent.Tests
{
    /// <summary>Zero-config install: memory on a SQLite file, with nothing to install or configure.</summary>
    public class InstallTests
    {
        private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
        {
            public AppDbContext CreateDbContext() =>
                new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        }

        [Fact]
        public void A_provider_key_alone_is_enough_configuration()
        {
            // The bundled backend ships no appsettings with provider URLs: defaults must be usable as they are
            var deepSeek = new Config.LLMOptions();
            Assert.True(Uri.TryCreate(deepSeek.BaseUrl, UriKind.Absolute, out _));
            Assert.False(string.IsNullOrWhiteSpace(deepSeek.Model));
            Assert.False(string.IsNullOrWhiteSpace(new Config.AnthropicOptions().Model));
        }

        [Fact]
        public async Task Memory_works_on_a_sqlite_file_created_from_scratch()
        {
            using var ws = new TempWorkspace();
            var dbPath = Path.Combine(ws.Root, "data", "agent.db");   // folder doesn't exist yet
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var factory = new Factory(dbPath);
            await using (var context = factory.CreateDbContext())
                Assert.True(await context.Database.EnsureCreatedAsync());

            var memory = new ConversationMemoryService(factory, NullLogger<ConversationMemoryService>.Instance);
            await memory.SaveAsync(new ConversationMemory { UserRequest = "add pagination", Summary = "done", Workspace = "A" });
            await memory.SaveAsync(new ConversationMemory { UserRequest = "other project", Summary = "x", Workspace = "B" });

            var recent = await memory.GetRecentAsync(5, "A");

            Assert.Equal("add pagination", Assert.Single(recent).UserRequest);   // only this workspace's memories
            Assert.True(File.Exists(dbPath));
        }
    }
}
