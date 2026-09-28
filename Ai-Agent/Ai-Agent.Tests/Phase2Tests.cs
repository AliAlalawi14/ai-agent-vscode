using Ai_Agent.Agent.Services;
using Ai_Agent.Tools.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ai_Agent.Tests
{
    /// <summary>Phase 2: every service works on the request's workspace; project context is cached and invalidated.</summary>
    public class Phase2Tests
    {
        private static ProjectContextService ContextService() =>
            new(NullLogger<ProjectContextService>.Instance, new ProjectIndexer(NullLogger<ProjectIndexer>.Instance));

        private static ChangeTracker Tracker() => new(new UnifiedDiffService(), NullLogger<ChangeTracker>.Instance);

        [Fact]
        public void Project_context_describes_the_requested_workspace_only()
        {
            using var a = new TempWorkspace();
            using var b = new TempWorkspace();
            a.Write("Calculator.cs", "class Calculator { int Add(int x, int y) => x + y; }");
            b.Write("Inventory.cs", "class Inventory { int Count() => 0; }");
            var service = ContextService();

            var classesA = service.GatherContext(a.Root).ProjectIndex!.Classes.Select(c => c.Name).ToList();
            var classesB = service.GatherContext(b.Root).ProjectIndex!.Classes.Select(c => c.Name).ToList();

            Assert.Equal(new[] { "Calculator" }, classesA);
            Assert.Equal(new[] { "Inventory" }, classesB);
        }

        [Fact]
        public void Project_context_is_cached_until_invalidated()
        {
            using var ws = new TempWorkspace();
            ws.Write("A.cs", "class A { }");
            var service = ContextService();

            var first = service.GatherContext(ws.Root);
            ws.Write("B.cs", "class B { }");
            Assert.Same(first, service.GatherContext(ws.Root));   // cache hit: no rescan

            service.Invalidate(ws.Root);
            var rebuilt = service.GatherContext(ws.Root);
            Assert.NotSame(first, rebuilt);
            Assert.Contains(rebuilt.ProjectIndex!.Classes, c => c.Name == "B");
        }

        [Fact]
        public void Index_never_enters_node_modules_bin_or_obj()
        {
            using var ws = new TempWorkspace();
            ws.Write("src/Real.ts", "export class Real {}");
            ws.Write("node_modules/lib/Vendor.js", "class Vendor {}");
            ws.Write("bin/Debug/Gen.cs", "class Generated {}");

            var names = ContextService().GatherContext(ws.Root).ProjectIndex!.Classes.Select(c => c.Name).ToList();

            Assert.Contains("Real", names);
            Assert.DoesNotContain("Vendor", names);
            Assert.DoesNotContain("Generated", names);
        }

        [Fact]
        public async Task An_agent_edit_refreshes_the_cached_code_map()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("Calc.cs", "class Calc\n{\n    int A() => 1;\n}\n");
            var context = h.Services.GetRequiredService<ProjectContextService>();
            Assert.DoesNotContain("Cube", context.GatherContext(h.Workspace.Root).ProjectIndex!.Classes.Single().Methods);

            h.Llm.Call("edit_file", new { path = "Calc.cs", old_string = "int A() => 1;", new_string = "int A() => 1;\n    int Cube(int x) => x * x * x;" })
                 .Text("done");
            await h.RunAsync("add Cube", "agent", approve: true);

            Assert.Contains("Cube", context.GatherContext(h.Workspace.Root).ProjectIndex!.Classes.Single().Methods);
        }

        [Fact]
        public async Task Changes_are_logged_per_workspace_and_found_only_there()
        {
            using var a = new TempWorkspace();
            using var b = new TempWorkspace();
            var tracker = Tracker();

            var change = await tracker.RecordChangeAsync(a.Root, "s1", 1, "C.cs", "edit_file", "old\n", "new\n", isNewFile: false);

            Assert.NotNull(await tracker.GetChangeAsync(a.Root, change.ChangeId));
            Assert.Null(await tracker.GetChangeAsync(b.Root, change.ChangeId));
            Assert.True(File.Exists(Path.Combine(a.Root, ChangeTracker.LogFileName)));
            Assert.False(File.Exists(Path.Combine(b.Root, ChangeTracker.LogFileName)));
        }

        [Fact]
        public async Task A_change_is_never_reverted_into_another_workspace()
        {
            using var a = new TempWorkspace();
            using var b = new TempWorkspace();
            a.Write("C.cs", "new\n");
            b.Write("C.cs", "b's own content\n");
            var tracker = Tracker();
            var change = await tracker.RecordChangeAsync(a.Root, "s1", 1, "C.cs", "edit_file", "old\n", "new\n", isNewFile: false);

            var (ok, error) = await tracker.RevertFileAsync(change, b.Root);

            Assert.False(ok);
            Assert.Contains("different workspace", error);
            Assert.Equal("b's own content\n", b.Read("C.cs"));
        }
    }
}
