namespace Ai_Agent.Models
{
    /// <summary>A previous chat turn sent by the client so follow-up questions have a referent.</summary>
    public class HistoryMessage
    {
        public string Role { get; set; } = string.Empty;     // "user" | "assistant"
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// What the user is looking at in the editor: an active/@-mentioned file, a symbol, or a selection.
    /// </summary>
    public class EditorContextItem
    {
        public string Type { get; set; } = "file";           // "file" | "symbol" | "selection"
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty; // relative to the workspace
        public bool Active { get; set; }                     // the file focused in the editor
        public bool AutoContext { get; set; }                // added automatically (recent file), not by the user
        public string? Code { get; set; }                    // selection text
        public int? StartLine { get; set; }
        public int? EndLine { get; set; }
        public string? ClassName { get; set; }
    }
}
