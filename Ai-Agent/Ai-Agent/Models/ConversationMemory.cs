namespace Ai_Agent.Models
{
    // Models/ConversationMemory.cs
    public class ConversationMemory
    {
        public int Id { get; set; }
        public string SessionId { get; set; } = Guid.CreateVersion7().ToString();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string Title { get; set; } = string.Empty;
        public string UserRequest { get; set; } = string.Empty;  // Original task
        public string Summary { get; set; } = string.Empty;      // What was done
        public string KeyTerms { get; set; } = string.Empty;     // Comma-separated
        public string FilesModified { get; set; } = string.Empty;// Comma-separated
        public bool BuildSucceeded { get; set; }
        public int IterationsUsed { get; set; }
        public string Workspace { get; set; } = string.Empty;   // memories are only recalled in the same workspace
    }
}
