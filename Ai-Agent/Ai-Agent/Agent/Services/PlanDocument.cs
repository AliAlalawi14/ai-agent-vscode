using Ai_Agent.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// A plan as a Markdown file the user can edit with any editor (Cursor-style plan files):
    /// <code>
    /// # Title
    /// ## Overview
    /// (markdown, may contain ```mermaid diagrams)
    /// ## Steps
    /// - [ ] 1. Step title
    ///   Files: `a.cs`, `b.cs`
    ///   Details...
    /// - [x] 2. Done step
    /// - [ ] 3. Current step (in progress)
    /// - [x] 4. Dropped step (skipped)
    /// </code>
    /// Parsing is tolerant of hand edits: reordered, added or removed steps, ticked boxes, plain numbered
    /// lists, extra sections (kept in the overview) and free text.
    /// </summary>
    public static partial class PlanDocument
    {
        private const string InProgressSuffix = "(in progress)";
        private const string SkippedSuffix = "(skipped)";

        public static string Render(ActivePlan plan)
        {
            var sb = new StringBuilder();
            sb.Append("# ").AppendLine(string.IsNullOrWhiteSpace(plan.Title) ? "Plan" : plan.Title.Trim());
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(plan.Summary))
            {
                sb.AppendLine("## Overview");
                sb.AppendLine();
                sb.AppendLine(plan.Summary.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("## Steps");
            sb.AppendLine();
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                sb.AppendLine(StepLine(i + 1, plan.Steps[i].Title, plan.Steps[i].Status));
                var step = plan.Steps[i];
                if (step.Files.Count > 0)
                    sb.Append("  Files: ").AppendLine(string.Join(", ", step.Files.Select(f => $"`{f}`")));
                foreach (var line in step.Details.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0))
                    sb.Append("  ").AppendLine(line.Trim());
            }
            return sb.ToString().Replace("\r\n", "\n");   // plan files always use \n, whatever the OS
        }

        /// <summary>One checklist line: the checkbox and suffix encode the status.</summary>
        public static string StepLine(int number, string title, string status)
        {
            var box = status is "done" or "skipped" ? "[x]" : "[ ]";
            var suffix = status switch
            {
                "in_progress" => " " + InProgressSuffix,
                "skipped" => " " + SkippedSuffix,
                _ => string.Empty
            };
            return $"- {box} {number}. {title.Trim()}{suffix}";
        }

        public static ActivePlan Parse(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var plan = new ActivePlan();
            var summary = new StringBuilder();
            var inSteps = false;
            PlanStep? current = null;
            var details = new List<string>();

            void FinishStep()
            {
                if (current == null) return;
                current.Details = string.Join("\n", details).Trim();
                plan.Steps.Add(current);
                current = null;
                details.Clear();
            }

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();

                if (string.IsNullOrEmpty(plan.Title) && H1().Match(line) is { Success: true } h1)
                {
                    plan.Title = h1.Groups[1].Value.Trim();
                    continue;
                }

                if (H2().Match(line) is { Success: true } h2)
                {
                    FinishStep();
                    var heading = h2.Groups[1].Value.Trim();
                    inSteps = StepHeadings().IsMatch(heading);
                    // Overview's own heading is implied; any other section the user added stays in the overview
                    if (!inSteps && !heading.Equals("Overview", StringComparison.OrdinalIgnoreCase) &&
                        !heading.Equals("Summary", StringComparison.OrdinalIgnoreCase))
                        summary.Append(line).Append('\n');
                    continue;
                }

                if (!inSteps)
                {
                    summary.Append(raw.TrimEnd()).Append('\n');
                    continue;
                }

                if (TryParseStepStart(line, out var step))
                {
                    FinishStep();
                    current = step;
                    continue;
                }

                if (current == null) continue;   // text between the heading and the first step
                var text = line.Trim();
                if (text.Length == 0) continue;
                if (FilesLine().Match(text) is { Success: true } files)
                {
                    current.Files = SplitFiles(files.Groups[1].Value);
                    continue;
                }
                details.Add(text);
            }
            FinishStep();

            plan.Title = string.IsNullOrWhiteSpace(plan.Title) ? "Plan" : plan.Title;
            plan.Summary = summary.ToString().Trim();
            return plan;
        }

        /// <summary>Index (0-based) of each step's first line in the file, in order; used to edit a single line.</summary>
        public static List<int> StepLineIndexes(string[] lines)
        {
            var result = new List<int>();
            var inSteps = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd();
                if (H2().Match(line) is { Success: true } h2)
                {
                    inSteps = StepHeadings().IsMatch(h2.Groups[1].Value.Trim());
                    continue;
                }
                if (inSteps && TryParseStepStart(line, out _)) result.Add(i);
            }
            return result;
        }

        private static bool TryParseStepStart(string line, out PlanStep step)
        {
            step = new PlanStep();
            string title;
            var status = "pending";

            if (Checkbox().Match(line) is { Success: true } box)
            {
                title = box.Groups[2].Value;
                if (box.Groups[1].Value is "x" or "X") status = "done";
            }
            else if (Numbered().Match(line) is { Success: true } numbered)
            {
                title = numbered.Groups[1].Value;
            }
            else
            {
                return false;
            }

            title = LeadingNumber().Replace(title.Trim(), string.Empty).Trim();
            if (title.EndsWith(InProgressSuffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^InProgressSuffix.Length].TrimEnd();
                if (status != "done") status = "in_progress";
            }
            else if (title.EndsWith(SkippedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^SkippedSuffix.Length].TrimEnd();
                status = "skipped";
            }
            if (title.Length == 0) return false;

            step.Title = title;
            step.Status = status;
            return true;
        }

        private static List<string> SplitFiles(string value) =>
            value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim('`', ' '))
                .Where(f => f.Length > 0)
                .ToList();

        [GeneratedRegex(@"^#\s+(.+)$")]
        private static partial Regex H1();

        [GeneratedRegex(@"^##\s+(.+)$")]
        private static partial Regex H2();

        [GeneratedRegex(@"^(steps|to-?dos|tasks|checklist)$", RegexOptions.IgnoreCase)]
        private static partial Regex StepHeadings();

        // Steps start at the left margin (at most one space): an indented "1. ..." is a detail of the step above
        [GeneratedRegex(@"^ ?[-*+]\s+\[([ xX])\]\s+(.*)$")]
        private static partial Regex Checkbox();

        [GeneratedRegex(@"^ ?\d+[.)]\s+(.*)$")]
        private static partial Regex Numbered();

        [GeneratedRegex(@"^\d+[.)]\s*")]
        private static partial Regex LeadingNumber();

        [GeneratedRegex(@"^files?\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
        private static partial Regex FilesLine();
    }
}
