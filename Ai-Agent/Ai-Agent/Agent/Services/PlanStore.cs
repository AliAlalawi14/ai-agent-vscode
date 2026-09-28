using Ai_Agent.Models;
using Ai_Agent.Tools.Services;
using System.Text;
using System.Text.RegularExpressions;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Plans live as editable Markdown files in the workspace: .ai/plans/&lt;slug&gt;.plan.md (see PlanDocument).
    /// The file is the source of truth: a request that names it builds from what is in it now, including the user's edits.
    /// </summary>
    public partial class PlanStore
    {
        public const string PlansFolder = ".ai/plans";
        public const string Extension = ".plan.md";

        private readonly ILogger<PlanStore> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public PlanStore(ILogger<PlanStore> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Writes the plan. With <paramref name="existingPath"/> (a revision) that file is overwritten, and steps whose
        /// titles didn't change keep their done/skipped/in-progress status; otherwise a new file is created.
        /// Returns the workspace-relative path.
        /// </summary>
        public async Task<string> SaveAsync(string workspaceRoot, ActivePlan plan, string? existingPath = null)
        {
            await _lock.WaitAsync();
            try
            {
                string relativePath;
                if (existingPath != null && Resolve(workspaceRoot, existingPath) is { } existingFull && File.Exists(existingFull))
                {
                    relativePath = Normalize(existingPath);
                    var previous = PlanDocument.Parse(await File.ReadAllTextAsync(existingFull));
                    var statusByTitle = previous.Steps
                        .GroupBy(s => Key(s.Title))
                        .ToDictionary(g => g.Key, g => g.First().Status);
                    foreach (var step in plan.Steps.Where(s => s.Status == "pending"))
                        if (statusByTitle.TryGetValue(Key(step.Title), out var status))
                            step.Status = status;
                }
                else
                {
                    relativePath = NewPath(workspaceRoot, plan.Title);
                }

                var full = Resolve(workspaceRoot, relativePath)
                           ?? throw new InvalidOperationException($"Invalid plan path {relativePath}");
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, PlanDocument.Render(plan), new UTF8Encoding(false));
                plan.Path = relativePath;
                _logger.LogInformation("Plan saved: {Path} ({Steps} steps)", relativePath, plan.Steps.Count);
                return relativePath;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>The plan in the file right now, or null if the path is invalid or the file is gone.</summary>
        public async Task<ActivePlan?> LoadAsync(string workspaceRoot, string path)
        {
            var full = Resolve(workspaceRoot, path);
            if (full == null || !File.Exists(full)) return null;

            var plan = PlanDocument.Parse(await File.ReadAllTextAsync(full));
            plan.Path = Normalize(path);
            return plan;
        }

        /// <summary>Ticks one step in the file, rewriting only that step's line (the user's formatting stays).</summary>
        public async Task<bool> SetStepStatusAsync(string workspaceRoot, string path, int step, string status)
        {
            var full = Resolve(workspaceRoot, path);
            if (full == null || !File.Exists(full)) return false;

            await _lock.WaitAsync();
            try
            {
                var text = await File.ReadAllTextAsync(full);
                var newline = text.Contains("\r\n") ? "\r\n" : "\n";
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var stepLines = PlanDocument.StepLineIndexes(lines);
                if (step < 1 || step > stepLines.Count) return false;

                var index = stepLines[step - 1];
                var title = PlanDocument.Parse(string.Join("\n", lines)).Steps[step - 1].Title;
                lines[index] = PlanDocument.StepLine(step, title, status);
                await File.WriteAllTextAsync(full, string.Join(newline, lines), new UTF8Encoding(false));
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Saved plans of the workspace, newest first.</summary>
        public IReadOnlyList<(string Path, ActivePlan Plan, DateTime Modified)> List(string workspaceRoot)
        {
            var dir = Resolve(workspaceRoot, PlansFolder);
            if (dir == null || !Directory.Exists(dir)) return Array.Empty<(string, ActivePlan, DateTime)>();

            return Directory.GetFiles(dir, "*" + Extension)
                .Select(f => (Path: $"{PlansFolder}/{System.IO.Path.GetFileName(f)}", File: f))
                .Select(x => (x.Path, Plan: PlanDocument.Parse(File.ReadAllText(x.File)), Modified: File.GetLastWriteTimeUtc(x.File)))
                .OrderByDescending(x => x.Modified)
                .ToList();
        }

        /// <summary>
        /// Full path of a plan file, or null unless it is a *.plan.md directly inside .ai/plans of this workspace
        /// (so a client can never point the agent at another file).
        /// </summary>
        public static string? Resolve(string workspaceRoot, string path)
        {
            var normalized = Normalize(path);
            var isPlansFolder = normalized.Equals(PlansFolder, StringComparison.OrdinalIgnoreCase);
            var isPlanFile = normalized.StartsWith(PlansFolder + "/", StringComparison.OrdinalIgnoreCase) &&
                             normalized.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) &&
                             !normalized[(PlansFolder.Length + 1)..].Contains('/');
            if (!isPlansFolder && !isPlanFile) return null;
            return WorkspacePath.Resolve(workspaceRoot, normalized);
        }

        private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('/');

        private static string NewPath(string workspaceRoot, string title)
        {
            var slug = Slug().Replace(title.ToLowerInvariant(), "-").Trim('-');
            if (slug.Length == 0) slug = "plan";
            if (slug.Length > 50)
            {
                slug = slug[..50];
                var lastWord = slug.LastIndexOf('-');
                slug = (lastWord > 20 ? slug[..lastWord] : slug).TrimEnd('-');   // cut at a word boundary
            }

            var path = $"{PlansFolder}/{slug}{Extension}";
            for (var n = 2; Resolve(workspaceRoot, path) is { } full && File.Exists(full); n++)
                path = $"{PlansFolder}/{slug}-{n}{Extension}";
            return path;
        }

        private static string Key(string title) => Regex.Replace(title.Trim().ToLowerInvariant(), @"\s+", " ");

        [GeneratedRegex("[^a-z0-9]+")]
        private static partial Regex Slug();
    }
}
