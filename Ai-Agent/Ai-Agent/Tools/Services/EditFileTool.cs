using Ai_Agent.Tools.Interfaces;
using System.Text;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Search-and-replace edit (the Claude Code / Aider pattern): old_string must match the file exactly
    /// once, so the edit can't land in the wrong place, and no line numbers are needed. Keeps the file's
    /// line endings and UTF-8 BOM. The user approves the change before it is written.
    /// </summary>
    public class EditFileTool : ITool
    {
        private readonly string _workspaceRoot;

        public string Name => "edit_file";
        public string Description =>
            "Edits an existing file by replacing an exact piece of text. Preferred way to change existing files. " +
            "old_string must appear exactly once in the file: copy it from read_file (without the line-number prefix) " +
            "and include 2-3 surrounding lines so it is unique. The user must approve the change first.";

        public Dictionary<string, string> Parameters => new()
        {
            { "path", "Relative path to the file" },
            { "old_string", "The exact current text to replace (must match once, including indentation)" },
            { "new_string", "The replacement text" },
            { "replace_all", "[Optional] 'true' to replace every occurrence of old_string instead of exactly one" }
        };

        public bool RequiresApproval => true;

        public IReadOnlyDictionary<string, object> ParameterSchemas { get; } = new Dictionary<string, object>
        {
            ["replace_all"] = ParamSchema.Boolean()
        };

        public EditFileTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public async Task<ToolPreview> PreviewAsync(Dictionary<string, string> parameters)
        {
            var edit = await ComputeAsync(parameters);
            return edit.Error != null
                ? ToolPreview.Fail(edit.Error)
                : new ToolPreview { FilePath = edit.RelativePath, Before = edit.Before, After = edit.After };
        }

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            var edit = await ComputeAsync(parameters);
            if (edit.Error != null) return $"ERROR: {edit.Error}";

            try
            {
                await File.WriteAllTextAsync(edit.FullPath!, edit.After, new UTF8Encoding(edit.HasBom));
                return $"SUCCESS: Edited {edit.RelativePath} ({edit.Replacements} replacement{(edit.Replacements == 1 ? "" : "s")}).";
            }
            catch (Exception ex)
            {
                return $"ERROR writing {edit.RelativePath}: {ex.Message}";
            }
        }

        private async Task<Edit> ComputeAsync(Dictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue("path", out var relativePath) || string.IsNullOrWhiteSpace(relativePath))
                return Edit.Fail("Missing 'path' parameter");
            if (!parameters.TryGetValue("old_string", out var oldString) || oldString.Length == 0)
                return Edit.Fail("Missing 'old_string' (the exact text to replace). To create a file use write_file.");
            if (!parameters.TryGetValue("new_string", out var newString))
                return Edit.Fail("Missing 'new_string' parameter");
            var replaceAll = string.Equals(parameters.GetValueOrDefault("replace_all"), "true", StringComparison.OrdinalIgnoreCase);

            var fullPath = WorkspacePath.Resolve(_workspaceRoot, relativePath);
            if (fullPath == null)
                return Edit.Fail("Access denied - file is outside the workspace");
            if (!File.Exists(fullPath))
                return Edit.Fail($"File not found: {relativePath}. To create a new file use write_file.");

            var bytes = await File.ReadAllBytesAsync(fullPath);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var before = hasBom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);

            // The model writes "\n"; a CRLF file needs "\r\n" for the match and for the inserted text
            if (before.Contains("\r\n"))
            {
                oldString = ToCrlf(oldString);
                newString = ToCrlf(newString);
            }

            if (oldString == newString)
                return Edit.Fail("old_string and new_string are identical; nothing to change.");

            if (Agent.Services.SecretRedactor.ContainsMarker(oldString) || Agent.Services.SecretRedactor.ContainsMarker(newString))
                return Edit.Fail($"The edit touches '{Agent.Services.SecretRedactor.Marker}' (a hidden secret the model cannot see). " +
                                 "Leave secret values alone, or ask the user to change them.");

            var count = CountOccurrences(before, oldString);
            if (count == 0)
                return Edit.Fail(NotFoundHint(before, oldString, relativePath));
            if (count > 1 && !replaceAll)
                return Edit.Fail($"old_string appears {count} times in {relativePath}. Include more surrounding lines so it matches " +
                                 "exactly once, or pass replace_all=true to change every occurrence.");

            var after = replaceAll
                ? before.Replace(oldString, newString, StringComparison.Ordinal)
                : ReplaceFirst(before, oldString, newString);

            return new Edit
            {
                RelativePath = relativePath,
                FullPath = fullPath,
                Before = before,
                After = after,
                HasBom = hasBom,
                Replacements = replaceAll ? count : 1
            };
        }

        private static string ToCrlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string ReplaceFirst(string text, string oldValue, string newValue)
        {
            var i = text.IndexOf(oldValue, StringComparison.Ordinal);
            return text[..i] + newValue + text[(i + oldValue.Length)..];
        }

        /// <summary>Helps the model fix a near miss: shows where the first line of old_string occurs, if anywhere.</summary>
        private static string NotFoundHint(string content, string oldString, string relativePath)
        {
            var firstLine = oldString.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var hits = firstLine == null
                ? new List<int>()
                : lines.Select((l, i) => (l, i)).Where(x => x.l.Trim() == firstLine).Select(x => x.i + 1).Take(3).ToList();

            var hint = hits.Count > 0
                ? $" Its first line occurs at line {string.Join(", ", hits)}; the rest (indentation or following lines) differs. " +
                  "Re-read those lines and copy them exactly."
                : " Re-read the file and copy the text exactly (without line-number prefixes).";
            return $"old_string was not found in {relativePath}.{hint}";
        }

        private sealed class Edit
        {
            public string? Error { get; init; }
            public string RelativePath { get; init; } = string.Empty;
            public string? FullPath { get; init; }
            public string Before { get; init; } = string.Empty;
            public string After { get; init; } = string.Empty;
            public bool HasBom { get; init; }
            public int Replacements { get; init; }

            public static Edit Fail(string error) => new() { Error = error };
        }
    }
}
