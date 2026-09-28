using Ai_Agent.Agent.Services;
using Ai_Agent.Config;
using Ai_Agent.Models;
using Ai_Agent.Tools.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Ai_Agent.Tests
{
    public class UnifiedDiffServiceTests
    {
        private readonly UnifiedDiffService _diff = new();

        public static TheoryData<string, string, string> Cases => new()
        {
            { "CRLF insert at top", "line1\r\nline2\r\nline3\r\n", "new\r\nline1\r\nline2\r\nline3\r\n" },
            { "CRLF replace middle", "line1\r\nline2\r\nline3\r\n", "line1\r\nCHANGED\r\nline3\r\n" },
            { "LF delete line", "a\nb\nc\n", "a\nc\n" },
            { "no final newline", "a\nb", "a\nb\nc" },
            { "append at end", "a\r\nb\r\n", "a\r\nb\r\ntail\r\n" },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void Patch_applies_forward_and_reverses_exactly(string name, string before, string after)
        {
            var patch = _diff.ComputeDiff("f.cs", before, after);
            Assert.DoesNotContain("\r", patch);
            Assert.Equal(after, _diff.ApplyPatch(before, patch));
            Assert.Equal(before, _diff.ApplyPatch(after, patch, reverse: true));
            _ = name;
        }

        [Fact]
        public void Reverse_patch_refuses_when_the_file_changed_since()
        {
            var patch = _diff.ComputeDiff("f.cs", "a\nb\nc\n", "a\nCHANGED\nc\n");
            Assert.Throws<PatchConflictException>(() => _diff.ApplyPatch("a\nUSER EDIT\nc\n", patch, reverse: true));
        }
    }

    public class ChangeTrackerRevertTests
    {
        private static ChangeTracker Tracker() => new(new UnifiedDiffService(), NullLogger<ChangeTracker>.Instance);

        [Fact]
        public async Task Untouched_file_is_restored_byte_for_byte_with_bom()
        {
            using var ws = new TempWorkspace();
            const string before = "class C\r\n{\r\n}\r\n";
            ws.Write("C.cs", before, bom: true);
            var originalBytes = ws.ReadBytes("C.cs");
            const string after = "// added\r\nclass C\r\n{\r\n}\r\n";
            ws.Write("C.cs", after, bom: true);

            var tracker = Tracker();
            var change = await tracker.RecordChangeAsync(ws.Root, "s1", 1, "C.cs", "edit_file", before, after, isNewFile: false);
            var (ok, error) = await tracker.RevertFileAsync(change, ws.Root);

            Assert.True(ok, error);
            Assert.Equal(originalBytes, ws.ReadBytes("C.cs"));
        }

        [Fact]
        public async Task Revert_is_refused_when_the_user_edited_the_changed_lines()
        {
            using var ws = new TempWorkspace();
            ws.Write("C.cs", "a\nCHANGED\nc\n");
            var tracker = Tracker();
            var change = await tracker.RecordChangeAsync(ws.Root, "s1", 1, "C.cs", "edit_file", "a\nb\nc\n", "a\nCHANGED\nc\n", isNewFile: false);
            ws.Write("C.cs", "a\nUSER EDIT\nc\n");

            var (ok, error) = await tracker.RevertFileAsync(change, ws.Root);

            Assert.False(ok);
            Assert.Contains("refused", error);
            Assert.Equal("a\nUSER EDIT\nc\n", ws.Read("C.cs"));
        }
    }

    public class ContextPrunerTests
    {
        [Fact]
        public void Prunes_under_budget_without_orphaning_tool_results()
        {
            var counter = new TokenCounter();
            var pruner = new ContextPruner(NullLogger<ContextPruner>.Instance, counter);
            var messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "system" },
                new() { Role = "user", Content = "current request" }
            };
            for (var i = 0; i < 30; i++)
            {
                var id = $"call_{i}";
                var args = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = $"F{i}.cs", ["content"] = new string('x', 3000) });
                messages.Add(new ChatMessage { Role = "assistant", ToolCalls = new() { new ToolCall { Id = id, Function = new ToolCallFunction { Name = "write_file", Arguments = args } } } });
                messages.Add(new ChatMessage { Role = "tool", ToolCallId = id, Name = "write_file", Content = new string('y', 4000) });
            }

            var pruned = pruner.Prune(messages, 12000);

            Assert.True(counter.EstimateTokens(pruned) <= 12000);
            var callIds = pruned.Where(m => m.ToolCalls != null).SelectMany(m => m.ToolCalls!).Select(c => c.Id).ToHashSet();
            var resultIds = pruned.Where(m => m.Role == "tool").Select(m => m.ToolCallId!).ToHashSet();
            Assert.True(callIds.SetEquals(resultIds));
            Assert.Equal("current request", pruned[1].Content);
            Assert.All(pruned.Where(m => m.ToolCalls != null).SelectMany(m => m.ToolCalls!),
                c => JsonDocument.Parse(c.Function.Arguments).Dispose());   // arguments stay valid JSON
        }
    }

    public class ApprovalBrokerTests
    {
        [Fact]
        public async Task Accept_reject_timeout_and_cancel_all_resolve()
        {
            var broker = new ApprovalBroker();

            broker.Register("a");
            var accept = broker.WaitAsync("a", TimeSpan.FromSeconds(5), default);
            Assert.True(broker.Resolve("a", true));
            Assert.Equal(ApprovalDecision.Approved, await accept);

            broker.Register("r");
            var reject = broker.WaitAsync("r", TimeSpan.FromSeconds(5), default);
            broker.Resolve("r", false);
            Assert.Equal(ApprovalDecision.Rejected, await reject);

            broker.Register("t");
            Assert.Equal(ApprovalDecision.TimedOut, await broker.WaitAsync("t", TimeSpan.FromMilliseconds(50), default));

            broker.Register("c");
            using var cts = new CancellationTokenSource(50);
            Assert.Equal(ApprovalDecision.Cancelled, await broker.WaitAsync("c", TimeSpan.FromSeconds(5), cts.Token));

            Assert.False(broker.Resolve("unknown", true));   // nothing waiting → 404 in the API
        }
    }
}
