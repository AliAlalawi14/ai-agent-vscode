using Ai_Agent.Agent.Services;
using Ai_Agent.Config;
using Ai_Agent.LLM;
using Ai_Agent.Models;
using Ai_Agent.Tools.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace Ai_Agent.Tests
{
    /// <summary>Phase 1: approvals don't hold LLM slots, real provider errors, cache-friendly prompt, cache-aware cost.</summary>
    public class Phase1Tests
    {
        [Fact]
        public async Task A_run_waiting_for_approval_does_not_block_another_chat()
        {
            // Only ONE LLM call at a time: if the waiting run held the slot, the second chat would hang
            using var h = new AgentHarness(o => o.MaxConcurrentLlmCalls = 1);
            h.Workspace.Write("Calc.cs", "class Calc\n{\n    int A() => 1;\n}\n");
            h.Llm.Call("edit_file", new { path = "Calc.cs", old_string = "int A() => 1;", new_string = "int A() => 2;" })
                 .Text("second chat answer")
                 .Text("first chat done");

            var approvalAsked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRun = Task.Run(async () =>
            {
                await foreach (var chunk in h.Agent.RunStreamAsync("change A", h.Workspace.Root, mode: "agent"))
                    if (chunk.StartsWith("[APPROVAL_EVENT]") && !chunk.Contains("\"decision\""))
                        approvalAsked.TrySetResult(JsonDocument.Parse(chunk["[APPROVAL_EVENT]".Length..^"[/APPROVAL_EVENT]".Length])
                            .RootElement.GetProperty("approvalId").GetString()!);
            });
            var approvalId = await approvalAsked.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The first run is now parked on Accept/Reject; the second chat must still get an answer
            var second = await h.RunAsync("a question", "ask").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(second, e => e.Contains("second chat answer"));

            h.Approvals.Resolve(approvalId, false);
            await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task Streaming_error_carries_the_providers_message()
        {
            var body = "{\"error\":{\"message\":\"This model's maximum context length is 65536 tokens\",\"type\":\"invalid_request_error\"}}";
            var http = new HttpClient(new StubHandler(HttpStatusCode.BadRequest, body)) { BaseAddress = new Uri("https://example.invalid") };
            var client = new DeepSeekClient(http, Options.Create(new LLMOptions { Model = "deepseek-chat" }), NullLogger<DeepSeekClient>.Instance);

            var ex = await Assert.ThrowsAsync<LLMException>(async () =>
            {
                await foreach (var _ in client.StreamMessageAsync(new List<ChatMessage> { new() { Role = "user", Content = "hi" } })) { }
            });

            Assert.Contains("400", ex.Message);
            Assert.Contains("maximum context length", ex.Message);
        }

        [Fact]
        public async Task System_prompt_puts_stable_parts_first_and_memories_last()
        {
            using var h = new AgentHarness();
            var root = h.Workspace.Root;
            await h.Services.GetRequiredService<ConversationMemoryService>().SaveAsync(new ConversationMemory
            {
                UserRequest = "earlier request", Summary = "earlier answer", Workspace = root
            });
            var builder = h.Services.GetRequiredService<PromptBuilder>();
            var registry = h.Services.GetRequiredService<ToolFactory>().CreateRegistry(root);

            var agent = await builder.BuildSystemPromptAsync(registry, root, AgentModes.Agent);
            var ask = await builder.BuildSystemPromptAsync(registry, root, AgentModes.Ask);

            var project = agent.IndexOf("## PROJECT CONTEXT", StringComparison.Ordinal);
            var mode = agent.IndexOf("## MODE", StringComparison.Ordinal);
            var memories = agent.IndexOf("RELEVANT PAST CONVERSATIONS", StringComparison.Ordinal);
            Assert.True(project >= 0 && project < mode && mode < memories, $"order: project={project} mode={mode} memories={memories}");

            // Different modes share everything before the mode section (that is what the prompt cache reuses)
            Assert.Equal(agent[..mode], ask[..ask.IndexOf("## MODE", StringComparison.Ordinal)]);
        }

        [Fact]
        public void Cost_bills_cached_input_at_the_cache_price()
        {
            var tracker = new CostTracker(Options.Create(new LLMOptions()), NullLogger<CostTracker>.Instance);

            var uncached = tracker.CalculateCost("deepseek-chat", 1000, 0).TotalCost;
            var allCached = tracker.CalculateCost("deepseek-chat", 1000, 0, cachedInputTokens: 1000).TotalCost;

            Assert.Equal(0.00027, uncached, 8);
            Assert.Equal(0.00007, allCached, 8);
        }

        [Fact]
        public void Configured_prices_override_the_built_in_ones()
        {
            var options = new LLMOptions();
            options.Pricing["deepseek-chat"] = new ModelPriceOptions { Input = 0.001, CachedInput = 0.0001, Output = 0.002 };
            var tracker = new CostTracker(Options.Create(options), NullLogger<CostTracker>.Instance);

            var cost = tracker.CalculateCost("deepseek-chat", 2000, 1000, cachedInputTokens: 1000);

            Assert.Equal(0.001 + 0.0001 + 0.002, cost.TotalCost, 8);
        }

        [Fact]
        public async Task Metrics_event_reports_cache_hits_and_prompt_build_time()
        {
            using var h = new AgentHarness();
            h.Llm.Text("answer");

            var events = await h.RunAsync("hello", "ask");

            var metrics = events.Single(e => e.StartsWith("[METRICS_EVENT]"));
            var json = JsonDocument.Parse(metrics["[METRICS_EVENT]".Length..^"[/METRICS_EVENT]".Length]).RootElement;
            Assert.Equal(40, json.GetProperty("cacheHitTokens").GetInt32());
            Assert.True(json.GetProperty("promptBuildMs").GetInt64() >= 0);
        }

        private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
