using Ai_Agent.Config;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Validates user requests and prevents malicious inputs.
    /// </summary>
    public class ValidationService
    {
        private readonly ILogger<ValidationService> _logger;

        // Maximum request size limits
        private const int MaxTaskLength = 10000;
        private const int MaxMentions = 50;

        // No content regexes on purpose: users of a coding assistant paste code (backticks, ${...}, <script>),
        // and regexes don't stop prompt injection anyway. Safety comes from the path sandbox and tool limits.

        private readonly IOptions<AgentOptions> _options;

        public ValidationService(IOptions<AgentOptions> options, ILogger<ValidationService> logger)
        {
            _options = options;
            _logger = logger;
        }

        /// <summary>
        /// Resolves the workspace a client asked for. Null/empty means the configured WorkspaceRoot;
        /// anything else must match WorkspaceRoot or an entry in AllowedWorkspaces.
        /// Returns null when the workspace is not allowed.
        /// </summary>
        public string? ResolveWorkspace(string? requested)
        {
            var options = _options.Value;
            if (string.IsNullOrWhiteSpace(requested))
                return options.WorkspaceRoot;

            var normalized = NormalizeDirectory(requested);
            var allowed = options.AllowedWorkspaces
                .Append(options.WorkspaceRoot)
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(NormalizeDirectory);

            if (allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                return normalized;

            _logger.LogWarning("Rejected workspace outside AllowedWorkspaces: {Workspace}", requested);
            return null;
        }

        private static string NormalizeDirectory(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>
        /// Validates an agent run request.
        /// </summary>
        public ValidationResult ValidateRequest(string task, string? workspace = null)
        {
            var errors = new List<string>();

            // Task length validation
            if (string.IsNullOrWhiteSpace(task))
            {
                errors.Add("Task cannot be empty");
            }
            else if (task.Length > MaxTaskLength)
            {
                errors.Add($"Task too long ({task.Length} chars, max {MaxTaskLength})");
            }

            // Count @mentions
            var mentionCount = task.Split('@').Length - 1;
            if (mentionCount > MaxMentions)
            {
                errors.Add($"Too many @mentions ({mentionCount}, max {MaxMentions})");
            }

            // Workspace validation
            if (!string.IsNullOrEmpty(workspace))
            {
                if (workspace.Contains(".."))
                {
                    errors.Add("Workspace path cannot contain parent directory references");
                }
                if (workspace.Length > 500)
                {
                    errors.Add("Workspace path too long");
                }
            }

            var result = new ValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                SanitizedTask = SanitizeTask(task)
            };

            if (!result.IsValid)
            {
                _logger.LogWarning("Request validation failed: {Errors}", string.Join(", ", errors));
            }

            return result;
        }

        /// <summary>
        /// Sanitizes a task string for safe processing.
        /// </summary>
        private string SanitizeTask(string task)
        {
            if (string.IsNullOrWhiteSpace(task))
                return string.Empty;

            // Trim whitespace
            var sanitized = task.Trim();

            // Normalize line endings
            sanitized = sanitized.Replace("\r\n", "\n");

            // Remove null bytes
            sanitized = sanitized.Replace("\0", "");

            return sanitized;
        }

        /// <summary>
        /// Validates that a string is safe to include in logs.
        /// </summary>
        public string SanitizeForLog(string input, int maxLength = 1000)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            var sanitized = input.Replace("\n", "\\n").Replace("\r", "\\r");

            if (sanitized.Length > maxLength)
            {
                sanitized = sanitized[..maxLength] + "...";
            }

            return sanitized;
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public string SanitizedTask { get; set; } = string.Empty;

        public void ThrowIfInvalid()
        {
            if (!IsValid)
            {
                throw new ValidationException(string.Join("; ", Errors));
            }
        }
    }

    public class ValidationException : Exception
    {
        public ValidationException(string message) : base(message) { }
    }
}
