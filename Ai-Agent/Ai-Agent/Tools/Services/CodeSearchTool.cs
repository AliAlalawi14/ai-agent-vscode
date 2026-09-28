using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Searches for text across files in the workspace.
    /// Like grep or Ctrl+Shift+F in VS Code.
    /// The AI uses this to find relevant code without reading every file.
    /// </summary>
    public class CodeSearchTool : ITool
    {
        private readonly string _workspaceRoot;

        public string Name => "search_code";
        public bool IsReadOnly => true;

        public string Description => "Searches for text across files. Returns matching files with line numbers. Use this to find where specific code lives.";

        public Dictionary<string, string> Parameters => new()
    {
        { "query", "The text to search for (case-insensitive)" },
        { "filePattern", "[Optional] File pattern like *.cs or *.json (default: *.cs)" }
    };

        // Folders to skip during search
        private static readonly string[] _skipFolders = { "bin", "obj", "node_modules", ".git", ".vs" };

        // Max files to search
        private const int MaxFiles = 100;

        // Max results to return
        private const int MaxResults = 20;

        public CodeSearchTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            // Validate
            if (!parameters.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
            {
                return Task.FromResult("ERROR: Missing 'query' parameter");
            }

            // Default file pattern
            var filePattern = "*.cs";
            if (parameters.TryGetValue("filePattern", out var pattern) && !string.IsNullOrWhiteSpace(pattern))
            {
                filePattern = pattern;
            }

            try
            {
                // Get all matching files (skip bin, obj, etc.)
                var allFiles = Directory.GetFiles(
                    _workspaceRoot,
                    filePattern,
                    SearchOption.AllDirectories
                );

                // Filter out skipped folders
                var files = allFiles
                    .Where(f => !ShouldSkipFolder(f))
                    .Take(MaxFiles)
                    .ToList();

                if (files.Count == 0)
                {
                    return Task.FromResult($"No files found matching pattern: {filePattern}");
                }

                // Search each file
                var results = new List<SearchResult>();
                var searchQuery = query.ToLower();

                foreach (var file in files)
                {
                    // Stop if we have enough results
                    if (results.Count >= MaxResults) break;

                    try
                    {
                        var lines = File.ReadAllLines(file);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (results.Count >= MaxResults) break;

                            if (lines[i].ToLower().Contains(searchQuery))
                            {
                                var relativePath = Path.GetRelativePath(_workspaceRoot, file);
                                results.Add(new SearchResult
                                {
                                    File = relativePath,
                                    LineNumber = i + 1,
                                    Content = lines[i].Trim()
                                });
                            }
                        }
                    }
                    catch
                    {
                        // Skip files we can't read (binary, locked, etc.)
                        continue;
                    }
                }

                // Format output
                if (results.Count == 0)
                {
                    return Task.FromResult($"No matches found for '{query}' in {files.Count} files");
                }

                var output = new System.Text.StringBuilder();
                output.AppendLine($"Found {results.Count} matches for '{query}' in {files.Count} files:");
                output.AppendLine();

                // Group by file
                var grouped = results.GroupBy(r => r.File);
                foreach (var group in grouped)
                {
                    output.AppendLine($"FILE: {group.Key}");
                    foreach (var match in group)
                    {
                        output.AppendLine($"  Line {match.LineNumber}: {match.Content}");
                    }
                    output.AppendLine();
                }

                if (results.Count >= MaxResults)
                {
                    output.AppendLine($"(Results limited to {MaxResults}. Narrow your search if needed.)");
                }

                return Task.FromResult(output.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"ERROR searching: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a file path contains a folder that should be skipped.
        /// </summary>
        private bool ShouldSkipFolder(string fullPath)
        {
            var relativePath = Path.GetRelativePath(_workspaceRoot, fullPath);
            var parts = relativePath.Split(Path.DirectorySeparatorChar);

            return parts.Any(part => _skipFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Holds a single search result.
        /// </summary>
        private class SearchResult
        {
            public string File { get; set; } = string.Empty;
            public int LineNumber { get; set; }
            public string Content { get; set; } = string.Empty;
        }
    }
}
