using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Lists files and folders in a directory within the workspace.
    /// The AI uses this to explore project structure.
    /// </summary>
    public class DirectoryBrowserTool : ITool
    {
        private readonly string _workspaceRoot;

        public string Name => "list_directory";
        public bool IsReadOnly => true;

        public string Description => "Lists all files and folders in a directory. Use this to explore the project structure.";

        public Dictionary<string, string> Parameters => new()
    {
        { "path", "Relative path to the folder (leave empty for workspace root)" }
    };

        public DirectoryBrowserTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            // Get path from parameters, or use workspace root
            var relativePath = string.Empty;
            if (parameters.TryGetValue("path", out var pathParam) && !string.IsNullOrWhiteSpace(pathParam))
            {
                relativePath = pathParam.Trim('/').Trim('\\');
            }

            // Build full path
            var fullPath = WorkspacePath.Resolve(_workspaceRoot, relativePath);

            // Security check
            if (fullPath == null)
            {
                return Task.FromResult("ERROR: Access denied - path is outside workspace");
            }

            // Check if directory exists
            if (!Directory.Exists(fullPath))
            {
                return Task.FromResult($"ERROR: Directory not found: {relativePath}");
            }

            try
            {
                var result = new System.Text.StringBuilder();

                // Show current path
                var displayPath = string.IsNullOrEmpty(relativePath) ? "root" : relativePath;
                result.AppendLine($"Directory: {displayPath}");
                result.AppendLine();

                // Get directories
                var directories = Directory.GetDirectories(fullPath);
                if (directories.Length > 0)
                {
                    result.AppendLine("Folders:");
                    foreach (var dir in directories)
                    {
                        var dirName = Path.GetFileName(dir);
                        result.AppendLine($"  [DIR]  {dirName}/");
                    }
                    result.AppendLine();
                }

                // Get files
                var files = Directory.GetFiles(fullPath)
                    .Where(f => !WorkspacePath.IsInternalFile(Path.GetFileName(f)))
                    .ToArray();
                if (files.Length > 0)
                {
                    result.AppendLine("Files:");
                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileName(file);
                        var fileInfo = new FileInfo(file);
                        var size = FormatSize(fileInfo.Length);
                        result.AppendLine($"  [FILE] {fileName} ({size})");
                    }
                }

                if (directories.Length == 0 && files.Length == 0)
                {
                    result.AppendLine("(empty directory)");
                }

                return Task.FromResult(result.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"ERROR listing directory: {ex.Message}");
            }
        }

        /// <summary>
        /// Formats file size in human-readable format.
        /// </summary>
        private string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }

}
