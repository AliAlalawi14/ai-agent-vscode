using Ai_Agent.Agent.Services;
using Ai_Agent.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Ai_Agent.Tests
{
    public class ProjectCommandsTests
    {
        [Fact]
        public void Dotnet_builds_and_tests_only_when_a_test_project_exists()
        {
            using var ws = new TempWorkspace();
            ws.Write("App.sln", "");
            ws.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            Assert.Equal(new[] { "dotnet build" }, ProjectCommands.Detect(ws.Root).Select(c => c.Display));

            ws.Write("tests/App.Tests/App.Tests.csproj", "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" />");
            var commands = ProjectCommands.Detect(ws.Root);
            Assert.Equal(new[] { "dotnet build", "dotnet test" }, commands.Select(c => c.Display));
            Assert.Contains("--no-build", commands[1].Args);   // the build just ran
        }

        [Fact]
        public void Node_uses_the_projects_scripts_and_package_manager()
        {
            using var ws = new TempWorkspace();
            ws.Write("package.json", """{ "scripts": { "build": "tsc", "test": "vitest run" } }""");
            ws.Write("pnpm-lock.yaml", "");
            Assert.Equal(new[] { "pnpm run build", "pnpm test" }, ProjectCommands.Detect(ws.Root).Select(c => c.Display));
        }

        [Fact]
        public void Node_skips_the_default_placeholder_test_and_typechecks_TypeScript_without_a_build_script()
        {
            using var ws = new TempWorkspace();
            ws.Write("package.json", """{ "scripts": { "test": "echo \"Error: no test specified\" && exit 1" } }""");
            ws.Write("tsconfig.json", "{}");
            Assert.Equal(new[] { "npx tsc --noEmit" }, ProjectCommands.Detect(ws.Root).Select(c => c.Display));
        }

        [Theory]
        [InlineData("Cargo.toml", "cargo build,cargo test")]
        [InlineData("go.mod", "go build ./...,go test ./...")]
        [InlineData("pom.xml", "mvn test")]
        public void Other_ecosystems_are_detected(string marker, string expected)
        {
            using var ws = new TempWorkspace();
            ws.Write(marker, "");
            Assert.Equal(expected.Split(','), ProjectCommands.Detect(ws.Root).Select(c => c.Display));
        }

        [Fact]
        public void Python_runs_pytest_when_there_are_tests()
        {
            using var ws = new TempWorkspace();
            ws.Write("pyproject.toml", "");
            Assert.Empty(ProjectCommands.Detect(ws.Root));
            ws.Write("tests/test_app.py", "def test_ok(): pass");
            var pytest = Assert.Single(ProjectCommands.Detect(ws.Root));
            Assert.Equal(new[] { "-m", "pytest", "-q" }, pytest.Args);
        }

        [Fact]
        public void Configured_commands_win_over_detection()
        {
            using var ws = new TempWorkspace();
            ws.Write("package.json", """{ "scripts": { "test": "jest" } }""");
            var commands = ProjectCommands.Detect(ws.Root, new[] { "npm run lint", "npm run test:unit \"quoted arg\"" });
            Assert.Equal(new[] { "build", "test" }, commands.Select(c => c.Kind));
            Assert.Equal(new[] { "run", "test:unit", "quoted arg" }, commands[1].Args);
        }

        [Fact]
        public void An_unknown_project_has_no_commands() =>
            Assert.Empty(ProjectCommands.Detect(new TempWorkspace().Root));
    }

    public class TestSummaryParserTests
    {
        [Theory]
        [InlineData("Passed!  - Failed:     0, Passed:    12, Skipped:     1, Total:    13, Duration: 2 s - A.Tests.dll", 12, 0, 1)]
        [InlineData("Failed!  - Failed:     2, Passed:    10, Skipped:     0, Total:    12\nPassed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5", 15, 2, 0)]
        [InlineData("Tests:       1 failed, 2 skipped, 11 passed, 14 total", 11, 1, 2)]
        [InlineData("      Tests  1 failed | 11 passed | 2 skipped (14)", 11, 1, 2)]
        [InlineData("============ 1 failed, 11 passed, 2 skipped in 0.52s ============", 11, 1, 2)]
        [InlineData("===== 12 passed in 1.03s =====", 12, 0, 0)]
        [InlineData("test result: ok. 12 passed; 0 failed; 1 ignored; 0 measured\ntest result: FAILED. 3 passed; 1 failed; 0 ignored", 15, 1, 1)]
        [InlineData("[INFO] Tests run: 3, Failures: 0\nResults:\nTests run: 12, Failures: 1, Errors: 1, Skipped: 2", 8, 2, 2)]
        [InlineData("  12 passing (40ms)\n  1 failing\n  2 pending", 12, 1, 2)]
        public void Reads_the_counts_of_common_test_runners(string output, int passed, int failed, int skipped)
        {
            var counts = TestSummaryParser.Parse(output);
            Assert.NotNull(counts);
            Assert.Equal((passed, failed, skipped), (counts!.Passed, counts.Failed, counts.Skipped));
        }

        [Fact]
        public void Unknown_output_has_no_counts() => Assert.Null(TestSummaryParser.Parse("Build succeeded.\n0 Warning(s)"));
    }

    public class VerifyRunnerTests
    {
        private static VerifyCommand Node(string kind, string script) => new(kind, $"node {kind}", "node", new[] { "-e", script });

        [Fact]
        public async Task A_passing_build_and_test_report_counts()
        {
            using var ws = new TempWorkspace();
            var result = await new VerifyRunner().RunAsync(ws.Root, new[]
            {
                Node("build", "console.log('compiled')"),
                Node("test", "console.log('Tests:       12 passed, 12 total')"),
            }, CancellationToken.None);

            Assert.True(result.Passed);
            Assert.Equal("Build passed", result.Steps[0].Summary);
            Assert.Equal("12/12 tests passed", result.Steps[1].Summary);
            Assert.Equal(12, result.Tests!.Total);
        }

        [Fact]
        public async Task A_failed_build_skips_the_tests_and_keeps_the_output()
        {
            using var ws = new TempWorkspace();
            var result = await new VerifyRunner().RunAsync(ws.Root, new[]
            {
                Node("build", "console.error('error CS1002: ; expected'); process.exit(1)"),
                Node("test", "console.log('never')"),
            }, CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Equal("Build failed (exit code 1)", result.Steps[0].Summary);
            Assert.Contains("CS1002", result.Steps[0].OutputTail);
            Assert.True(result.Steps[1].Skipped);
        }

        [Fact]
        public async Task Failing_tests_are_counted()
        {
            using var ws = new TempWorkspace();
            var result = await new VerifyRunner().RunAsync(ws.Root, new[]
            {
                Node("test", "console.log('Tests:       2 failed, 10 passed, 12 total'); process.exit(1)"),
            }, CancellationToken.None);
            Assert.Equal("2 of 12 tests failed", result.Steps[0].Summary);
        }

        [Fact]
        public async Task A_missing_program_and_a_hanging_one_fail_clearly()
        {
            using var ws = new TempWorkspace();
            var missing = await new VerifyRunner().RunAsync(ws.Root, new[] { new VerifyCommand("build", "nope", "stoat-no-such-program", Array.Empty<string>()) }, CancellationToken.None);
            Assert.Contains("could not be started", missing.Steps[0].Summary);

            var runner = new VerifyRunner { TestTimeout = TimeSpan.FromSeconds(1) };
            var hanging = await runner.RunAsync(ws.Root, new[] { Node("test", "setTimeout(() => {}, 30000)") }, CancellationToken.None);
            Assert.True(hanging.Steps[0].TimedOut);
            Assert.Contains("timed out", hanging.Steps[0].Summary);
        }

        [Fact]
        public async Task Stop_kills_a_running_check()
        {
            using var ws = new TempWorkspace();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new VerifyRunner().RunAsync(ws.Root, new[] { Node("test", "setTimeout(() => {}, 30000)") }, cts.Token));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>The verify loop and the budget cap inside the agent loop.</summary>
    public class VerifyLoopTests
    {
        private const string Original = "class Calc\n{\n    int A() => 1;\n}\n";

        // The "test suite": passes only when A() returns 2
        private const string Check = "const s=require('fs').readFileSync('Calc.cs','utf8'); if(s.includes('=> 2')){console.log('Tests:       3 passed, 3 total')} else {console.log('Tests:       1 failed, 2 passed, 3 total'); console.error('Expected A() to return 2'); process.exit(1)}";

        private static AgentHarness Harness() => new(o => o.VerifyCommands = new List<string> { "node check.js" });

        [Fact]
        public async Task A_wrong_edit_fails_the_check_the_model_fixes_it_and_the_check_passes()
        {
            using var h = Harness();
            h.Workspace.Write("Calc.cs", Original);
            h.Workspace.Write("check.js", Check);
            h.Llm.Call("edit_file", new { path = "Calc.cs", old_string = "int A() => 1;", new_string = "int A() => 3;" }, id: "e1")
                 .Text("Done: A now returns 2.")
                 .Call("edit_file", new { path = "Calc.cs", old_string = "int A() => 3;", new_string = "int A() => 2;" }, id: "e2")
                 .Text("Fixed: A returns 2.");

            var events = await h.RunAsync("make A return 2", "agent", reviewEdits: true, verify: true);

            var verify = events.Where(e => e.StartsWith("[VERIFY_EVENT]")).ToList();
            Assert.Equal(4, verify.Count);   // running, failed, running, passed
            Assert.Contains("\"status\":\"failed\"", verify[1]);
            Assert.Contains("\"willFix\":true", verify[1]);
            Assert.Contains("\"status\":\"passed\"", verify[3]);
            Assert.Contains("3/3 tests passed", verify[3]);
            Assert.Contains("int A() => 2;", h.Workspace.Read("Calc.cs"));

            // The model saw the failing output before its fix
            var fixPrompt = h.Llm.Requests[2].Last(m => m.Role == "user").Content;
            Assert.Contains("Automatic check", fixPrompt);
            Assert.Contains("Expected A() to return 2", fixPrompt);
            Assert.Equal(2, events.Count(e => e.StartsWith("[CHANGE_EVENT]")));   // both edits are in the review
        }

        [Fact]
        public async Task Fix_attempts_are_bounded()
        {
            using var h = Harness();
            h.Workspace.Write("Calc.cs", Original);
            h.Workspace.Write("check.js", Check);
            h.Llm.Call("edit_file", new { path = "Calc.cs", old_string = "int A() => 1;", new_string = "int A() => 3;" }, id: "e1").Text("done")
                 .Text("I can't find the cause.").Text("Still failing.");

            var events = await h.RunAsync("make A return 2", "agent", reviewEdits: true, verify: true);

            var verify = events.Where(e => e.StartsWith("[VERIFY_EVENT]")).ToList();
            Assert.Equal(2, verify.Count);   // the model changed nothing after the failure: no re-check
            Assert.Contains("\"status\":\"failed\"", verify[1]);
        }

        [Fact]
        public async Task No_check_without_changes_in_read_only_modes_or_when_turned_off()
        {
            using (var h = Harness())
            {
                h.Workspace.Write("check.js", Check);
                h.Llm.Text("Nothing to change.");
                Assert.DoesNotContain(await h.RunAsync("hi", "agent", verify: true), e => e.StartsWith("[VERIFY_EVENT]"));
            }
            using (var h = Harness())
            {
                h.Workspace.Write("Calc.cs", Original);
                h.Workspace.Write("check.js", Check);
                h.Llm.Call("edit_file", new { path = "Calc.cs", old_string = "int A() => 1;", new_string = "int A() => 2;" }).Text("done");
                Assert.DoesNotContain(await h.RunAsync("edit", "agent", reviewEdits: true, verify: false), e => e.StartsWith("[VERIFY_EVENT]"));
            }
        }

        [Fact]
        public async Task A_project_without_commands_says_so()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("notes.txt", "a");
            h.Llm.Call("edit_file", new { path = "notes.txt", old_string = "a", new_string = "b" }).Text("done");
            var events = await h.RunAsync("edit", "agent", reviewEdits: true, verify: true);
            var verify = Assert.Single(events, e => e.StartsWith("[VERIFY_EVENT]"));
            Assert.Contains("unavailable", verify);
        }

        [Fact]
        public async Task The_budget_cap_stops_before_the_next_model_call()
        {
            // "fake" costs $1 per 1K tokens; each call uses 100 in + 10 out = $0.11
            using var h = new AgentHarness(configureServices: s => s.Configure<LLMOptions>(o =>
                o.Pricing["fake"] = new ModelPriceOptions { Input = 1, CachedInput = 1, Output = 1 }));
            h.Workspace.Write("a.txt", "x");
            for (var i = 0; i < 5; i++) h.Llm.Call("read_file", new { path = "a.txt" }, id: $"r{i}");

            var events = await h.RunAsync("read a lot", "ask", budgetUsd: 0.2);

            Assert.Equal(2, h.Llm.Requests.Count);   // $0.22 after two calls ≥ $0.20: no third call
            var limit = Assert.Single(events, e => e.StartsWith("[LIMIT_EVENT]"));
            Assert.Contains("\"reason\":\"budget\"", limit);
            Assert.Contains(events, e => e.Contains("reached its $0.20 budget"));
            Assert.Contains(events, e => e.StartsWith("[METRICS_EVENT]") && e.Contains("\"outcome\":\"budget\"") && e.Contains("\"priced\":true"));
        }

        [Fact]
        public async Task Models_without_a_price_are_not_limited()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("a.txt", "x");
            h.Llm.Call("read_file", new { path = "a.txt" }, id: "r1").Call("read_file", new { path = "a.txt" }, id: "r2").Text("done");

            var events = await h.RunAsync("read", "ask", budgetUsd: 0.0001);

            Assert.DoesNotContain(events, e => e.StartsWith("[LIMIT_EVENT]"));
            Assert.Contains(events, e => e.StartsWith("[METRICS_EVENT]") && e.Contains("\"priced\":false"));
        }
    }
}
