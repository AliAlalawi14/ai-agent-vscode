namespace Ai_Agent.Models
{
    /// <summary>
    /// Represents a single file change made by the agent.
    /// Supports unified diff patches, session grouping,
    /// and multi-level revert.
    /// </summary>
    public class PendingChange
    {
        /// <summary>Unique change identifier</summary>
        public string ChangeId { get; set; } = Guid.CreateVersion7().ToString();

        /// <summary>Session this change belongs to (groups changes from one agent run)</summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>Order within the session (1-based)</summary>
        public int SequenceNumber { get; set; }

        /// <summary>Relative file path from workspace root</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Tool that made the change (write_file, replace_lines)</summary>
        public string ToolUsed { get; set; } = string.Empty;

        /// <summary>
        /// Standard unified diff patch.
        /// This is the primary change representation — compact, standard, reversible.
        /// </summary>
        public string Patch { get; set; } = string.Empty;

        /// <summary>Full file content before the change (for absolute revert)</summary>
        public string BeforeContent { get; set; } = string.Empty;

        /// <summary>
        /// SHA-256 of the file content right after the change. At revert time, a matching hash means the
        /// file is untouched since, so BeforeContent can be restored exactly. Empty for older records.
        /// </summary>
        public string AfterHash { get; set; } = string.Empty;

        /// <summary>Whether this was creating a new file</summary>
        public bool IsNewFile { get; set; }

        /// <summary>The change removed the file (delete_file, or the source of move_file); revert recreates it</summary>
        public bool IsDeletion { get; set; }

        /// <summary>
        /// Change status:
        /// "applied" — change is active
        /// "reverted" — change has been undone
        /// </summary>
        public string Status { get; set; } = "applied";

        /// <summary>UTC timestamp of the change</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>Summary of what changed (for UI display)</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>Normalized root of the workspace the change was made in (empty for older records)</summary>
        public string Workspace { get; set; } = string.Empty;
    }
}
