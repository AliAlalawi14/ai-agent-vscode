using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{
    public class FileWriterTool : ITool
    {
        private readonly string _workspaceRoot;

        public string Name => "write_file";
        public string Description => "Writes content to a file. Creates the file if it doesn't exist, overwrites if it does. The user must approve the change first.";

        public Dictionary<string, string> Parameters => new()
        {
            { "path", "Relative path to the file from workspace root" },
            { "content", "The complete new content for the file" }
        };

        public bool RequiresApproval => true;

        public FileWriterTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public async Task<ToolPreview> PreviewAsync(Dictionary<string, string> parameters)
        {
            var (error, relativePath, fullPath, content) = Validate(parameters);
            if (error != null) return ToolPreview.Fail(error);

            var isNewFile = !File.Exists(fullPath);
            return new ToolPreview
            {
                FilePath = relativePath,
                Before = isNewFile ? string.Empty : await File.ReadAllTextAsync(fullPath!),
                After = content,
                IsNewFile = isNewFile
            };
        }

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            var (error, relativePath, fullPath, content) = Validate(parameters);
            if (error != null) return $"ERROR: {error}";

            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var isNewFile = !File.Exists(fullPath);
                // No .backup files: the ChangeTracker keeps the before-content and patch for revert
                await File.WriteAllTextAsync(fullPath!, content);

                return isNewFile
                    ? $"SUCCESS: Created new file at {relativePath}"
                    : $"SUCCESS: Updated file at {relativePath}";
            }
            catch (Exception ex)
            {
                return $"ERROR writing file: {ex.Message}";
            }
        }

        private (string? Error, string? RelativePath, string? FullPath, string? Content) Validate(Dictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue("path", out var relativePath))
                return ("Missing 'path' parameter", null, null, null);

            if (!parameters.TryGetValue("content", out var content))
                return ("Missing 'content' parameter", null, null, null);

            var fullPath = WorkspacePath.Resolve(_workspaceRoot, relativePath);
            if (fullPath == null)
                return ("Access denied - cannot write outside workspace", null, null, null);

            if (string.IsNullOrWhiteSpace(content))
                return ("Content is empty. Must provide complete file content.", null, null, null);

            if (content.Trim().Length < 10)
                return ("Content too short. Must provide the COMPLETE file.", null, null, null);

            if (Agent.Services.SecretRedactor.ContainsMarker(content))
                return ($"The content contains '{Agent.Services.SecretRedactor.Marker}' (a hidden secret). Writing it would destroy the real value; " +
                        "use edit_file on the lines you need to change instead.", null, null, null);

            return (null, relativePath, fullPath, content);
        }
    }
}
