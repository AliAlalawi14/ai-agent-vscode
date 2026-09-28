using Ai_Agent.Models;
using System.Collections.Concurrent;
using System.Xml.Linq;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Gathers project information (type, packages, folders, code map) so the AI has context
    /// about the project without needing to discover it through tools.
    /// Cached per workspace: rebuilt after the agent changes a file there (Invalidate) or after CacheTtl.
    /// </summary>
    public class ProjectContextService
    {
        private readonly ILogger<ProjectContextService> _logger;
        private readonly ProjectIndexer _projectIndexer;
        private readonly ConcurrentDictionary<string, (ProjectContext Context, DateTime BuiltAt)> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        // Also catches edits the user makes by hand, which the agent is not told about
        public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

        public ProjectContextService(ILogger<ProjectContextService> logger, ProjectIndexer projectIndexer)
        {
            _logger = logger;
            _projectIndexer = projectIndexer;
        }

        /// <summary>The project context of <paramref name="workspaceRoot"/>, from the cache when it is fresh.</summary>
        public ProjectContext GatherContext(string workspaceRoot)
        {
            var key = Key(workspaceRoot);
            if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.BuiltAt < CacheTtl)
                return cached.Context;

            var context = Build(workspaceRoot);
            _cache[key] = (context, DateTime.UtcNow);
            return context;
        }

        /// <summary>Drops the cached context, e.g. after the agent changed a file in this workspace.</summary>
        public void Invalidate(string workspaceRoot) => _cache.TryRemove(Key(workspaceRoot), out _);

        private static string Key(string workspaceRoot) =>
            Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private ProjectContext Build(string workspaceRoot)
        {
            var context = new ProjectContext();

            try
            {
                // 1. Find and parse .csproj file
                var csprojFiles = Directory.GetFiles(workspaceRoot, "*.csproj", SearchOption.TopDirectoryOnly);

                if (csprojFiles.Length > 0)
                {
                    var csprojPath = csprojFiles[0];
                    context.ProjectFiles.Add(Path.GetFileName(csprojPath));
                    ParseProjectFile(csprojPath, context);
                }

                // 2. Get folder structure
                GetFolderStructure(workspaceRoot, context);

                // 3. Read appsettings.json summary
                ReadConfigSummary(workspaceRoot, context);

                // 4. Read Program.cs summary
                ReadProgramSummary(workspaceRoot, context);

                _logger.LogInformation("Project context gathered: {Type}, {Framework}, {Packages} packages, {Folders} folders",
                    context.ProjectType, context.TargetFramework, context.Packages.Count, context.Folders.Count);


                context.ProjectIndex = _projectIndexer.BuildIndex(workspaceRoot);

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to gather full project context. Using partial context.");
            }

            return context;
        }

        /// <summary>
        /// Parses the .csproj file to extract project type, framework, and packages.
        /// </summary>
        private void ParseProjectFile(string csprojPath, ProjectContext context)
        {
            try
            {
                var xml = XDocument.Load(csprojPath);
                var projectElement = xml.Root;
                if (projectElement == null) return;

                // Determine project type from SDK attribute
                var sdk = projectElement.Attribute("Sdk")?.Value ?? string.Empty;
                context.ProjectType = sdk switch
                {
                    "Microsoft.NET.Sdk.Web" => "ASP.NET Core Web API",
                    "Microsoft.NET.Sdk" => "Console Application or Class Library",
                    "Microsoft.NET.Sdk.BlazorWebAssembly" => "Blazor WebAssembly",
                    _ => sdk.Replace("Microsoft.NET.Sdk.", "")
                };

                // Get target framework
                var targetFramework = projectElement
                    .Descendants("TargetFramework")
                    .FirstOrDefault()?.Value;

                if (!string.IsNullOrEmpty(targetFramework))
                {
                    context.TargetFramework = targetFramework;
                }

                // Get NuGet packages
                var packageReferences = projectElement
                    .Descendants("PackageReference")
                    .Select(p => $"{p.Attribute("Include")?.Value} (v{p.Attribute("Version")?.Value})");

                context.Packages.AddRange(packageReferences);

                _logger.LogInformation("Parsed project file: {Type}, {Framework}, {PackageCount} packages",
                    context.ProjectType, context.TargetFramework, context.Packages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse .csproj file");
            }
        }

        /// <summary>
        /// Gets top-level folder structure.
        /// </summary>
        private void GetFolderStructure(string workspaceRoot, ProjectContext context)
        {
            try
            {
                var directories = Directory.GetDirectories(workspaceRoot);
                foreach (var dir in directories)
                {
                    var dirName = Path.GetFileName(dir);
                    // Skip common non-code folders
                    if (dirName is "bin" or "obj" or ".git" or ".vs" or "node_modules")
                        continue;

                    context.Folders.Add(dirName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get folder structure");
            }
        }

        /// <summary>
        /// Reads key settings from appsettings.json.
        /// </summary>
        private void ReadConfigSummary(string workspaceRoot, ProjectContext context)
        {
            try
            {
                var configPath = Path.Combine(workspaceRoot, "appsettings.json");
                if (File.Exists(configPath))
                {
                    var content = File.ReadAllText(configPath);
                    // Take just the first 500 characters as summary
                    context.ConfigSummary = content.Length > 500
                        ? content[..500] + "..."
                        : content;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read appsettings.json");
            }
        }

        /// <summary>
        /// Reads Program.cs and creates a brief summary.
        /// </summary>
        private void ReadProgramSummary(string workspaceRoot, ProjectContext context)
        {
            try
            {
                var programPath = Path.Combine(workspaceRoot, "Program.cs");
                if (File.Exists(programPath))
                {
                    var lines = File.ReadAllLines(programPath);
                    // Just take first 20 lines as summary
                    var summary = string.Join("\n", lines.Take(20));
                    context.ProgramSummary = summary;
                    if (lines.Length > 20)
                    {
                        context.ProgramSummary += $"\n... ({lines.Length} total lines)";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Program.cs");
            }
        }
    }
}