using Ai_Agent.Agent.Services;
using Ai_Agent.Models;
using Ai_Agent.Tools.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Ai_Agent.Tests
{
    /// <summary>Cursor-style plan mode: clarifying questions, the plan as an editable file, progress and revisions in it.</summary>
    public class PlanModeTests
    {
        private static ActivePlan Sample() => new()
        {
            Title = "Orders API",
            Summary = "### Goal\nOrders for customers.\n\n```mermaid\nflowchart LR\n  A[Client] --> B[OrdersController]\n```",
            Steps = new()
            {
                new() { Title = "Create the Order model", Files = new() { "Models/Order.cs" }, Details = "Id, CustomerId, Total (decimal).", Status = "done" },
                new() { Title = "Add OrdersController", Files = new() { "Controllers/OrdersController.cs", "Program.cs" }, Details = "GET/POST /api/orders.\n1. validate totals", Status = "in_progress" },
                new() { Title = "Write tests", Status = "skipped" },
                new() { Title = "Update README" }
            }
        };

        private static PlanStore Store() => new(NullLogger<PlanStore>.Instance);

        [Fact]
        public void Render_then_parse_gives_back_the_same_plan()
        {
            var plan = Sample();

            var parsed = PlanDocument.Parse(PlanDocument.Render(plan));

            Assert.Equal(plan.Title, parsed.Title);
            Assert.Equal(plan.Summary, parsed.Summary);
            Assert.Equal(plan.Steps.Select(s => (s.Title, s.Status, string.Join("|", s.Files), s.Details)),
                         parsed.Steps.Select(s => (s.Title, s.Status, string.Join("|", s.Files), s.Details)));
        }

        [Fact]
        public void Hand_edited_plans_are_read_the_way_the_user_meant()
        {
            const string edited = """
                # Orders API (v2)

                ## Overview
                Keep it small.

                ## Notes from me
                Use SQLite, not Postgres.

                ## Steps
                Some text the user typed above the list.

                - [x] 1. Create the Order model
                - [ ] 2. Name the class ProductItem instead
                  Files: `Models/ProductItem.cs`
                  Keep the namespace.
                * [ ] Add a brand new step without a number
                3. A plain numbered step
                - [X] 4. Write tests (skipped)
                """;

            var plan = PlanDocument.Parse(edited);

            Assert.Equal("Orders API (v2)", plan.Title);
            Assert.Contains("Use SQLite", plan.Summary);          // extra sections are kept in the overview
            Assert.Equal(5, plan.Steps.Count);
            Assert.Equal("done", plan.Steps[0].Status);
            Assert.Equal("Name the class ProductItem instead", plan.Steps[1].Title);
            Assert.Equal(new[] { "Models/ProductItem.cs" }, plan.Steps[1].Files);
            Assert.Equal("Keep the namespace.", plan.Steps[1].Details);
            Assert.Equal("Add a brand new step without a number", plan.Steps[2].Title);
            Assert.Equal("A plain numbered step", plan.Steps[3].Title);
            Assert.Equal("skipped", plan.Steps[4].Status);
        }

        [Fact]
        public async Task Plans_are_saved_under_ai_plans_and_never_overwrite_another_plan()
        {
            using var ws = new TempWorkspace();
            var store = Store();

            var first = await store.SaveAsync(ws.Root, Sample());
            var second = await store.SaveAsync(ws.Root, Sample());

            Assert.Equal(".ai/plans/orders-api.plan.md", first);
            Assert.Equal(".ai/plans/orders-api-2.plan.md", second);
            Assert.Contains("- [x] 1. Create the Order model", ws.Read(first));
        }

        [Fact]
        public async Task Ticking_a_step_rewrites_only_that_line()
        {
            using var ws = new TempWorkspace();
            var store = Store();
            var path = await store.SaveAsync(ws.Root, Sample());
            var withUserNote = ws.Read(path).Replace("## Steps", "My own note: keep it simple.\n\n## Steps");
            ws.Write(path, withUserNote);

            Assert.True(await store.SetStepStatusAsync(ws.Root, path, 4, "done"));

            var after = ws.Read(path);
            Assert.Contains("- [x] 4. Update README", after);
            Assert.Contains("My own note: keep it simple.", after);
            var changed = withUserNote.Split('\n').Zip(after.Split('\n')).Count(p => p.First != p.Second);
            Assert.Equal(1, changed);
        }

        [Fact]
        public async Task A_revision_updates_the_same_file_and_keeps_progress()
        {
            using var ws = new TempWorkspace();
            var store = Store();
            var path = await store.SaveAsync(ws.Root, Sample());

            var revised = new ActivePlan
            {
                Title = "Orders API",
                Summary = "Now with SQLite.",
                Steps = new()
                {
                    new() { Title = "Create the Order model" },            // done before: stays done
                    new() { Title = "Store orders in SQLite" },            // new
                    new() { Title = "Update README" }
                }
            };
            var savedPath = await store.SaveAsync(ws.Root, revised, existingPath: path);

            Assert.Equal(path, savedPath);
            var plan = await store.LoadAsync(ws.Root, path);
            Assert.Equal(new[] { "done", "pending", "pending" }, plan!.Steps.Select(s => s.Status));
            Assert.Contains("SQLite", plan.Summary);
        }

        [Theory]
        [InlineData("Program.cs")]
        [InlineData(".ai/plans/../../Program.cs")]
        [InlineData(".ai/plans/sub/x.plan.md")]
        [InlineData(".ai/plans/x.md")]
        [InlineData("../outside/.ai/plans/x.plan.md")]
        public void Only_plan_files_inside_ai_plans_are_accepted(string path)
        {
            using var ws = new TempWorkspace();
            Assert.Null(PlanStore.Resolve(ws.Root, path));
        }

        [Fact]
        public void Questions_need_options_and_at_most_four()
        {
            var ok = AskQuestionsTool.Parse(new() { ["questions"] = "[{\"question\":\"Storage?\",\"options\":[\"SQL\",\"Files\"]}]" }, out var e1);
            var noOptions = AskQuestionsTool.Parse(new() { ["questions"] = "[{\"question\":\"Storage?\",\"options\":[\"SQL\"]}]" }, out var e2);
            var five = AskQuestionsTool.Parse(new()
            {
                ["questions"] = JsonSerializer.Serialize(Enumerable.Range(1, 5).Select(i => new { question = $"Q{i}", options = new[] { "a", "b" } }))
            }, out var e3);

            Assert.NotNull(ok);
            Assert.Null(e1);
            Assert.Null(noOptions);
            Assert.Contains("2 options", e2);
            Assert.Null(five);
            Assert.Contains("at most", e3);
        }

        [Fact]
        public async Task Plan_mode_asks_questions_and_the_run_ends_there()
        {
            using var h = new AgentHarness();
            h.Llm.Call("ask_questions", new { questions = new[] { new { question = "Email or push?", options = new[] { "Email", "Push" } } } })
                 .Text("should never be requested");

            var events = await h.RunAsync("plan a notifications feature", "plan");

            var questions = events.Single(e => e.StartsWith("[QUESTIONS_EVENT]"));
            Assert.Contains("Email or push?", questions);
            Assert.Single(h.Llm.Requests);   // stopped right after asking
            Assert.Contains("ask_questions", h.Llm.OfferedTools[0]);
        }

        [Fact]
        public async Task A_submitted_plan_is_written_to_a_file_and_the_event_carries_its_path()
        {
            using var h = new AgentHarness();
            h.Llm.Call("submit_plan", new
            {
                title = "Orders",
                summary = "### Goal\nOrders",
                steps = new[] { new { title = "Model", files = new[] { "Order.cs" }, details = "Create Order" } }
            });

            var events = await h.RunAsync("plan orders", "plan");

            var planEvent = events.Single(e => e.StartsWith("[PLAN_EVENT]"));
            Assert.Contains("\"path\":\".ai/plans/orders.plan.md\"", planEvent);
            Assert.Contains("- [ ] 1. Model", h.Workspace.Read(".ai/plans/orders.plan.md"));
        }

        [Fact]
        public async Task Building_reads_the_plan_file_so_the_users_edits_win()
        {
            using var h = new AgentHarness();
            h.Workspace.Write(".ai/plans/shop.plan.md", "# Shop\n\n## Steps\n\n- [ ] 1. Create class ProductItem (renamed by the user)\n");
            h.Llm.Call("update_plan", new { step = 1, status = "done" }).Text("done");

            await h.Agent.RunStreamAsync("start", h.Workspace.Root, mode: "agent",
                    activePlan: new ActivePlan { Title = "Old", Steps = new() { new() { Title = "Create class Product" } } },
                    planPath: ".ai/plans/shop.plan.md")
                .ToListAsync();

            var userMessage = h.Llm.Requests[0].Last(m => m.Role == "user").Content;
            Assert.Contains("ProductItem (renamed by the user)", userMessage);   // the file, not the stale chat copy
            Assert.DoesNotContain("Create class Product\n", userMessage);
            Assert.Contains("- [x] 1. Create class ProductItem", h.Workspace.Read(".ai/plans/shop.plan.md"));   // progress in the file
        }
    }
}
