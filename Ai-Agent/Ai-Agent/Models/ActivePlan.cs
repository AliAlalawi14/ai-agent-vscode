namespace Ai_Agent.Models
{
    /// <summary>
    /// A plan produced in Plan mode. The extension keeps it with the conversation and sends it back
    /// with every request, so "start", "continue" or "build it" mean "implement the next step".
    /// </summary>
    public class ActivePlan
    {
        public string Title { get; set; } = string.Empty;

        /// <summary>Markdown overview: goal, approach, data model / API shape, decisions, open questions</summary>
        public string Summary { get; set; } = string.Empty;

        public List<PlanStep> Steps { get; set; } = new();

        /// <summary>Workspace-relative plan file (.ai/plans/*.plan.md); null for plans that only live in the chat</summary>
        public string? Path { get; set; }
    }

    public class PlanStep
    {
        public string Title { get; set; } = string.Empty;
        public List<string> Files { get; set; } = new();
        public string Details { get; set; } = string.Empty;

        /// <summary>pending | in_progress | done | skipped</summary>
        public string Status { get; set; } = "pending";
    }

    /// <summary>ask = read-only Q&amp;A, plan = read-only + submit_plan, agent = edits with approval, auto = auto-approved edits.</summary>
    public static class AgentModes
    {
        public const string Ask = "ask";
        public const string Plan = "plan";
        public const string Agent = "agent";
        public const string Auto = "auto";

        public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase) { Ask, Plan, Agent, Auto };

        public static string Normalize(string? mode) =>
            mode != null && All.Contains(mode) ? mode.ToLowerInvariant() : Agent;
    }
}
