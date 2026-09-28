using Ai_Agent.Models;
using Ai_Agent.Tools.Interfaces;
using System.Text.Json;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Plan mode: the agent's final action. The plan is shown to the user as a checklist card
    /// and sent back with later requests as the ACTIVE PLAN.
    /// </summary>
    public class SubmitPlanTool : ITool
    {
        public string Name => "submit_plan";
        public string Description =>
            "Submit your implementation plan to the user (Plan mode). Call exactly once, after investigating. " +
            "It is saved as an editable Markdown file; the user reviews or edits it, then builds it step by step.";

        public Dictionary<string, string> Parameters => new()
        {
            { "title", "Short title of the plan, e.g. 'E-commerce API: Category and Product'" },
            { "summary", "The plan explained for the user, in Markdown with short headings: ### Goal, ### Approach (how it fits the existing code), " +
                         "### Design (data model with fields/types, API endpoints with routes, storage), ### Decisions & assumptions, ### Open questions. " +
                         "When a diagram makes the design clearer, include ONE ```mermaid block (flowchart, sequenceDiagram or erDiagram). " +
                         "Concrete and specific to this project; roughly 150-400 words." },
            { "steps", "JSON array of 3-8 steps, in order. Each step: {\"title\": \"...\", \"files\": [\"relative/path.cs\"], " +
                       "\"details\": \"1-3 sentences naming exactly what to create or change: classes, properties, methods, routes\"}" },
            { "revise", "[Optional] 'true' when this is the ACTIVE PLAN with the user's requested changes applied (the same plan file " +
                        "is updated); omit for a new, unrelated plan" }
        };

        public bool IsReadOnly => true;

        // steps is a real JSON array of objects, not a string holding JSON
        public IReadOnlyDictionary<string, object> ParameterSchemas { get; } = new Dictionary<string, object>
        {
            ["steps"] = ParamSchema.ArrayOf(new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["title"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["files"] = ParamSchema.ArrayOf(new Dictionary<string, object> { ["type"] = "string" }),
                    ["details"] = new Dictionary<string, object> { ["type"] = "string" }
                },
                ["required"] = new[] { "title", "details" }
            }),
            ["revise"] = ParamSchema.Boolean()
        };

        /// <summary>True when the model says this revises the conversation's current plan.</summary>
        public static bool IsRevision(Dictionary<string, string> parameters) =>
            string.Equals(parameters.GetValueOrDefault("revise"), "true", StringComparison.OrdinalIgnoreCase);

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            var plan = Parse(parameters, out var error);
            return Task.FromResult(plan == null
                ? $"ERROR: {error}"
                : $"PLAN SUBMITTED: \"{plan.Title}\" with {plan.Steps.Count} steps. The user will review it; stop here.");
        }

        /// <summary>Parses the tool arguments into a plan (also used by the agent loop to emit the plan event).</summary>
        public static ActivePlan? Parse(Dictionary<string, string> parameters, out string? error)
        {
            error = null;
            var title = parameters.GetValueOrDefault("title")?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                error = "Missing 'title'.";
                return null;
            }

            if (!parameters.TryGetValue("steps", out var stepsJson) || string.IsNullOrWhiteSpace(stepsJson))
            {
                error = "Missing 'steps' (a JSON array).";
                return null;
            }

            var steps = new List<PlanStep>();
            try
            {
                using var doc = JsonDocument.Parse(stepsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = "'steps' must be a JSON array.";
                    return null;
                }

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    // Accept plain strings too: ["Create models", ...]
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        steps.Add(new PlanStep { Title = item.GetString() ?? string.Empty });
                        continue;
                    }
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var step = new PlanStep
                    {
                        Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                        Details = item.TryGetProperty("details", out var d) ? d.GetString() ?? string.Empty : string.Empty
                    };
                    if (item.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array)
                        step.Files = f.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList();
                    if (step.Title.Length > 0) steps.Add(step);
                }
            }
            catch (JsonException ex)
            {
                error = $"'steps' is not valid JSON: {ex.Message}";
                return null;
            }

            if (steps.Count == 0)
            {
                error = "The plan has no steps.";
                return null;
            }

            return new ActivePlan
            {
                Title = title,
                Summary = parameters.GetValueOrDefault("summary")?.Trim() ?? string.Empty,
                Steps = steps
            };
        }
    }

    /// <summary>
    /// Plan mode, before planning (Cursor-style): asks the user 1-4 multiple-choice questions whose answers change the plan.
    /// The run ends there; the answers arrive as the next user message.
    /// </summary>
    public class AskQuestionsTool : ITool
    {
        public const int MaxQuestions = 4;

        public string Name => "ask_questions";
        public string Description =>
            "Plan mode only: ask the user 1-4 short multiple-choice questions BEFORE writing the plan, only when the request leaves " +
            "open WHAT to build (several quite different features would match it). Not for HOW questions that have a sensible " +
            "default (storage, framework, naming): decide those and state them in the plan. Offer 2-5 concrete options per question. " +
            "The user answers in the next message; then call submit_plan.";

        public Dictionary<string, string> Parameters => new()
        {
            { "questions", "JSON array of 1-4 questions: {\"question\": \"...\", \"options\": [\"...\", \"...\"], \"multiple\": false}" }
        };

        public bool IsReadOnly => true;

        public IReadOnlyDictionary<string, object> ParameterSchemas { get; } = new Dictionary<string, object>
        {
            ["questions"] = ParamSchema.ArrayOf(new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["question"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["options"] = ParamSchema.ArrayOf(new Dictionary<string, object> { ["type"] = "string" }),
                    ["multiple"] = ParamSchema.Boolean()
                },
                ["required"] = new[] { "question", "options" }
            })
        };

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            var questions = Parse(parameters, out var error);
            return Task.FromResult(questions == null
                ? $"ERROR: {error}"
                : $"QUESTIONS SENT: {questions.Count} question(s). Stop here; the user's answers arrive in the next message.");
        }

        public static List<PlanQuestion>? Parse(Dictionary<string, string> parameters, out string? error)
        {
            error = null;
            if (!parameters.TryGetValue("questions", out var json) || string.IsNullOrWhiteSpace(json))
            {
                error = "Missing 'questions' (a JSON array).";
                return null;
            }

            var questions = new List<PlanQuestion>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = "'questions' must be a JSON array.";
                    return null;
                }
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var text = item.TryGetProperty("question", out var q) ? q.GetString()?.Trim() ?? "" : "";
                    if (text.Length == 0) continue;
                    var options = item.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                        ? o.EnumerateArray().Select(x => x.GetString()?.Trim() ?? "").Where(x => x.Length > 0).Distinct().ToList()
                        : new List<string>();
                    if (options.Count < 2)
                    {
                        error = $"Question \"{text}\" needs at least 2 options.";
                        return null;
                    }
                    questions.Add(new PlanQuestion
                    {
                        Question = text,
                        Options = options.Take(5).ToList(),
                        Multiple = item.TryGetProperty("multiple", out var m) && m.ValueKind == JsonValueKind.True
                    });
                }
            }
            catch (JsonException ex)
            {
                error = $"'questions' is not valid JSON: {ex.Message}";
                return null;
            }

            if (questions.Count == 0)
            {
                error = "No questions given.";
                return null;
            }
            if (questions.Count > MaxQuestions)
            {
                error = $"Ask at most {MaxQuestions} questions; keep only the ones that change the plan.";
                return null;
            }
            return questions;
        }
    }

    public class PlanQuestion
    {
        public string Question { get; set; } = string.Empty;
        public List<string> Options { get; set; } = new();
        public bool Multiple { get; set; }
    }

    /// <summary>Agent mode: ticks the user's plan checklist as steps start and finish.</summary>
    public class UpdatePlanTool : ITool
    {
        private static readonly HashSet<string> Statuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "in_progress", "done", "skipped"
        };

        public string Name => "update_plan";
        public string Description =>
            "Update a step of the ACTIVE PLAN: call with status 'in_progress' when you start a step and 'done' when it is finished.";

        public Dictionary<string, string> Parameters => new()
        {
            { "step", "Step number (1-based) from the ACTIVE PLAN" },
            { "status", "One of: in_progress, done, skipped" }
        };

        public bool IsReadOnly => true;

        public IReadOnlyDictionary<string, object> ParameterSchemas { get; } = new Dictionary<string, object>
        {
            ["step"] = ParamSchema.Integer(minimum: 1),
            ["status"] = ParamSchema.Enum("in_progress", "done", "skipped")
        };

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            var error = Validate(parameters, out var step, out var status);
            return Task.FromResult(error != null ? $"ERROR: {error}" : $"Plan step {step} marked {status}.");
        }

        public static string? Validate(Dictionary<string, string> parameters, out int step, out string status)
        {
            status = parameters.GetValueOrDefault("status")?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!int.TryParse(parameters.GetValueOrDefault("step"), out step) || step < 1)
                return "'step' must be a positive step number.";
            if (!Statuses.Contains(status))
                return "'status' must be in_progress, done or skipped.";
            return null;
        }
    }
}
