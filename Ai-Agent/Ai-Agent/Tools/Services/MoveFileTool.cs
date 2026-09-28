using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Moves or renames one file inside the workspace. It never overwrites an existing file. The ChangeTracker records
    /// it as two changes (source deleted, destination created), so reverting the session restores the original.
    /// </summary>
    public class MoveFileTool : ITool
    {
        private readonly string _workspaceRoot;

        public string Name => "move_file";
        public string Description =>
            "Moves or renames ONE file inside the workspace (e.g. Models/Old.cs -> Models/New.cs). Does not change the " +
            "file's content: rename classes/usages with edit_file afterwards. Fails if the destination exists. " +
            "The user must approve it; it can be reverted.";

        public Dictionary<string, string> Parameters => new()
        {
            { "path", "Relative path of the file to move" },
            { "new_path", "Relative destination path (folders are created as needed)" }
        };

        public bool RequiresApproval => true;

        public MoveFileTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        /// <summary>Both paths a move touches, for change tracking (source, destination).</summary>
        public static IReadOnlyList<string> AffectedPaths(Dictionary<string, string> parameters) =>
            new[] { parameters.GetValueOrDefault("path"), parameters.GetValueOrDefault("new_path") }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();

        public async Task<ToolPreview> PreviewAsync(Dictionary<string, string> parameters)
        {
            var (error, from, to, fullFrom, _) = Validate(parameters);
            if (error != null) return ToolPreview.Fail(error);

            // Shown as the new file (content unchanged); the summary says where it comes from
            return new ToolPreview
            {
                FilePath = to,
                Before = string.Empty,
                After = await File.ReadAllTextAsync(fullFrom!),
                IsNewFile = true,
                Summary = $"Move {from} → {to}"
            };
        }

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            var (error, from, to, fullFrom, fullTo) = Validate(parameters);
            if (error != null) return Task.FromResult($"ERROR: {error}");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullTo!)!);
                File.Move(fullFrom!, fullTo!, overwrite: false);
                return Task.FromResult($"SUCCESS: Moved {from} to {to}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult($"ERROR moving {from}: {ex.Message}");
            }
        }

        private (string? Error, string? From, string? To, string? FullFrom, string? FullTo) Validate(Dictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue("path", out var from) || string.IsNullOrWhiteSpace(from))
                return ("Missing 'path' parameter", null, null, null, null);
            if (!parameters.TryGetValue("new_path", out var to) || string.IsNullOrWhiteSpace(to))
                return ("Missing 'new_path' parameter", null, null, null, null);

            var fullFrom = WorkspacePath.Resolve(_workspaceRoot, from);
            var fullTo = WorkspacePath.Resolve(_workspaceRoot, to);
            if (fullFrom == null || fullTo == null)
                return ("Access denied - both paths must be inside the workspace", null, null, null, null);
            if (!File.Exists(fullFrom))
                return ($"File not found: {from}", null, null, null, null);
            if (File.Exists(fullTo) || Directory.Exists(fullTo))
                return ($"{to} already exists; move_file never overwrites.", null, null, null, null);
            if (Agent.Services.SecretRedactor.IsSecretFile(from) || WorkspacePath.IsInternalFile(Path.GetFileName(fullFrom)))
                return ($"{from} can't be moved by the agent.", null, null, null, null);

            return (null, from, to, fullFrom, fullTo);
        }
    }
}
