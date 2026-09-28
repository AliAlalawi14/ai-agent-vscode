namespace Ai_Agent.Tools.Interfaces
{
    /// <summary>
    /// Every tool in the system must implement this interface.
    /// This keeps all tools consistent and interchangeable.
    /// </summary>
    public interface ITool
    {
        // The name the AI will use to call this tool (e.g., "read_file")
        string Name { get; }

        // Description so the AI knows when to use it
        string Description { get; }

        // What parameters does this tool need? (name → description; "[Optional]" prefix = not required)
        // Example: read_file needs a "path"
        Dictionary<string, string> Parameters { get; }

        // JSON-schema overrides per parameter (type integer/boolean/enum/array…); others are strings.
        // Typed schemas let the model send 5 instead of "5" and pick valid enum values.
        IReadOnlyDictionary<string, object> ParameterSchemas => NoSchemas;

        // Actually runs the tool with the given parameters
        Task<string> ExecuteAsync(Dictionary<string, string> parameters);

        // Cancellation-aware variant (the user pressed Stop / closed the chat). Long-running tools override it.
        Task<string> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken) =>
            ExecuteAsync(parameters);

        // Read-only tools (read/search/list) are the only ones offered in Ask and Plan mode
        bool IsReadOnly => false;

        // Side-effecting tools (writes, commands) wait for the user's Accept before ExecuteAsync runs
        bool RequiresApproval => false;

        // What ExecuteAsync WOULD do, computed without side effects, shown to the user for approval
        Task<ToolPreview> PreviewAsync(Dictionary<string, string> parameters) =>
            Task.FromResult(new ToolPreview());

        static readonly IReadOnlyDictionary<string, object> NoSchemas = new Dictionary<string, object>();
    }

    /// <summary>Small helpers to declare typed parameter schemas.</summary>
    public static class ParamSchema
    {
        public static object Integer(int? minimum = null) => minimum.HasValue
            ? new Dictionary<string, object> { ["type"] = "integer", ["minimum"] = minimum.Value }
            : new Dictionary<string, object> { ["type"] = "integer" };

        public static object Boolean() => new Dictionary<string, object> { ["type"] = "boolean" };

        public static object Enum(params string[] values) =>
            new Dictionary<string, object> { ["type"] = "string", ["enum"] = values };

        public static object ArrayOf(object items) =>
            new Dictionary<string, object> { ["type"] = "array", ["items"] = items };
    }

    /// <summary>
    /// A dry run of a tool call: the file before/after, or the command line.
    /// Error is set when the call would fail anyway, so the user isn't asked to approve it.
    /// </summary>
    public class ToolPreview
    {
        public string? FilePath { get; set; }     // relative to the workspace
        public string? Before { get; set; }
        public string? After { get; set; }
        public bool IsNewFile { get; set; }
        public string? Command { get; set; }
        public string? Error { get; set; }
        public string? Summary { get; set; }      // approval card title, when "Edit X" / "Create X" doesn't fit (delete, move)

        public static ToolPreview Fail(string error) => new() { Error = error };
    }
}
