using Ai_Agent.Agent.Services;
using Ai_Agent.Config;
using Ai_Agent.Data;
using Ai_Agent.LLM;
using Ai_Agent.Models;
using Ai_Agent.Tools.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ai_Agent.Tests
{
    /// <summary>A model that replays a script: each turn is either text or a list of tool calls.</summary>
    public sealed class FakeLLMClient : ILLMClient
    {
        private readonly Queue<Func<List<ChatMessage>, LLMStreamChunk[]>> _turns = new();
        public List<List<ChatMessage>> Requests { get; } = new();
        public List<List<string>> OfferedTools { get; } = new();

        public FakeLLMClient Text(string text)
        {
            _turns.Enqueue(_ => new[] { new LLMStreamChunk { Content = text } });
            return this;
        }

        public FakeLLMClient Call(string tool, object args, string? id = null)
        {
            var callId = id ?? $"call_{_turns.Count}";
            _turns.Enqueue(_ => new[]
            {
                new LLMStreamChunk
                {
                    ToolCallDeltas = new() { new ToolCallDelta { Index = 0, Id = callId, Name = tool, Arguments = JsonSerializer.Serialize(args) } }
                }
            });
            return this;
        }

        /// <summary>A turn made of raw stream chunks (stop reasons, reasoning blocks, partial tool calls...).</summary>
        public FakeLLMClient Chunks(params LLMStreamChunk[] chunks)
        {
            _turns.Enqueue(_ => chunks);
            return this;
        }

        public async IAsyncEnumerable<LLMStreamChunk> StreamMessageAsync(
            List<ChatMessage> messages, List<ToolDefinition>? tools = null, string? model = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            OfferedTools.Add(tools?.Select(t => t.Function.Name).ToList() ?? new());
            var turn = _turns.Count > 0 ? _turns.Dequeue() : _ => new[] { new LLMStreamChunk { Content = "(script finished)" } };
            foreach (var chunk in turn(messages))
            {
                await Task.Yield();
                yield return chunk;
            }
            yield return new LLMStreamChunk { InputTokens = 100, OutputTokens = 10, CacheHitTokens = 40 };
        }

        public Task<LLMResponse> SendMessageAsync(List<ChatMessage> messages, List<ToolDefinition>? tools = null, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LLMResponse { Content = "ok" });
        public Task<List<ModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<ModelInfo> { new() { Id = "fake", SupportsFunctionCalling = true } });
        public string ProviderName => "fake";
        public string DefaultModel => "fake";
        public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public int EstimateTokens(string text) => text.Length / 4;
    }

    public sealed class AgentHarness : IDisposable
    {
        public TempWorkspace Workspace { get; } = new();
        public FakeLLMClient Llm { get; } = new();
        public ServiceProvider Services { get; }
        public AgentService Agent => Services.GetRequiredService<AgentService>();
        public ApprovalBroker Approvals => Services.GetRequiredService<ApprovalBroker>();

        public AgentHarness(Action<AgentOptions>? configure = null)
        {
            var root = Workspace.Root;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.Configure<AgentOptions>(o => { o.WorkspaceRoot = root; o.ApiToken = "test"; configure?.Invoke(o); });
            services.Configure<LLMOptions>(o => o.Model = "fake");
            services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            services.AddSingleton<ConversationMemoryService>();
            services.AddSingleton<ProjectIndexer>();
            services.AddSingleton<ProjectContextService>();
            services.AddSingleton(sp => new PromptBuilder(root, sp.GetRequiredService<ProjectContextService>(), sp.GetRequiredService<ConversationMemoryService>()));
            services.AddSingleton<TokenCounter>();
            services.AddSingleton<ContextPruner>();
            services.AddSingleton<AgentMetrics>();
            services.AddSingleton<CostTracker>();
            services.AddSingleton<UnifiedDiffService>();
            services.AddSingleton<ChangeTracker>();
            services.AddSingleton<ApprovalBroker>();
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
                new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root });
            services.AddSingleton<AuditLog>();
            services.AddSingleton(_ => new OllamaEmbeddingService());
            services.AddHttpClient<ChromaDbService>(c => c.BaseAddress = new Uri("http://127.0.0.1:1"));
            services.AddSingleton<CodeVectorIndexer>();
            services.AddSingleton<PlanStore>();
            services.AddSingleton<ToolFactory>();
            services.AddSingleton<ILLMClient>(Llm);
            services.AddSingleton<AgentService>();
            Services = services.BuildServiceProvider();
        }

        /// <summary>Runs the agent; every approval request is answered with <paramref name="approve"/>.</summary>
        public async Task<List<string>> RunAsync(string task, string mode, bool approve = false, bool reviewEdits = false)
        {
            var events = new List<string>();
            await foreach (var chunk in Agent.RunStreamAsync(task, Workspace.Root, mode: mode, reviewEdits: reviewEdits))
            {
                events.Add(chunk);
                if (chunk.StartsWith("[APPROVAL_EVENT]") && !chunk.Contains("\"decision\"") && !chunk.Contains("\"autoApproved\""))
                {
                    var json = chunk["[APPROVAL_EVENT]".Length..^"[/APPROVAL_EVENT]".Length];
                    var id = JsonDocument.Parse(json).RootElement.GetProperty("approvalId").GetString()!;
                    Approvals.Resolve(id, approve);
                }
            }
            return events;
        }

        public void Dispose()
        {
            Services.Dispose();
            Workspace.Dispose();
        }
    }

    public class AgentLoopTests
    {
        private const string Original = "class Calc\n{\n    int A() => 1;\n}\n";

        [Fact]
        public async Task Rejected_edit_leaves_the_file_untouched_and_tells_the_model()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("Calc.cs", Original);
            h.Llm.Call("edit_file", new { path = "Calc.cs", old_string = "int A() => 1;", new_string = "int A() => 2;" }).Text("ok, not changed");

            var events = await h.RunAsync("change A to return 2", "agent", approve: false);

            Assert.Equal(Original, h.Workspace.Read("Calc.cs"));
            Assert.Contains(events, e => e.StartsWith("[APPROVAL_EVENT]") && e.Contains("\"rejected\""));
            Assert.DoesNotContain(events, e => e.StartsWith("[CHANGE_EVENT]"));
            var toolMessage = h.Llm.Requests[1].Last(m => m.Role == "tool");
            Assert.Contains("REJECTED", toolMessage.Content);
        }

        [Fact]
        public async Task Accepted_edit_is_written_and_reported_as_a_change_linked_to_its_approval()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("Calc.cs", Original);
            h.Llm.Call("edit_file", new { path = "Calc.cs", old_string = "int A() => 1;", new_string = "int A() => 2;" }).Text("done");

            var events = await h.RunAsync("change A to return 2", "agent", approve: true);

            Assert.Contains("int A() => 2;", h.Workspace.Read("Calc.cs"));
            var change = events.Single(e => e.StartsWith("[CHANGE_EVENT]"));
            Assert.Contains("\"approvalId\"", change);
            Assert.Contains(events, e => e.StartsWith("[METRICS_EVENT]"));
        }

        /// <summary>Approval prompts the user would have had to answer (not decisions, not auto-approved cards).</summary>
        private static List<string> ApprovalPrompts(List<string> events) =>
            events.Where(e => e.StartsWith("[APPROVAL_EVENT]") && !e.Contains("\"decision\"") && !e.Contains("\"autoApproved\"")).ToList();

        [Fact]
        public async Task Review_mode_applies_edits_without_asking_and_records_them_for_undo()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("Calc.cs", Original);
            h.Llm.Call("edit_file", new { path = "Calc.cs", old_string = "int A() => 1;", new_string = "int A() => 2;" }).Text("done");

            // approve: false would reject any prompt, so the edit landing proves none was shown
            var events = await h.RunAsync("change A to return 2", "agent", approve: false, reviewEdits: true);

            Assert.Contains("int A() => 2;", h.Workspace.Read("Calc.cs"));
            Assert.Empty(ApprovalPrompts(events));
            var change = events.Single(e => e.StartsWith("[CHANGE_EVENT]"));
            Assert.Contains("\"changeId\"", change);
        }

        [Fact]
        public async Task Review_mode_still_asks_before_running_a_command()
        {
            using var h = new AgentHarness();
            h.Llm.Call("run_terminal", new { command = "dotnet build" }).Text("not run");

            var events = await h.RunAsync("build it", "agent", approve: false, reviewEdits: true);

            var prompt = Assert.Single(ApprovalPrompts(events));
            Assert.Contains("\"command\"", prompt);
            Assert.Contains(events, e => e.StartsWith("[APPROVAL_EVENT]") && e.Contains("\"rejected\""));
        }

        [Fact]
        public async Task Review_mode_is_ignored_in_read_only_modes()
        {
            using var h = new AgentHarness();
            h.Llm.Text("answer");

            await h.RunAsync("change A", "ask", reviewEdits: true);

            Assert.DoesNotContain("edit_file", h.Llm.OfferedTools[0]);
        }

        [Fact]
        public async Task Secrets_in_files_never_reach_the_model()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("appsettings.json", "{ \"DeepSeek\": { \"ApiKey\": \"sk-1234567890abcdefghijklmnop\" } }");
            h.Workspace.Write("cert.pfx", "binary");
            h.Llm.Call("read_file", new { path = "appsettings.json" }, id: "r1")
                 .Call("read_file", new { path = "cert.pfx" }, id: "r2")
                 .Text("done");

            await h.RunAsync("show me the config", "ask");

            var toolResults = h.Llm.Requests.Last().Where(m => m.Role == "tool").Select(m => m.Content).ToList();
            Assert.DoesNotContain("sk-1234567890", toolResults[0]);
            Assert.Contains(SecretRedactor.Marker, toolResults[0]);
            Assert.StartsWith("ERROR", toolResults[1]);   // key/certificate files are not read at all
        }

        [Fact]
        public async Task Ask_mode_offers_no_tools_that_can_change_anything()
        {
            using var h = new AgentHarness();
            h.Llm.Text("answer");

            await h.RunAsync("delete Calc.cs", "ask");

            var offered = h.Llm.OfferedTools[0];
            Assert.Contains("read_file", offered);
            Assert.DoesNotContain("edit_file", offered);
            Assert.DoesNotContain("write_file", offered);
            Assert.DoesNotContain("run_terminal", offered);
        }

        [Fact]
        public async Task Plan_mode_emits_the_plan_and_ends_the_run()
        {
            using var h = new AgentHarness();
            h.Llm.Call("submit_plan", new
            {
                title = "Orders",
                summary = "## Goal\nOrders",
                steps = new[] { new { title = "Model", files = new[] { "Order.cs" }, details = "Create Order" } }
            }).Text("should never be requested");

            var events = await h.RunAsync("plan orders", "plan");

            Assert.Contains(events, e => e.StartsWith("[PLAN_EVENT]") && e.Contains("\"Orders\""));
            Assert.Single(h.Llm.Requests);   // stopped right after the plan
        }

        [Fact]
        public async Task Loop_guard_pauses_commands_after_three_failures()
        {
            using var h = new AgentHarness();   // temp folder is not a git repo: every git command fails
            var commands = new[] { "git status", "git log", "git branch", "git diff" };
            for (var i = 0; i < commands.Length; i++)
                h.Llm.Call("run_terminal", new { command = commands[i] }, id: $"c{i}");
            h.Llm.Text("explained");

            await h.RunAsync("run git commands", "auto");

            var toolResults = h.Llm.Requests.Last().Where(m => m.Role == "tool").Select(m => m.Content).ToList();
            Assert.Equal(4, toolResults.Count);
            Assert.All(toolResults.Take(3), r => Assert.StartsWith("ERROR: Command failed", r));
            Assert.Contains("paused", toolResults[3]);   // 4th never ran
        }

        [Fact]
        public async Task Loop_guard_refuses_an_exact_repeat_of_a_failed_call()
        {
            using var h = new AgentHarness();
            h.Llm.Call("run_terminal", new { command = "git status" }, id: "a")
                 .Call("run_terminal", new { command = "git status" }, id: "b")
                 .Text("explained");

            await h.RunAsync("run git status", "auto");

            var toolResults = h.Llm.Requests.Last().Where(m => m.Role == "tool").Select(m => m.Content).ToList();
            Assert.StartsWith("ERROR: Command failed", toolResults[0]);
            Assert.Contains("already made this exact call", toolResults[1]);
        }
    }
}
