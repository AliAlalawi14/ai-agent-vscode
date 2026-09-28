using System.Text;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Computes standard unified diffs between two text inputs.
    /// Produces output compatible with GNU diff -u format,
    /// usable by git apply, patch, and any diff viewer.
    /// </summary>
    public class UnifiedDiffService
    {
        // Configure diff output
        private const int ContextLines = 3;

        /// <summary>
        /// Computes a unified diff between two strings.
        /// Returns the patch in standard unified diff format.
        /// </summary>
        /// <param name="filePath">Relative file path for the diff header</param>
        /// <param name="before">Original file content</param>
        /// <param name="after">Modified file content</param>
        /// <param name="contextLines">Number of context lines around changes</param>
        /// <returns>Unified diff string, or empty string if no changes</returns>
        public string ComputeDiff(string filePath, string before, string after, int contextLines = ContextLines)
        {
            // Lines are compared without their line endings; a CRLF file used to leave "\r" on every
            // line, producing "\r\r\n" in the patch and patches that could not be applied back.
            var beforeLines = SplitLines(before, out _, out _);
            var afterLines = SplitLines(after, out _, out _);

            var hunks = ComputeHunks(beforeLines.ToArray(), afterLines.ToArray(), contextLines);

            if (hunks.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();

            // Unified diff header ("\n" only, independent of the OS)
            sb.Append($"--- a/{filePath}\n");
            sb.Append($"+++ b/{filePath}\n");

            foreach (var hunk in hunks)
            {
                sb.Append($"@@ -{hunk.BeforeStart},{hunk.BeforeCount} +{hunk.AfterStart},{hunk.AfterCount} @@\n");

                foreach (var line in hunk.Lines)
                {
                    sb.Append(line).Append('\n');
                }
            }

            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// Applies a unified diff patch to content (reverse = undo it).
        /// Every context and removed line is verified; if the content doesn't match the patch
        /// (the file was edited since), this throws instead of producing a corrupted file.
        /// The content's line endings and final newline are preserved.
        /// </summary>
        /// <exception cref="PatchConflictException">The patch does not match the content.</exception>
        public string ApplyPatch(string original, string patch, bool reverse = false)
        {
            var lines = SplitLines(original, out var eol, out var endsWithNewline);

            var patchLines = patch.Replace("\r\n", "\n").Split('\n');
            var hunks = ParseHunks(patchLines, reverse);
            if (hunks.Count == 0)
                throw new PatchConflictException("The patch contains no hunks.");

            // Apply hunks from bottom to top to preserve line numbers
            foreach (var hunk in hunks.OrderByDescending(h => h.BeforeStart))
            {
                ApplyHunk(lines, hunk, reverse);
            }

            return string.Join(eol, lines) + (endsWithNewline && lines.Count > 0 ? eol : string.Empty);
        }

        /// <summary>Splits text into lines without line terminators; reports the EOL style and final newline.</summary>
        private static List<string> SplitLines(string text, out string eol, out bool endsWithNewline)
        {
            eol = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            endsWithNewline = lines.Count > 0 && lines[^1].Length == 0;
            if (endsWithNewline) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 1 && lines[0].Length == 0) lines.Clear();   // empty file
            return lines;
        }

        private List<DiffHunk> ComputeHunks(string[] before, string[] after, int context)
        {
            var edits = ComputeLcsEdits(before, after);
            if (edits.Count == 0)
                return new List<DiffHunk>();

            // Group edits into hunks with context
            var hunks = new List<DiffHunk>();
            var currentHunkEdits = new List<Edit>();

            for (int i = 0; i < edits.Count; i++)
            {
                if (currentHunkEdits.Count == 0)
                {
                    currentHunkEdits.Add(edits[i]);
                }
                else
                {
                    var prevEnd = currentHunkEdits[^1].BeforeLine;
                    var currentStart = edits[i].BeforeLine;
                    if (currentStart - prevEnd <= context * 2 + 1)
                    {
                        currentHunkEdits.Add(edits[i]);
                    }
                    else
                    {
                        hunks.Add(BuildHunk(before, after, currentHunkEdits, context));
                        currentHunkEdits = new List<Edit> { edits[i] };
                    }
                }
            }

            if (currentHunkEdits.Count > 0)
            {
                hunks.Add(BuildHunk(before, after, currentHunkEdits, context));
            }

            return hunks;
        }

        private DiffHunk BuildHunk(string[] before, string[] after, List<Edit> edits, int context)
        {
            var beforeStart = Math.Max(1, edits[0].BeforeLine - context);
            var beforeEnd = Math.Min(before.Length, edits[^1].BeforeEnd + context);
            var afterStart = Math.Max(1, edits[0].AfterLine - context);
            var afterEnd = Math.Min(after.Length, edits[^1].AfterEnd + context);

            var lines = new List<string>();
            var beforeIdx = beforeStart;
            var afterIdx = afterStart;
            var editIdx = 0;

            while (beforeIdx <= beforeEnd || afterIdx <= afterEnd)
            {
                if (editIdx < edits.Count)
                {
                    var edit = edits[editIdx];

                    // Context lines before the edit
                    while (beforeIdx < edit.BeforeLine && beforeIdx <= beforeEnd)
                    {
                        lines.Add($" {before[beforeIdx - 1]}");
                        beforeIdx++;
                        afterIdx++;
                    }

                    // Deleted lines (present in before, not in after)
                    while (beforeIdx <= edit.BeforeEnd && beforeIdx <= beforeEnd)
                    {
                        lines.Add($"-{before[beforeIdx - 1]}");
                        beforeIdx++;
                    }

                    // Added lines (present in after, not in before)
                    while (afterIdx <= edit.AfterEnd && afterIdx <= afterEnd)
                    {
                        lines.Add($"+{after[afterIdx - 1]}");
                        afterIdx++;
                    }

                    editIdx++;
                }
                else
                {
                    // Remaining context
                    if (beforeIdx <= beforeEnd && afterIdx <= afterEnd)
                    {
                        lines.Add($" {before[beforeIdx - 1]}");
                        beforeIdx++;
                        afterIdx++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return new DiffHunk
            {
                BeforeStart = beforeStart,
                BeforeCount = beforeEnd - beforeStart + 1,
                AfterStart = afterStart,
                AfterCount = afterEnd - afterStart + 1,
                Lines = lines
            };
        }

        /// <summary>
        /// Simple LCS-based edit detection.
        /// Finds minimal set of line-level insertions and deletions.
        /// </summary>
        private List<Edit> ComputeLcsEdits(string[] before, string[] after)
        {
            var edits = new List<Edit>();

            // Build LCS table
            var m = before.Length;
            var n = after.Length;
            var dp = new int[m + 1, n + 1];

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    if (before[i - 1] == after[j - 1])
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                    else
                        dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }

            // Backtrack to find edits
            var i2 = m;
            var j2 = n;

            while (i2 > 0 || j2 > 0)
            {
                if (i2 > 0 && j2 > 0 && before[i2 - 1] == after[j2 - 1])
                {
                    // Same line, no edit
                    i2--;
                    j2--;
                }
                else if (j2 > 0 && (i2 == 0 || dp[i2, j2 - 1] >= dp[i2 - 1, j2]))
                {
                    // Insertion from after
                    var insertEnd = j2;
                    while (j2 > 0 && (i2 == 0 || dp[i2, j2 - 1] >= dp[i2 - 1, j2]) &&
                           (i2 > 0 && j2 > 0 && before[i2 - 1] != after[j2 - 1]) || j2 > 0)
                    {
                        j2--;
                        if (i2 > 0 && j2 > 0 && before[i2 - 1] == after[j2 - 1])
                            break;
                        if (j2 == 0) break;
                        if (i2 > 0 && dp[i2 - 1, j2] > dp[i2, j2 - 1])
                            break;
                    }

                    edits.Insert(0, new Edit
                    {
                        BeforeLine = i2 + 1,
                        BeforeEnd = i2,
                        AfterLine = j2 + 1,
                        AfterEnd = insertEnd,
                        Type = EditType.Insert
                    });
                }
                else
                {
                    // Deletion from before
                    var deleteEnd = i2;
                    while (i2 > 0)
                    {
                        i2--;
                        if (i2 > 0 && j2 > 0 && before[i2 - 1] == after[j2 - 1])
                            break;
                        if (j2 > 0 && dp[i2, j2 - 1] > dp[i2 - 1, j2])
                            break;
                    }

                    edits.Insert(0, new Edit
                    {
                        BeforeLine = i2 + 1,
                        BeforeEnd = deleteEnd,
                        AfterLine = j2 + 1,
                        AfterEnd = j2,
                        Type = EditType.Delete
                    });
                }
            }

            // Merge adjacent insert/delete pairs into replaces
            var merged = new List<Edit>();
            for (int i = 0; i < edits.Count; i++)
            {
                if (i + 1 < edits.Count &&
                    edits[i].Type == EditType.Delete &&
                    edits[i + 1].Type == EditType.Insert &&
                    edits[i].BeforeEnd + 1 == edits[i + 1].BeforeLine)
                {
                    merged.Add(new Edit
                    {
                        BeforeLine = edits[i].BeforeLine,
                        BeforeEnd = edits[i].BeforeEnd,
                        AfterLine = edits[i + 1].AfterLine,
                        AfterEnd = edits[i + 1].AfterEnd,
                        Type = EditType.Replace
                    });
                    i++;
                }
                else
                {
                    merged.Add(edits[i]);
                }
            }

            return merged;
        }

        // ── Patch application helpers ──────────────────────────

        private List<DiffHunk> ParseHunks(string[] patchLines, bool reverse)
        {
            var hunks = new List<DiffHunk>();
            DiffHunk? currentHunk = null;

            foreach (var line in patchLines)
            {
                if (line.StartsWith("@@"))
                {
                    if (currentHunk != null)
                        hunks.Add(currentHunk);

                    currentHunk = ParseHunkHeader(line, reverse);
                }
                else if (currentHunk != null)
                {
                    currentHunk.Lines.Add(line);
                }
            }

            if (currentHunk != null)
                hunks.Add(currentHunk);

            return hunks;
        }

        private DiffHunk ParseHunkHeader(string header, bool reverse)
        {
            // Format: @@ -start,count +start,count @@
            var parts = header.Split(' ');
            var beforePart = parts[1].TrimStart('-');
            var afterPart = parts[2].TrimStart('+');

            var beforeStart = int.Parse(beforePart.Split(',')[0]);
            var beforeCount = int.Parse(beforePart.Split(',')[1]);
            var afterStart = int.Parse(afterPart.Split(',')[0]);
            var afterCount = int.Parse(afterPart.Split(',')[1]);

            return new DiffHunk
            {
                BeforeStart = reverse ? afterStart : beforeStart,
                BeforeCount = reverse ? afterCount : beforeCount,
                AfterStart = reverse ? beforeStart : afterStart,
                AfterCount = reverse ? beforeCount : afterCount,
                Lines = new List<string>()
            };
        }

        private static void ApplyHunk(List<string> lines, DiffHunk hunk, bool reverse)
        {
            // A hunk that removes nothing and has no context starts AFTER line N (count 0 → insert at N)
            var hasOldLines = hunk.Lines.Any(l => l.Length > 0 && (l[0] == ' ' || l[0] == (reverse ? '+' : '-')));
            var idx = hasOldLines ? hunk.BeforeStart - 1 : Math.Min(hunk.BeforeStart, lines.Count);   // 0-based

            foreach (var hunkLine in hunk.Lines)
            {
                if (hunkLine.Length == 0)
                    continue;   // trailing blank from splitting the patch text

                // Reverse = undo: added lines must be removed and removed lines re-added
                var op = hunkLine[0];
                if (reverse && op == '+') op = '-';
                else if (reverse && op == '-') op = '+';

                var content = hunkLine[1..];

                switch (op)
                {
                    case ' ':
                        ExpectLine(lines, idx, content, "context");
                        idx++;
                        break;
                    case '-':
                        ExpectLine(lines, idx, content, "removed");
                        lines.RemoveAt(idx);
                        break;
                    case '+':
                        lines.Insert(idx, content);
                        idx++;
                        break;
                }
            }
        }

        private static void ExpectLine(List<string> lines, int idx, string expected, string kind)
        {
            if (idx >= lines.Count || lines[idx].TrimEnd() != expected.TrimEnd())
            {
                var actual = idx < lines.Count ? lines[idx] : "(end of file)";
                throw new PatchConflictException(
                    $"The file changed since this edit: expected {kind} line {idx + 1} to be \"{expected.Trim()}\" but it is \"{actual.Trim()}\".");
            }
        }

        // ── Internal types ─────────────────────────────────────

        private class Edit
        {
            public int BeforeLine { get; set; }
            public int BeforeEnd { get; set; }
            public int AfterLine { get; set; }
            public int AfterEnd { get; set; }
            public EditType Type { get; set; }
        }

        private enum EditType
        {
            Insert,
            Delete,
            Replace
        }

        private class DiffHunk
        {
            public int BeforeStart { get; set; }
            public int BeforeCount { get; set; }
            public int AfterStart { get; set; }
            public int AfterCount { get; set; }
            public List<string> Lines { get; set; } = new();
        }
    }

    /// <summary>A patch could not be applied because the content no longer matches it.</summary>
    public class PatchConflictException : Exception
    {
        public PatchConflictException(string message) : base(message) { }
    }
}
