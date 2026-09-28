using Ai_Agent.Agent.Services;
using Ai_Agent.Config;
using Ai_Agent.Tools.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;

namespace Ai_Agent.Tests
{
    /// <summary>Phase 4: delete/move tools with revert, configurable commands, link guard, change-log retention.</summary>
    public class Phase4Tests
    {
        private static JsonElement Event(string chunk, string tag) =>
            JsonDocument.Parse(chunk[$"[{tag}]".Length..^$"[/{tag}]".Length]).RootElement;

        [Fact]
        public async Task Approved_delete_removes_the_file_and_revert_brings_it_back()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("Old.cs", "class Old { }\n");
            h.Llm.Call("delete_file", new { path = "Old.cs" }).Text("deleted");

            var events = await h.RunAsync("delete Old.cs", "agent", approve: true);

            Assert.False(File.Exists(Path.Combine(h.Workspace.Root, "Old.cs")));
            var approval = Event(events.First(e => e.StartsWith("[APPROVAL_EVENT]")), "APPROVAL_EVENT");
            Assert.Equal("Delete Old.cs", approval.GetProperty("summary").GetString());
            var change = Event(events.Single(e => e.StartsWith("[CHANGE_EVENT]")), "CHANGE_EVENT");
            Assert.True(change.GetProperty("isDeletion").GetBoolean());

            var tracker = h.Services.GetRequiredService<ChangeTracker>();
            var recorded = await tracker.GetChangeAsync(h.Workspace.Root, change.GetProperty("changeId").GetString()!);
            var (ok, error) = await tracker.RevertFileAsync(recorded!, h.Workspace.Root);

            Assert.True(ok, error);
            Assert.Equal("class Old { }\n", h.Workspace.Read("Old.cs"));
        }

        [Fact]
        public async Task Rejected_delete_leaves_the_file()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("Keep.cs", "class Keep { }\n");
            h.Llm.Call("delete_file", new { path = "Keep.cs" }).Text("ok");

            var events = await h.RunAsync("delete Keep.cs", "agent", approve: false);

            Assert.Equal("class Keep { }\n", h.Workspace.Read("Keep.cs"));
            Assert.DoesNotContain(events, e => e.StartsWith("[CHANGE_EVENT]"));
        }

        [Fact]
        public async Task Move_is_two_changes_and_a_session_revert_restores_the_original()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("Calc.cs", "class Calc { }\n");
            h.Llm.Call("move_file", new { path = "Calc.cs", new_path = "Math/Calculator.cs" }).Text("moved");

            var events = await h.RunAsync("move Calc.cs", "agent", approve: true);

            Assert.False(File.Exists(Path.Combine(h.Workspace.Root, "Calc.cs")));
            Assert.Equal("class Calc { }\n", h.Workspace.Read("Math/Calculator.cs"));
            var changes = events.Where(e => e.StartsWith("[CHANGE_EVENT]")).Select(e => Event(e, "CHANGE_EVENT")).ToList();
            Assert.Equal(2, changes.Count);

            var sessionId = changes[0].GetProperty("sessionId").GetString()!;
            var reverted = await h.Services.GetRequiredService<ChangeTracker>().RevertSessionAsync(sessionId, h.Workspace.Root);

            Assert.Equal(2, reverted.Count);
            Assert.Equal("class Calc { }\n", h.Workspace.Read("Calc.cs"));
            Assert.False(File.Exists(Path.Combine(h.Workspace.Root, "Math", "Calculator.cs")));
        }

        [Fact]
        public async Task Move_never_overwrites_an_existing_file()
        {
            using var ws = new TempWorkspace();
            ws.Write("A.cs", "a");
            ws.Write("B.cs", "b");

            var result = await new MoveFileTool(ws.Root).ExecuteAsync(new() { ["path"] = "A.cs", ["new_path"] = "B.cs" });

            Assert.StartsWith("ERROR", result);
            Assert.Equal("b", ws.Read("B.cs"));
        }

        [Fact]
        public async Task A_failed_write_to_a_new_path_is_not_recorded_as_a_change()
        {
            using var h = new AgentHarness();
            h.Llm.Call("write_file", new { path = "New.cs", content = "x" }).Text("failed");   // too short: the tool refuses

            var events = await h.RunAsync("create New.cs", "auto");

            Assert.False(File.Exists(Path.Combine(h.Workspace.Root, "New.cs")));
            Assert.DoesNotContain(events, e => e.StartsWith("[CHANGE_EVENT]"));
        }

        [Fact]
        public async Task Commands_come_from_config_and_unlisted_ones_stay_blocked()
        {
            using var ws = new TempWorkspace();
            var defaults = new TerminalTool(ws.Root);
            var configured = new TerminalTool(ws.Root, new AgentOptions().AllowedCommands.Append("npm test"));

            Assert.NotNull((await defaults.PreviewAsync(new() { ["command"] = "npm test" })).Error);
            Assert.Null((await configured.PreviewAsync(new() { ["command"] = "npm test" })).Error);
            Assert.NotNull((await configured.PreviewAsync(new() { ["command"] = "rm -rf ." })).Error);
            Assert.NotNull((await configured.PreviewAsync(new() { ["command"] = "npm test && rm -rf ." })).Error);
            Assert.Null((await configured.PreviewAsync(new() { ["command"] = "dotnet build" })).Error);   // defaults kept
        }

        [Fact]
        public void A_junction_pointing_outside_the_workspace_is_refused()
        {
            if (!OperatingSystem.IsWindows()) return;
            using var ws = new TempWorkspace();
            using var outside = new TempWorkspace();
            outside.Write("secret.txt", "outside");
            ws.Write("inside/ok.txt", "fine");

            var link = Path.Combine(ws.Root, "escape");
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Root}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                mklink!.WaitForExit();
            }
            Assert.True(Directory.Exists(link), "could not create the junction for the test");

            Assert.Null(WorkspacePath.Resolve(ws.Root, "escape/secret.txt"));
            Assert.Null(WorkspacePath.Resolve(ws.Root, "escape"));
            Assert.NotNull(WorkspacePath.Resolve(ws.Root, "inside/ok.txt"));
            Assert.NotNull(WorkspacePath.Resolve(ws.Root, "inside/new-file.cs"));   // not created yet: fine

            Directory.Delete(link);   // removes the junction only, not the target's files
        }

        [Fact]
        public async Task Change_log_keeps_only_the_most_recent_sessions()
        {
            using var ws = new TempWorkspace();
            var options = Options.Create(new AgentOptions { ChangeLogMaxSessions = 2 });
            var writer = new ChangeTracker(new UnifiedDiffService(), NullLogger<ChangeTracker>.Instance, options);
            foreach (var session in new[] { "s1", "s2", "s3" })
            {
                await writer.RecordChangeAsync(ws.Root, session, 1, "C.cs", "edit_file", "a\n", "b\n", isNewFile: false);
                await Task.Delay(15);   // distinct timestamps
            }

            var reader = new ChangeTracker(new UnifiedDiffService(), NullLogger<ChangeTracker>.Instance, options);
            var sessions = (await reader.GetRecentChangesAsync(ws.Root, 100)).Select(c => c.SessionId).Distinct().ToList();

            Assert.Equal(new[] { "s3", "s2" }, sessions);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(ws.Root, ChangeTracker.LogFileName)).Length);
        }
    }
}
