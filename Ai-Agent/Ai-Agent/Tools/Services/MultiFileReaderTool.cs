using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{

    /// <summary>
    /// Reads multiple files at once and returns their combined content.
    /// The AI uses this to read several related files in one iteration
    /// instead of calling read_file multiple times.
    /// </summary>
    public class MultiFileReaderTool : ITool
    {
        private readonly string _workspaceRoot;

        public string Name => "read_files";
        public bool IsReadOnly => true;

        public string Description => "Reads multiple files at once. Use this when you need to see several related files together. Max 5 files per call.";

        public Dictionary<string, string> Parameters => new()
    {
        { "paths", "Comma-separated list of file paths (e.g., 'Calculator.cs, test.cs, Program.cs')" }
    };

        private const int MaxFiles = 5;
        private const int MaxContentLength = 8000;

        public MultiFileReaderTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue("paths", out var pathsStr) || string.IsNullOrWhiteSpace(pathsStr))
            {
                return "ERROR: Missing 'paths' parameter. Provide comma-separated file paths.";
            }

            // Split paths by comma
            var paths = pathsStr
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            if (paths.Count == 0)
            {
                return "ERROR: No valid file paths provided.";
            }

            if (paths.Count > MaxFiles)
            {
                return $"ERROR: Too many files ({paths.Count}). Maximum is {MaxFiles}. Read the most important ones first.";
            }

            var result = new System.Text.StringBuilder();
            var filesRead = 0;
            var filesFailed = 0;

            foreach (var relativePath in paths)
            {
                var fullPath = WorkspacePath.Resolve(_workspaceRoot, relativePath);

                // Security check
                if (fullPath == null)
                {
                    result.AppendLine($"=== FILE: {relativePath} === (SKIPPED: Outside workspace)");
                    filesFailed++;
                    continue;
                }

                // Check if file exists - USE fullPath NOT relativePath
                if (!File.Exists(fullPath))
                {
                    result.AppendLine($"=== FILE: {relativePath} === (NOT FOUND)");
                    filesFailed++;
                    continue;
                }

                try
                {
                    var lines = await File.ReadAllLinesAsync(fullPath);
                    var totalLines = lines.Length;

                    var numberedLines = new List<string>();
                    for (int i = 0; i < lines.Length; i++)
                    {
                        numberedLines.Add($"{(i + 1).ToString().PadLeft(6)}: {lines[i]}");
                    }

                    result.AppendLine($"=== FILE: {relativePath} ({totalLines} lines) ===");
                    result.AppendLine(string.Join("\n", numberedLines));
                    result.AppendLine();

                    filesRead++;
                }
                catch (Exception ex)
                {
                    result.AppendLine($"=== FILE: {relativePath} === (ERROR: {ex.Message})");
                    filesFailed++;
                }
            }
            // Add summary line
            result.AppendLine($"---");
            result.AppendLine($"Read {filesRead} file(s), {filesFailed} failed.");

            var finalResult = result.ToString().TrimEnd();

            // Truncate if too long
            if (finalResult.Length > MaxContentLength)
            {
                finalResult = finalResult[..MaxContentLength] +
                             $"\n\n(Content truncated at {MaxContentLength} chars. Use read_file for specific files if needed.)";
            }

            return finalResult;
        }
    }
}
