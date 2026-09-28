using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Deletes one file in the workspace. The user approves it first (the card shows the whole content going away),
    /// and the ChangeTracker keeps the content, so Revert brings the file back.
    /// </summary>
    public class DeleteFileTool : ITool
    {
        private readonly string _workspaceRoot;

        public string Name => "delete_file";
        public string Description =>
            "Deletes ONE file in the workspace (not folders). Use it for files that are really obsolete, e.g. after moving " +
            "their code elsewhere. The user must approve it; it can be reverted.";

        public Dictionary<string, string> Parameters => new()
        {
            { "path", "Relative path of the file to delete" }
        };

        public bool RequiresApproval => true;

        public DeleteFileTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public async Task<ToolPreview> PreviewAsync(Dictionary<string, string> parameters)
        {
            var (error, relativePath, fullPath) = Validate(parameters);
            if (error != null) return ToolPreview.Fail(error);

            return new ToolPreview
            {
                FilePath = relativePath,
                Before = await File.ReadAllTextAsync(fullPath!),
                After = string.Empty,
                Summary = $"Delete {relativePath}"
            };
        }

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            var (error, relativePath, fullPath) = Validate(parameters);
            if (error != null) return Task.FromResult($"ERROR: {error}");

            try
            {
                File.Delete(fullPath!);
                return Task.FromResult($"SUCCESS: Deleted {relativePath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult($"ERROR deleting {relativePath}: {ex.Message}");
            }
        }

        private (string? Error, string? RelativePath, string? FullPath) Validate(Dictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue("path", out var relativePath) || string.IsNullOrWhiteSpace(relativePath))
                return ("Missing 'path' parameter", null, null);

            var fullPath = WorkspacePath.Resolve(_workspaceRoot, relativePath);
            if (fullPath == null)
                return ("Access denied - file is outside the workspace", null, null);
            if (Directory.Exists(fullPath))
                return ($"{relativePath} is a folder; delete_file only deletes single files.", null, null);
            if (!File.Exists(fullPath))
                return ($"File not found: {relativePath}", null, null);
            if (WorkspacePath.IsInternalFile(Path.GetFileName(fullPath)) || Agent.Services.SecretRedactor.IsSecretFile(relativePath))
                return ($"{relativePath} can't be deleted by the agent.", null, null);

            return (null, relativePath, fullPath);
        }
    }
}
