namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Enforces path security - prevents directory traversal and ensures sandboxing.
    /// </summary>
    public class PathSecurityService
    {
        private readonly ILogger<PathSecurityService> _logger;

        // File extensions that are dangerous to write
        private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1",
            ".com", ".msi", ".scr", ".vbs", ".js", ".wsf",
            ".reg", ".inf"
        };

        // Directories that should never be accessed
        private static readonly HashSet<string> ForbiddenPaths = new(StringComparer.OrdinalIgnoreCase)
        {
            "/etc", "/bin", "/sbin", "/usr/bin", "/usr/sbin",
            "/windows", "/windows/system32", "/program files", "/programdata",
            "c:\\windows", "c:\\program files", "c:\\programdata"
        };

        public PathSecurityService(ILogger<PathSecurityService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Validates that a path is safe and within the allowed workspace.
        /// </summary>
        public PathValidationResult ValidatePath(string path, string workspaceRoot)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add("Path cannot be empty");
                return new PathValidationResult { IsValid = false, Errors = errors };
            }

            // Normalize paths
            var normalizedPath = NormalizePath(path);
            var normalizedWorkspace = NormalizePath(workspaceRoot);

            // Check for directory traversal
            if (normalizedPath.Contains("..") || path.Contains("..")
                || normalizedPath.Contains("~") || path.StartsWith("/") || path.StartsWith("\\"))
            {
                errors.Add("Path contains directory traversal characters");
            }

            // Resolve to absolute path
            string absolutePath;
            try
            {
                absolutePath = Path.GetFullPath(Path.Combine(normalizedWorkspace, normalizedPath));
            }
            catch (Exception ex)
            {
                errors.Add($"Invalid path format: {ex.Message}");
                return new PathValidationResult { IsValid = false, Errors = errors };
            }

            // Ensure path is within workspace
            if (!IsSubPath(normalizedWorkspace, absolutePath))
            {
                errors.Add("Path is outside the allowed workspace");
                _logger.LogWarning(
                    "Path traversal attempt detected: {Path} resolved to {AbsolutePath}, outside workspace {Workspace}",
                    path, absolutePath, normalizedWorkspace);
            }

            // Check against forbidden paths
            foreach (var forbidden in ForbiddenPaths)
            {
                if (absolutePath.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("Access to system directories is not allowed");
                    _logger.LogWarning("Attempted access to forbidden path: {Path}", absolutePath);
                    break;
                }
            }

            // Check file extension for write operations
            var extension = Path.GetExtension(absolutePath);
            if (!string.IsNullOrEmpty(extension) && DangerousExtensions.Contains(extension))
            {
                errors.Add($"Writing {extension} files is not allowed for security");
            }

            var result = new PathValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                AbsolutePath = absolutePath,
                RelativePath = normalizedPath,
                IsSafeExtension = !DangerousExtensions.Contains(extension)
            };

            if (!result.IsValid)
            {
                _logger.LogWarning("Path validation failed for '{Path}': {Errors}",
                    path, string.Join(", ", errors));
            }

            return result;
        }

        /// <summary>
        /// Quick check if a file extension is safe to write.
        /// </summary>
        public bool IsSafeExtension(string path)
        {
            var extension = Path.GetExtension(path);
            return !DangerousExtensions.Contains(extension);
        }

        /// <summary>
        /// Normalizes a path for comparison.
        /// </summary>
        private string NormalizePath(string path)
        {
            return path.Replace('/', Path.DirectorySeparatorChar)
                       .Replace('\\', Path.DirectorySeparatorChar)
                       .TrimStart(Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Checks if a path is a sub-path of another.
        /// </summary>
        private bool IsSubPath(string parent, string child)
        {
            var parentUri = new Uri(parent.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? parent
                : parent + Path.DirectorySeparatorChar);
            var childUri = new Uri(child);

            return childUri.AbsolutePath.StartsWith(parentUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sanitizes a filename to be safe.
        /// </summary>
        public string SanitizeFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                return "unnamed";

            // Remove invalid characters
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(filename.Where(c => !invalid.Contains(c)).ToArray());

            // Limit length
            if (sanitized.Length > 255)
            {
                var extension = Path.GetExtension(sanitized);
                sanitized = sanitized[..(255 - extension.Length)] + extension;
            }

            return sanitized;
        }
    }

    public class PathValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public string AbsolutePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public bool IsSafeExtension { get; set; } = true;
        

        public void ThrowIfInvalid()
        {
            if (!IsValid)
            {
                throw new UnauthorizedAccessException(string.Join("; ", Errors));
            }
        }
    }
}
