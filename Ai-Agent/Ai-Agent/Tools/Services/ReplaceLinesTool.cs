using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{
    public class ReplaceLinesTool : ITool
    {
        private readonly string _workspaceRoot;
        private readonly ILogger<ReplaceLinesTool>? _logger;

        public string Name => "replace_lines";
        public string Description => "Replaces specific lines in a file. Use this for targeted edits instead of rewriting the whole file. The user must approve the change first.";

        public Dictionary<string, string> Parameters => new()
        {
            { "path", "Relative path to the file" },
            { "startLine", "First line number to replace (1-based)" },
            { "endLine", "Last line number to replace (same as startLine for single line)" },
            { "newContent", "The new text to insert in place of the old lines" },
            { "expectedContent", "[Optional] The exact current content at those lines, to verify before replacing" }
        };

        public bool RequiresApproval => true;

        public IReadOnlyDictionary<string, object> ParameterSchemas { get; } = new Dictionary<string, object>
        {
            ["startLine"] = ParamSchema.Integer(minimum: 1),
            ["endLine"] = ParamSchema.Integer(minimum: 1)
        };

        public ReplaceLinesTool(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public ReplaceLinesTool(string workspaceRoot, ILogger<ReplaceLinesTool> logger)
        {
            _workspaceRoot = workspaceRoot;
            _logger = logger;
        }

        public async Task<ToolPreview> PreviewAsync(Dictionary<string, string> parameters)
        {
            var edit = await ComputeAsync(parameters);
            if (edit.Error != null) return ToolPreview.Fail(edit.Error);

            return new ToolPreview
            {
                FilePath = edit.RelativePath,
                Before = edit.Before,
                After = edit.After,
                IsNewFile = false
            };
        }

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            var edit = await ComputeAsync(parameters);
            if (edit.Error != null) return $"ERROR: {edit.Error}";

            try
            {
                // Write exactly the text the user approved in the preview.
                // No .backup files: the ChangeTracker keeps the before-content and patch for revert.
                await File.WriteAllTextAsync(edit.FullPath!, edit.After);

                var response = $"SUCCESS: Replaced lines {edit.StartLine}-{edit.EndLine} in {edit.RelativePath}\n\n";
                response += "--- BEFORE ---\n";
                foreach (var line in edit.OldLines)
                    response += $"- {line}\n";
                response += "\n+++ AFTER +++\n";
                foreach (var line in edit.NewLines)
                    response += $"+ {line}\n";

                if (edit.OldLines.Count != edit.NewLines.Count)
                    response += $"\nFile went from {edit.OriginalLineCount} to {edit.ResultLineCount} lines.";

                return response;
            }
            catch (Exception ex)
            {
                return $"ERROR replacing lines: {ex.Message}";
            }
        }

        /// <summary>Validates the call and computes the new file content without writing anything.</summary>
        private async Task<LineEdit> ComputeAsync(Dictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue("path", out var relativePath))
                return LineEdit.Fail("Missing 'path' parameter");

            if (!parameters.TryGetValue("startLine", out var startStr) || !int.TryParse(startStr, out var startLine))
                return LineEdit.Fail("Missing or invalid 'startLine' parameter");

            if (!parameters.TryGetValue("endLine", out var endStr) || !int.TryParse(endStr, out var endLine))
                return LineEdit.Fail("Missing or invalid 'endLine' parameter");

            if (!parameters.TryGetValue("newContent", out var newContent))
                return LineEdit.Fail("Missing 'newContent' parameter");

            if (startLine < 1)
                return LineEdit.Fail("startLine must be 1 or greater");

            if (endLine < startLine)
                return LineEdit.Fail("endLine must be >= startLine");

            var fullPath = WorkspacePath.Resolve(_workspaceRoot, relativePath);
            if (fullPath == null)
                return LineEdit.Fail("Access denied - file is outside workspace");

            if (!File.Exists(fullPath))
                return LineEdit.Fail($"File not found: {relativePath}");

            var before = await File.ReadAllTextAsync(fullPath);
            var eol = before.Contains("\r\n") ? "\r\n" : "\n";
            // Split exactly like read_file (File.ReadAllLines: \r\n, \n and a lone \r all end a line),
            // otherwise the line numbers the model copied from read_file point at the wrong lines
            var lines = System.Text.RegularExpressions.Regex.Split(before, "\r\n|\n|\r").ToList();
            var endsWithNewline = lines.Count > 0 && lines[^1].Length == 0;
            if (endsWithNewline) lines.RemoveAt(lines.Count - 1);

            if (startLine > lines.Count)
                return LineEdit.Fail($"startLine ({startLine}) exceeds file length ({lines.Count} lines)");

            if (endLine > lines.Count)
                endLine = lines.Count;

            var oldLines = lines.Skip(startLine - 1).Take(endLine - startLine + 1).ToList();

            if (parameters.TryGetValue("expectedContent", out var expectedContent) && !string.IsNullOrWhiteSpace(expectedContent))
            {
                var normalizedExpected = expectedContent.Replace("\r\n", "\n").Trim();
                var normalizedActual = string.Join("\n", oldLines).Trim();

                if (normalizedExpected != normalizedActual)
                {
                    // Usually wrong line numbers or a mistyped expectedContent, rarely an outside edit;
                    // saying "the file changed" made the model blame a formatter and give up.
                    return LineEdit.Fail(
                        $"expectedContent does not match lines {startLine}-{endLine}.\n\n" +
                        $"EXPECTED:\n{Truncate(normalizedExpected, 300)}\n\n" +
                        $"ACTUAL at lines {startLine}-{endLine}:\n{Truncate(normalizedActual, 300)}\n\n" +
                        "The file was NOT modified. Use the ACTUAL text above to pick the right line numbers " +
                        "(re-read the file if needed) and try again.");
                }
            }

            var newLines = newContent.Replace("\r\n", "\n").Split('\n').ToList();

            var resultLines = new List<string>(lines.Count + newLines.Count);
            resultLines.AddRange(lines.Take(startLine - 1));
            resultLines.AddRange(newLines);
            resultLines.AddRange(lines.Skip(endLine));

            var after = string.Join(eol, resultLines) + (endsWithNewline ? eol : string.Empty);

            return new LineEdit
            {
                RelativePath = relativePath,
                FullPath = fullPath,
                Before = before,
                After = after,
                StartLine = startLine,
                EndLine = endLine,
                OldLines = oldLines,
                NewLines = newLines,
                OriginalLineCount = lines.Count,
                ResultLineCount = resultLines.Count
            };
        }

        private static string Truncate(string value, int maxLength)
        {
            if (value.Length <= maxLength) return value;
            return value[..maxLength] + "...";
        }

        private sealed class LineEdit
        {
            public string? Error { get; init; }
            public string RelativePath { get; init; } = string.Empty;
            public string? FullPath { get; init; }
            public string Before { get; init; } = string.Empty;
            public string After { get; init; } = string.Empty;
            public int StartLine { get; init; }
            public int EndLine { get; init; }
            public List<string> OldLines { get; init; } = new();
            public List<string> NewLines { get; init; } = new();
            public int OriginalLineCount { get; init; }
            public int ResultLineCount { get; init; }

            public static LineEdit Fail(string error) => new() { Error = error };
        }
    }
}
