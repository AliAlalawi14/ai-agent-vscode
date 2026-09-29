using Ai_Agent.Config;
using Ai_Agent.LLM;
using Ai_Agent.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Ai_Agent.Tests
{
    /// <summary>Phase 5: Claude message mapping, the OpenAI-compatible client, routing, and stop-reason handling.</summary>
    public class ProviderTests
    {
        // ── Claude: our OpenAI-style history → Claude turns ──

        [Fact]
        public void Claude_mapping_moves_system_out_and_keeps_signed_thinking_before_tool_calls()
        {
            var messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "You are a coding agent." },
                new() { Role = "user", Content = "fix Calc" },
                new()
                {
                    Role = "assistant",
                    Content = "Reading it first.",
                    Reasoning = new() { new ReasoningBlock { Thinking = "need the file", Signature = "sig-123" } },
                    ToolCalls = new()
                    {
                        new ToolCall { Id = "t1", Function = new() { Name = "read_file", Arguments = "{\"path\":\"Calc.cs\"}" } },
                        new ToolCall { Id = "t2", Function = new() { Name = "read_file", Arguments = "{\"path\":\"broken" } }
                    }
                },
                new() { Role = "tool", ToolCallId = "t1", Content = "class Calc {}" },
                new() { Role = "tool", ToolCallId = "t2", Content = "ERROR: File not found" },
                new() { Role = "user", Content = "(System note: 3 steps left)" }
            };

            var (system, turns) = ClaudeClient.MapMessages(messages);

            Assert.Equal("You are a coding agent.", system);
            Assert.Equal(new[] { "user", "assistant", "user" }, turns.Select(t => t.Role));

            var assistant = turns[1].Blocks;
            Assert.Equal(new[] { ClaudeBlockKind.Thinking, ClaudeBlockKind.Text, ClaudeBlockKind.ToolUse, ClaudeBlockKind.ToolUse },
                         assistant.Select(b => b.Kind));
            Assert.Equal("sig-123", assistant[0].Signature);                       // replayed unchanged
            Assert.Equal("Calc.cs", assistant[2].Input!["path"].GetString());
            Assert.Empty(assistant[3].Input!);                                     // cut-off arguments → {}

            // Both tool results AND the note after them form ONE user turn (Claude requires alternation)
            var results = turns[2].Blocks;
            Assert.Equal(new[] { ClaudeBlockKind.ToolResult, ClaudeBlockKind.ToolResult, ClaudeBlockKind.Text }, results.Select(b => b.Kind));
            Assert.Equal("t1", results[0].ToolUseId);
            Assert.False(results[0].IsError);
            Assert.True(results[1].IsError);
        }

        [Fact]
        public void Claude_mapping_never_sends_an_empty_assistant_turn()
        {
            var (_, turns) = ClaudeClient.MapMessages(new List<ChatMessage>
            {
                new() { Role = "user", Content = "hi" },
                new() { Role = "assistant", Content = "" },
                new() { Role = "user", Content = "again" }
            });

            Assert.Equal(ClaudeBlockKind.Text, turns[1].Blocks.Single().Kind);
            Assert.False(string.IsNullOrEmpty(turns[1].Blocks.Single().Text));
        }

        private static ClaudeClient Claude(Action<AnthropicOptions>? configure = null)
        {
            var options = new AnthropicOptions { ApiKey = "test-key" };
            configure?.Invoke(options);
            return new ClaudeClient(Options.Create(options), NullLogger<ClaudeClient>.Instance);
        }

        private static readonly List<ToolDefinition> OneTool = new()
        {
            new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = "read_file",
                    Description = "Reads a file",
                    Parameters = new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } }
                }
            }
        };

        [Fact]
        public void Claude_request_caches_streams_tool_input_and_enables_fallbacks_for_opus_5()
        {
            var messages = new List<ChatMessage> { new() { Role = "system", Content = "sys" }, new() { Role = "user", Content = "hi" } };

            var opus = Claude().BuildParams(messages, OneTool, "claude-opus-5");
            var haiku = Claude().BuildParams(messages, OneTool, "claude-haiku-4-5");
            var sonnet = Claude().BuildParams(messages, OneTool, "claude-sonnet-5");

            Assert.NotNull(opus.CacheControl);
            Assert.NotNull(opus.Fallbacks);                 // refusal fallbacks on by default for Opus 5
            Assert.Null(sonnet.Fallbacks);
            Assert.NotNull(sonnet.OutputConfig);            // effort
            Assert.Null(haiku.OutputConfig);                // Haiku 4.5 rejects effort
            var tool = Assert.Single(opus.Tools!);
            Assert.True(tool.TryPickBetaTool(out var betaTool));
            Assert.True(betaTool!.EagerInputStreaming);
            Assert.Equal(new[] { "path" }, betaTool.InputSchema.Required);
            Assert.Null(Claude(o => o.RefusalFallbacks = false).BuildParams(messages, OneTool, "claude-opus-5").Fallbacks);
        }

        [Fact]
        public void Providers_count_as_configured_only_with_their_settings()
        {
            Assert.True(new AnthropicOptions { ApiKey = "k" }.IsConfigured);
            Assert.False(new OpenAICompatibleOptions { BaseUrl = "http://localhost:11434" }.IsConfigured);   // no models
            Assert.True(new OpenAICompatibleOptions
            {
                BaseUrl = "http://localhost:11434",
                Models = new() { new OpenAIModelOptions { Id = "qwen3:14b" } }
            }.IsConfigured);
        }

        // ── OpenAI-compatible client ──

        private sealed class SseHandler(string sse) : HttpMessageHandler
        {
            public string? RequestBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
        }

        [Fact]
        public async Task OpenAI_compatible_stream_reads_text_tools_cached_tokens_and_cutoff()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"Reading\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"pa\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\"\"}}]},\"finish_reason\":\"length\"}]}\n\n" +
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":50,\"prompt_tokens_details\":{\"cached_tokens\":800}}}\n\n" +
                "data: [DONE]\n\n";
            var handler = new SseHandler(sse);
            // The older single "OpenAI" section still works: it becomes one provider of the list
            var provider = new OpenAICompatibleOptions
            {
                BaseUrl = "http://llm.test",
                UseMaxCompletionTokens = true,
                Models = new() { new OpenAIModelOptions { Id = "gpt-test" } }
            }.ToCustomProvider();
            var client = new OpenAICompatibleClient(
                new HttpClient(handler) { BaseAddress = provider.BaseAddress },
                provider.ToSettings(),
                NullLogger<OpenAICompatibleClient>.Instance);

            var chunks = new List<LLMStreamChunk>();
            await foreach (var c in client.StreamMessageAsync(new() { new() { Role = "user", Content = "hi" } }, OneTool))
                chunks.Add(c);

            Assert.Equal("Reading", string.Concat(chunks.Select(c => c.Content)));
            Assert.Equal("{\"path\"", string.Concat(chunks.SelectMany(c => c.ToolCallDeltas ?? new()).Select(d => d.Arguments)));
            Assert.Equal(800, chunks.Sum(c => c.CacheHitTokens ?? 0));
            Assert.Equal("max_tokens", chunks.Last(c => c.StopReason != null).StopReason);   // "length" normalized

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.True(body.RootElement.TryGetProperty("max_completion_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty("temperature", out _));            // provider default
            Assert.Equal("gpt-test", body.RootElement.GetProperty("model").GetString());
        }

        // ── Routing ──

        private sealed class NamedFake(string provider, params string[] models) : ILLMClient
        {
            public string ProviderName => provider;
            public string DefaultModel => models[0];
            public Task<List<ModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(models.Select(m => new ModelInfo { Id = m, Provider = provider, SupportsFunctionCalling = true }).ToList());
            public Task<LLMResponse> SendMessageAsync(List<ChatMessage> m, List<ToolDefinition>? t = null, string? model = null, CancellationToken c = default) =>
                Task.FromResult(new LLMResponse { Content = provider });
            public async IAsyncEnumerable<LLMStreamChunk> StreamMessageAsync(List<ChatMessage> m, List<ToolDefinition>? t = null, string? model = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c = default)
            {
                await Task.Yield();
                yield return new LLMStreamChunk { Content = provider };
            }
            public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
            public int EstimateTokens(string text) => 0;
        }

        [Fact]
        public async Task Router_sends_each_model_to_its_provider_and_the_rest_to_the_default()
        {
            var registry = new LLMProviderRegistry(NullLogger<LLMProviderRegistry>.Instance);
            registry.RegisterProvider(new NamedFake("deepseek", "deepseek-chat"));
            registry.RegisterProvider(new NamedFake("anthropic", "claude-opus-5", "claude-sonnet-5"));
            var router = new RoutingLLMClient(registry, "anthropic");

            Assert.Equal("anthropic", (await router.ProviderForAsync("claude-sonnet-5")).ProviderName);
            Assert.Equal("deepseek", (await router.ProviderForAsync("deepseek-chat")).ProviderName);
            Assert.Equal("anthropic", (await router.ProviderForAsync(null)).ProviderName);          // default provider
            Assert.Equal("anthropic", (await router.ProviderForAsync("unknown-model")).ProviderName);
            Assert.Equal("claude-opus-5", router.DefaultModel);
            Assert.Equal(3, (await router.GetAvailableModelsAsync()).Count);

            var streamed = new List<string?>();
            await foreach (var c in router.StreamMessageAsync(new(), model: "deepseek-chat")) streamed.Add(c.Content);
            Assert.Equal(new[] { "deepseek" }, streamed);
        }

        [Fact]
        public void Router_without_any_provider_explains_what_to_configure()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new RoutingLLMClient(new LLMProviderRegistry(NullLogger<LLMProviderRegistry>.Instance), null));
            Assert.Contains("ANTHROPIC_API_KEY", ex.Message);
        }

        // ── Agent loop: stop reasons and reasoning replay ──

        [Fact]
        public async Task A_tool_call_cut_off_at_the_output_limit_is_not_run()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("Calc.cs", "class Calc { }\n");
            h.Llm.Chunks(
                    new LLMStreamChunk { ToolCallDeltas = new() { new ToolCallDelta { Index = 0, Id = "w1", Name = "write_file", Arguments = "{\"path\":\"Calc.cs\",\"content\":\"class Calc { int A" } } },
                    new LLMStreamChunk { StopReason = "max_tokens" })
                 .Text("I'll make smaller edits.");

            await h.RunAsync("rewrite Calc", "auto");

            Assert.Equal("class Calc { }\n", h.Workspace.Read("Calc.cs"));
            var toolResult = h.Llm.Requests[1].Last(m => m.Role == "tool").Content;
            Assert.Contains("output limit", toolResult);
        }

        [Fact]
        public async Task Thinking_blocks_are_sent_back_with_their_tool_call()
        {
            using var h = new AgentHarness();
            h.Workspace.Write("Calc.cs", "class Calc { }\n");
            h.Llm.Chunks(
                    new LLMStreamChunk { ToolCallDeltas = new() { new ToolCallDelta { Index = 0, Id = "r1", Name = "read_file", Arguments = "{\"path\":\"Calc.cs\"}" } } },
                    new LLMStreamChunk { StopReason = "tool_use", Reasoning = new() { new ReasoningBlock { Thinking = "look first", Signature = "sig-9" } } })
                 .Text("done");

            await h.RunAsync("explain Calc", "ask");

            var assistant = h.Llm.Requests[1].Single(m => m.Role == "assistant");
            Assert.Equal("sig-9", Assert.Single(assistant.Reasoning!).Signature);
        }

        [Fact]
        public async Task A_refusal_ends_the_turn_without_running_tools()
        {
            using var h = new AgentHarness();
            h.Llm.Chunks(
                new LLMStreamChunk { ToolCallDeltas = new() { new ToolCallDelta { Index = 0, Id = "x", Name = "read_file", Arguments = "{\"path\":\"a\"}" } } },
                new LLMStreamChunk { StopReason = "refusal" });

            var events = await h.RunAsync("something", "ask");

            Assert.Single(h.Llm.Requests);
            Assert.DoesNotContain(events, e => e.StartsWith("[TOOL_EVENT]") && e.Contains("tool_start"));
            Assert.Contains(events, e => e.Contains("declined"));
        }
    }
}
