using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{

    /// <summary>
    /// Reads a file from the workspace and returns its content.
    /// The AI uses this to see your code before making changes.
    /// </summary>
    public class FileReaderTool :ITool
    {

        private readonly string _workspaceRoot;

        public string Name => "read_file";
        public bool IsReadOnly => true;

        public string Description => "Reads the content of a file. Use this to see code before modifying it.";
        public Dictionary<string, string> Parameters => new()
         {
          { "path", "Relative path to the file from workspace root" }
         };


        // Constructor - workspace root comes from configuration
        public FileReaderTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            // Get the file path from parameters
            if (!parameters.TryGetValue("path", out var relativePath))
            {
                return "ERROR: Missing 'path' parameter";
            }

            // Combine workspace root with relative path
            var fullPath = WorkspacePath.Resolve(_workspaceRoot, relativePath);

            // Security: Prevent reading files outside workspace
            if (fullPath == null)
            {
                return "ERROR: Access denied - file is outside workspace";
            }

            // Check if file exists
            if (!File.Exists(fullPath))
            {
                return $"ERROR: File not found at {relativePath}";
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

                var content = string.Join("\n", numberedLines);
                return $"File: {relativePath} ({totalLines} lines)\n\n{content}";
            }
            catch (Exception ex)
            {
                return $"ERROR reading file: {ex.Message}";
            }
        }



    }
}
