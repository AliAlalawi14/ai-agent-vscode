using Ai_Agent.Data;
using Ai_Agent.Models;
using Microsoft.EntityFrameworkCore;

namespace Ai_Agent.Agent.Services
{
    public class ConversationMemoryService
    {
        private readonly IDbContextFactory<AppDbContext> _contextFactory;
        private readonly ILogger<ConversationMemoryService> _logger;

        public ConversationMemoryService(
            IDbContextFactory<AppDbContext> contextFactory,
            ILogger<ConversationMemoryService> logger)
        {
            _contextFactory = contextFactory;
            _logger = logger;
        }

        public async Task SaveAsync(ConversationMemory memory)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            context.ConversationMemories.Add(memory);
            await context.SaveChangesAsync();
            _logger.LogInformation("Memory saved: {SessionId}", memory.SessionId);
        }

        public async Task<List<ConversationMemory>> GetRecentAsync(int count = 10, string? workspace = null)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var query = context.ConversationMemories.AsQueryable();
            if (!string.IsNullOrEmpty(workspace))
                query = query.Where(m => m.Workspace == workspace);

            return await query
                .OrderByDescending(m => m.CreatedAt)
                .Take(count)
                .ToListAsync();
        }

        public async Task<List<ConversationMemory>> SearchAsync(string query, int count = 5)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var terms = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var allMemories = await context.ConversationMemories
                .OrderByDescending(m => m.CreatedAt)
                .Take(100)
                .ToListAsync();

            // Simple relevance scoring
            return allMemories
                .Select(m => new { Memory = m, Score = ScoreMemory(m, terms) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(count)
                .Select(x => x.Memory)
                .ToList();
        }

        private int ScoreMemory(ConversationMemory memory, string[] terms)
        {
            var text = $"{memory.UserRequest} {memory.Summary} {memory.KeyTerms} {memory.FilesModified}".ToLower();
            return terms.Count(t => text.Contains(t));
        }

        public string FormatMemoriesForPrompt(List<ConversationMemory> memories)
        {
            if (memories.Count == 0) return string.Empty;

            var lines = new List<string>();
            lines.Add("## RELEVANT PAST CONVERSATIONS");

            foreach (var m in memories.Take(5))
            {
                var date = m.CreatedAt.ToString("MMM dd");
                var files = string.IsNullOrEmpty(m.FilesModified) ? "" : $" (changed: {m.FilesModified})";
                lines.Add($"- [{date}] Asked: \"{Shorten(m.UserRequest, 120)}\" → {Shorten(m.Summary, 200)}{files}");
            }

            lines.Add("Use this context to understand user preferences and project history.");
            lines.Add(string.Empty);

            return string.Join("\n", lines);
        }

        private static string Shorten(string text, int max)
        {
            var oneLine = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
        }
    }
}
