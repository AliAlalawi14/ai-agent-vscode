using Ai_Agent.Tools.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Finds files by glob pattern inside the workspace (e.g. "**/*.csproj", "Controllers/*.cs", "Store.cs").
    /// Replaces the model improvising with `dir /s /b` through the terminal.
    /// </summary>
    public class FindFilesTool : ITool
    {
        private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", ".vscode", "node_modules", ".idea"
        };
        private const int MaxResults = 200;

        private readonly string _workspaceRoot;

        public string Name => "find_files";
        public bool IsReadOnly => true;
        public string Description =>
            "Finds files in the workspace by glob pattern and returns their relative paths. " +
            "Examples: '**/*.csproj', 'Controllers/*.cs', '**/Store.cs', '*.json'. A pattern without '/' matches file names in any folder. " +
            "Skips bin, obj, .git and node_modules.";

        public Dictionary<string, string> Parameters => new()
        {
            { "pattern", "Glob pattern: * matches within a folder name, ** matches any folders" }
        };

        public FindFilesTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            var pattern = parameters.GetValueOrDefault("pattern")?.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(pattern))
                return Task.FromResult("ERROR: Missing 'pattern' parameter");
            if (pattern.StartsWith("/") || pattern.Contains(".."))
                return Task.FromResult("ERROR: The pattern must be relative to the workspace (no leading '/' or '..').");

            // "Store.cs" or "*.json" (no folder) = match that name in any folder
            if (!pattern.Contains('/'))
                pattern = "**/" + pattern;

            var regex = GlobToRegex(pattern);
            var root = Path.GetFullPath(_workspaceRoot);
            var matches = new List<string>();
            var truncated = false;

            foreach (var file in EnumerateFiles(root))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!regex.IsMatch(relative)) continue;
                if (matches.Count >= MaxResults) { truncated = true; break; }
                matches.Add(relative);
            }

            if (matches.Count == 0)
                return Task.FromResult($"No files match '{pattern}'.");

            matches.Sort(StringComparer.OrdinalIgnoreCase);
            var result = $"{matches.Count} file{(matches.Count == 1 ? "" : "s")} match '{pattern}':\n" + string.Join("\n", matches);
            if (truncated) result += $"\n(Stopped at {MaxResults} results; use a narrower pattern.)";
            return Task.FromResult(result);
        }

        private static IEnumerable<string> EnumerateFiles(string directory)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory); }
            catch (UnauthorizedAccessException) { yield break; }
            foreach (var file in files)
            {
                if (!WorkspacePath.IsInternalFile(Path.GetFileName(file))) yield return file;
            }

            IEnumerable<string> subdirectories;
            try { subdirectories = Directory.EnumerateDirectories(directory); }
            catch (UnauthorizedAccessException) { yield break; }
            foreach (var sub in subdirectories)
            {
                if (SkippedDirectories.Contains(Path.GetFileName(sub))) continue;
                foreach (var file in EnumerateFiles(sub)) yield return file;
            }
        }

        /// <summary>"**/" = any folders (or none), "**" = anything, "*" = within one name, "?" = one char.</summary>
        private static Regex GlobToRegex(string glob)
        {
            var sb = new StringBuilder("^");
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    var followedBySlash = i + 2 < glob.Length && glob[i + 2] == '/';
                    sb.Append(followedBySlash ? "(?:.*/)?" : ".*");
                    i += followedBySlash ? 2 : 1;
                }
                else if (c == '*') sb.Append("[^/]*");
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
