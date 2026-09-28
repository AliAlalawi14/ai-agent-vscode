using Ai_Agent.Models;
using System.Text.RegularExpressions;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Scans all code files in the project and builds an index
    /// of classes, methods, properties, and dependencies.
    /// Supports C#, JavaScript, TypeScript, Python, HTML, and CSS.
    /// </summary>
    public class ProjectIndexer
    {
        private readonly ILogger<ProjectIndexer> _logger;

        // Skip these folders
        private static readonly string[] SkipFolders = { "bin", "obj", ".git", ".vs", "node_modules" };

        // C# patterns
        private static readonly Regex ClassRegex = new(@"class\s+(\w+)", RegexOptions.Compiled);
        private static readonly Regex MethodRegex = new(@"(public|private|protected|internal|static|\s)+\s+(\w+)\s+(\w+)\s*\(", RegexOptions.Compiled);
        private static readonly Regex PropertyRegex = new(@"(public|private|protected|internal)\s+(\w+)\s+(\w+)\s*\{", RegexOptions.Compiled);
        private static readonly Regex UsingRegex = new(@"using\s+(\S+);", RegexOptions.Compiled);

        // JavaScript/TypeScript patterns
        private static readonly Regex JSFunctionRegex = new(@"(function\s+(\w+)|(\w+)\s*=\s*(\([^)]*\))\s*=>)", RegexOptions.Compiled);
        private static readonly Regex JSClassRegex = new(@"class\s+(\w+)", RegexOptions.Compiled);
        private static readonly Regex JSExportRegex = new(@"export\s+(default\s+)?(class|function|const|let|var)\s+(\w+)", RegexOptions.Compiled);
        private static readonly Regex JSImportRegex = new(@"import\s+.*from\s+['""]([^'""]+)['""]", RegexOptions.Compiled);

        // Python patterns
        private static readonly Regex PyFunctionRegex = new(@"def\s+(\w+)\s*\(", RegexOptions.Compiled);
        private static readonly Regex PyClassRegex = new(@"class\s+(\w+)", RegexOptions.Compiled);
        private static readonly Regex PyImportRegex = new(@"(import\s+(\S+)|from\s+(\S+)\s+import)", RegexOptions.Compiled);

        public ProjectIndexer(ILogger<ProjectIndexer> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Builds the complete project index for all supported languages, for the given workspace.
        /// </summary>
        public ProjectIndex BuildIndex(string workspaceRoot)
        {
            var index = new ProjectIndex();
            var contents = new Dictionary<string, string>();   // relative path -> content, read once

            try
            {
                var files = EnumerateSourceFiles(workspaceRoot).ToList();

                index.TotalFiles = files.Count;

                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(workspaceRoot, file);

                    try
                    {
                        var content = File.ReadAllText(file);
                        contents[relativePath] = content;
                        var lines = content.Split('\n');
                        var lineCount = lines.Length;
                        var extension = Path.GetExtension(file).ToLower();

                        switch (extension)
                        {
                            case ".cs":
                                IndexCSharpFile(content, relativePath, lineCount, index);
                                break;
                            case ".js":
                            case ".jsx":
                                IndexJavaScriptFile(content, relativePath, lineCount, index);
                                break;
                            case ".ts":
                            case ".tsx":
                                IndexTypeScriptFile(content, relativePath, lineCount, index);
                                break;
                            case ".py":
                                IndexPythonFile(content, relativePath, lineCount, index);
                                break;
                            case ".html":
                            case ".css":
                                index.Files.Add(new Models.FileInfo
                                {
                                    Path = relativePath,
                                    Lines = lineCount,
                                    Summary = $"{extension.ToUpper()} file ({lineCount} lines)"
                                });
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to index file: {File}", relativePath);
                    }
                }

                index.TotalClasses = index.Classes.Count;

                _logger.LogInformation(
                    "Project indexed: {Files} files, {Classes} classes",
                    index.TotalFiles,
                    index.TotalClasses);

                // Find dependencies
                index.Dependencies = FindDependencies(index, contents);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build project index");
            }

            return index;
        }

        // ==================== C# INDEXING ====================

        private void IndexCSharpFile(string content, string relativePath, int lineCount, ProjectIndex index)
        {
            var classMatches = ClassRegex.Matches(content);
            foreach (Match match in classMatches)
            {
                var className = match.Groups[1].Value;
                index.Classes.Add(new ClassInfo
                {
                    Name = className,
                    FilePath = relativePath,
                    Methods = ExtractMethods(content),
                    Properties = ExtractProperties(content),
                    Dependencies = ExtractDependencies(content)
                });
            }

            index.Files.Add(new Models.FileInfo
            {
                Path = relativePath,
                Lines = lineCount,
                Summary = GenerateFileSummary(content, relativePath)
            });
        }

        private List<string> ExtractMethods(string content)
        {
            var methods = new List<string>();
            var matches = MethodRegex.Matches(content);

            foreach (Match match in matches)
            {
                var methodName = match.Groups[3].Value;
                if (methodName is "class" or "struct" or "enum" or "interface" or "if" or "while" or "for" or "foreach" or "switch" or "catch")
                    continue;

                if (!methods.Contains(methodName))
                    methods.Add(methodName);
            }

            return methods;
        }

        private List<string> ExtractProperties(string content)
        {
            var properties = new List<string>();
            var matches = PropertyRegex.Matches(content);

            foreach (Match match in matches)
            {
                var propName = match.Groups[3].Value;
                if (!properties.Contains(propName))
                    properties.Add(propName);
            }

            return properties;
        }

        private List<string> ExtractDependencies(string content)
        {
            var dependencies = new List<string>();
            var matches = UsingRegex.Matches(content);

            foreach (Match match in matches)
            {
                var dep = match.Groups[1].Value;
                if (!dependencies.Contains(dep))
                    dependencies.Add(dep);
            }

            return dependencies;
        }

        private string GenerateFileSummary(string content, string filePath)
        {
            var classes = ClassRegex.Matches(content);
            if (classes.Count == 0)
                return "No classes found";

            var classNames = classes.Select(m => m.Groups[1].Value).Distinct();
            return $"Contains: {string.Join(", ", classNames)}";
        }

        // ==================== JAVASCRIPT INDEXING ====================

        private void IndexJavaScriptFile(string content, string relativePath, int lineCount, ProjectIndex index)
        {
            var classMatches = JSClassRegex.Matches(content);
            foreach (Match match in classMatches)
            {
                var className = match.Groups[1].Value;
                index.Classes.Add(new ClassInfo
                {
                    Name = className,
                    FilePath = relativePath,
                    Methods = ExtractJSFunctions(content),
                    Dependencies = ExtractJSImports(content)
                });
            }

            // Export declarations
            var exportMatches = JSExportRegex.Matches(content);
            foreach (Match match in exportMatches)
            {
                var exportName = match.Groups[3].Value;
                if (!index.Classes.Any(c => c.Name == exportName && c.FilePath == relativePath))
                {
                    index.Classes.Add(new ClassInfo
                    {
                        Name = exportName,
                        FilePath = relativePath,
                        Methods = new List<string>(),
                        Dependencies = ExtractJSImports(content)
                    });
                }
            }

            index.Files.Add(new Models.FileInfo
            {
                Path = relativePath,
                Lines = lineCount,
                Summary = $"JavaScript/React file - {lineCount} lines"
            });
        }

        private List<string> ExtractJSFunctions(string content)
        {
            var functions = new List<string>();
            var matches = JSFunctionRegex.Matches(content);
            foreach (Match match in matches)
            {
                var funcName = match.Groups[2].Value;
                if (string.IsNullOrEmpty(funcName))
                    funcName = match.Groups[3].Value;
                if (!string.IsNullOrEmpty(funcName) && !functions.Contains(funcName))
                    functions.Add(funcName);
            }
            return functions;
        }

        private List<string> ExtractJSImports(string content)
        {
            var imports = new List<string>();
            var matches = JSImportRegex.Matches(content);
            foreach (Match match in matches)
            {
                var imp = match.Groups[1].Value;
                if (!imports.Contains(imp))
                    imports.Add(imp);
            }
            return imports;
        }

        // ==================== TYPESCRIPT INDEXING ====================

        private void IndexTypeScriptFile(string content, string relativePath, int lineCount, ProjectIndex index)
        {
            // TypeScript shares JS patterns + interfaces
            IndexJavaScriptFile(content, relativePath, lineCount, index);

            var fileInfo = index.Files.FirstOrDefault(f => f.Path == relativePath);
            if (fileInfo != null)
            {
                fileInfo.Summary = $"TypeScript file - {lineCount} lines";
            }
        }

        // ==================== PYTHON INDEXING ====================

        private void IndexPythonFile(string content, string relativePath, int lineCount, ProjectIndex index)
        {
            var classMatches = PyClassRegex.Matches(content);
            foreach (Match match in classMatches)
            {
                var className = match.Groups[1].Value;
                index.Classes.Add(new ClassInfo
                {
                    Name = className,
                    FilePath = relativePath,
                    Methods = ExtractPyFunctions(content),
                    Dependencies = ExtractPyImports(content)
                });
            }

            index.Files.Add(new Models.FileInfo
            {
                Path = relativePath,
                Lines = lineCount,
                Summary = $"Python file - {lineCount} lines"
            });
        }

        private List<string> ExtractPyFunctions(string content)
        {
            var functions = new List<string>();
            var matches = PyFunctionRegex.Matches(content);
            foreach (Match match in matches)
            {
                var funcName = match.Groups[1].Value;
                if (!functions.Contains(funcName))
                    functions.Add(funcName);
            }
            return functions;
        }

        private List<string> ExtractPyImports(string content)
        {
            var imports = new List<string>();
            var matches = PyImportRegex.Matches(content);
            foreach (Match match in matches)
            {
                var imp = match.Groups[2].Value;
                if (string.IsNullOrEmpty(imp))
                    imp = match.Groups[3].Value;
                if (!string.IsNullOrEmpty(imp) && !imports.Contains(imp))
                    imports.Add(imp);
            }
            return imports;
        }

        // ==================== COMMON ====================

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".js", ".jsx", ".ts", ".tsx", ".py", ".html", ".css"
        };

        /// <summary>
        /// Walks the workspace without ever entering bin/obj/.git/.vs/node_modules
        /// (GetFiles(AllDirectories) would enumerate all of node_modules before filtering).
        /// </summary>
        private IEnumerable<string> EnumerateSourceFiles(string workspaceRoot)
        {
            var pending = new Stack<string>();
            pending.Push(workspaceRoot);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] subdirs, files;
                try
                {
                    subdirs = Directory.GetDirectories(dir);
                    files = Directory.GetFiles(dir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    _logger.LogDebug("Skipping unreadable folder {Dir}: {Message}", dir, ex.Message);
                    continue;
                }

                foreach (var sub in subdirs)
                    if (!SkipFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                        pending.Push(sub);

                foreach (var file in files)
                    if (SupportedExtensions.Contains(Path.GetExtension(file)))
                        yield return file;
            }
        }

        /// <summary>Uses the file contents BuildIndex already read (no second pass over the disk).</summary>
        private static List<DependencyInfo> FindDependencies(ProjectIndex index, Dictionary<string, string> contents)
        {
            var dependencies = new List<DependencyInfo>();

            foreach (var file in index.Files)
            {
                if (!contents.TryGetValue(file.Path, out var content)) continue;
                var classesInFile = index.Classes.Where(c => c.FilePath == file.Path).ToList();
                if (classesInFile.Count == 0) continue;

                foreach (var otherClass in index.Classes)
                {
                    if (otherClass.FilePath == file.Path || !content.Contains(otherClass.Name)) continue;

                    foreach (var cls in classesInFile)
                    {
                        dependencies.Add(new DependencyInfo
                        {
                            FromClass = cls.Name,
                            ToClass = otherClass.Name,
                            Relationship = "uses"
                        });
                    }
                }
            }

            return dependencies.Distinct().ToList();
        }
    }
}