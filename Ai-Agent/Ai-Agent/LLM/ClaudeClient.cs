using Ai_Agent.Config;
using Ai_Agent.Models;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Ai_Agent.LLM
{
    /// <summary>
    /// Claude through the official Anthropic SDK (Messages API, streamed).
    /// - Our OpenAI-style history is mapped to Claude turns (MapMessages, pure and unit-tested): system text goes to
    ///   `system`, assistant tool calls become tool_use blocks after their signed thinking blocks, and consecutive
    ///   tool results become ONE user message of tool_result blocks.
    /// - Top-level prompt caching; token counts are normalized so InputTokens includes cached tokens, like the other providers.
    /// - claude-opus-5 requests enable server-side refusal fallbacks (beta), so a safety decline is re-served by a fallback
    ///   model instead of ending the turn.
    /// </summary>
    public class ClaudeClient : ILLMClient
    {
        private readonly AnthropicOptions _options;
        private readonly ILogger<ClaudeClient> _logger;
        private readonly Anthropic.AnthropicClient _client;

        public ClaudeClient(IOptions<AnthropicOptions> options, ILogger<ClaudeClient> logger)
        {
            _options = options.Value;
            _logger = logger;
            _client = string.IsNullOrWhiteSpace(_options.ApiKey)
                ? new Anthropic.AnthropicClient()   // ANTHROPIC_API_KEY / `ant auth login` profile
                : new Anthropic.AnthropicClient { ApiKey = _options.ApiKey };
        }

        public string ProviderName => "anthropic";
        public string DefaultModel => string.IsNullOrWhiteSpace(_options.Model) ? AnthropicOptions.DefaultModelId : _options.Model;

        public Task<List<ModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<ModelInfo>
            {
                new() { Id = "claude-opus-5", Name = "Claude Opus 5", Provider = ProviderName, MaxContextTokens = 1_000_000, SupportsFunctionCalling = true },
                new() { Id = "claude-sonnet-5", Name = "Claude Sonnet 5", Provider = ProviderName, MaxContextTokens = 1_000_000, SupportsFunctionCalling = true },
                new() { Id = "claude-haiku-4-5", Name = "Claude Haiku 4.5", Provider = ProviderName, MaxContextTokens = 200_000, SupportsFunctionCalling = true },
            });

        public async Task<LLMResponse> SendMessageAsync(
            List<ChatMessage> messages, List<ToolDefinition>? tools = null, string? model = null,
            CancellationToken cancellationToken = default)
        {
            // Streaming underneath (long outputs don't hit HTTP timeouts), collected into one response
            var text = new StringBuilder();
            var calls = new Dictionary<int, ToolCall>();
            var response = new LLMResponse();
            await foreach (var chunk in StreamMessageAsync(messages, tools, model, cancellationToken))
            {
                if (chunk.Content != null) text.Append(chunk.Content);
                foreach (var delta in chunk.ToolCallDeltas ?? new())
                {
                    if (!calls.TryGetValue(delta.Index, out var call)) calls[delta.Index] = call = new ToolCall();
                    if (delta.Id != null) call.Id = delta.Id;
                    if (delta.Name != null) call.Function.Name = delta.Name;
                    if (delta.Arguments != null) call.Function.Arguments += delta.Arguments;
                }
                response.InputTokens = chunk.InputTokens ?? response.InputTokens;
                response.OutputTokens = chunk.OutputTokens ?? response.OutputTokens;
            }
            response.Content = text.ToString();
            response.ToolCalls = calls.Count > 0 ? calls.OrderBy(c => c.Key).Select(c => c.Value).ToList() : null;
            return response;
        }

        public async IAsyncEnumerable<LLMStreamChunk> StreamMessageAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            string? model = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var parameters = BuildParams(messages, tools, model ?? DefaultModel);
            _logger.LogInformation("[TRACE] ANTHROPIC_STREAM | Model={Model} | Turns={Turns} | Tools={Tools}",
                parameters.Model, parameters.Messages.Count, tools?.Count ?? 0);

            long input = 0, cacheRead = 0, cacheWrite = 0, output = 0;
            string? stopReason = null;
            var toolBlocks = new HashSet<long>();          // content-block indexes that are tool_use
            var toolArgsSeen = new HashSet<long>();        // ...that received input JSON
            var thinking = new Dictionary<long, ReasoningBlock>();

            var events = _client.Beta.Messages.CreateStreaming(parameters, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    BetaRawMessageStreamEvent ev;
                    try
                    {
                        if (!await events.MoveNextAsync()) break;
                        ev = events.Current;
                    }
                    catch (AnthropicApiException ex)
                    {
                        // Keep the API's reason (bad key, overloaded, invalid request...); the chat shows ex.Message
                        throw new LLMException($"Anthropic API error: {ex.Message}", ProviderName, ex);
                    }

                    if (ev.TryPickStart(out var start))
                    {
                        input = start.Message.Usage.InputTokens;
                        cacheRead = start.Message.Usage.CacheReadInputTokens ?? 0;
                        cacheWrite = start.Message.Usage.CacheCreationInputTokens ?? 0;
                    }
                    else if (ev.TryPickContentBlockStart(out var blockStart))
                    {
                        var index = blockStart.Index;
                        if (blockStart.ContentBlock.TryPickBetaToolUse(out var toolUse))
                        {
                            toolBlocks.Add(index);
                            yield return new LLMStreamChunk
                            {
                                ToolCallDeltas = new() { new ToolCallDelta { Index = (int)index, Id = toolUse.ID, Name = toolUse.Name } }
                            };
                        }
                        else if (blockStart.ContentBlock.TryPickBetaThinking(out var think))
                        {
                            thinking[index] = new ReasoningBlock { Thinking = think.Thinking, Signature = think.Signature };
                        }
                        else if (blockStart.ContentBlock.TryPickBetaRedactedThinking(out var redacted))
                        {
                            thinking[index] = new ReasoningBlock { RedactedData = redacted.Data };
                        }
                        else if (blockStart.ContentBlock.TryPickBetaText(out var text) && !string.IsNullOrEmpty(text.Text))
                        {
                            yield return new LLMStreamChunk { Content = text.Text };
                        }
                    }
                    else if (ev.TryPickContentBlockDelta(out var blockDelta))
                    {
                        var index = blockDelta.Index;
                        if (blockDelta.Delta.TryPickText(out var text))
                        {
                            yield return new LLMStreamChunk { Content = text.Text };
                        }
                        else if (blockDelta.Delta.TryPickInputJson(out var json))
                        {
                            toolArgsSeen.Add(index);
                            yield return new LLMStreamChunk
                            {
                                ToolCallDeltas = new() { new ToolCallDelta { Index = (int)index, Arguments = json.PartialJson } }
                            };
                        }
                        else if (blockDelta.Delta.TryPickThinking(out var think) && thinking.TryGetValue(index, out var block))
                        {
                            block.Thinking += think.Thinking;
                        }
                        else if (blockDelta.Delta.TryPickSignature(out var signature) && thinking.TryGetValue(index, out var signed))
                        {
                            signed.Signature = signature.Signature;
                        }
                    }
                    else if (ev.TryPickContentBlockStop(out var blockStop))
                    {
                        // A tool with no arguments sends no input JSON at all
                        if (toolBlocks.Contains(blockStop.Index) && !toolArgsSeen.Contains(blockStop.Index))
                            yield return new LLMStreamChunk
                            {
                                ToolCallDeltas = new() { new ToolCallDelta { Index = (int)blockStop.Index, Arguments = "{}" } }
                            };
                    }
                    else if (ev.TryPickDelta(out var messageDelta))
                    {
                        output = messageDelta.Usage.OutputTokens;
                        stopReason = messageDelta.Delta.StopReason?.Raw();   // wire value: "end_turn", "tool_use", "max_tokens", "refusal"...
                    }
                }
            }
            finally
            {
                await events.DisposeAsync();
            }

            // Normalized like the OpenAI-format providers: InputTokens is ALL input, CacheHitTokens the cached part
            yield return new LLMStreamChunk
            {
                InputTokens = (int)(input + cacheRead + cacheWrite),
                OutputTokens = (int)output,
                CacheHitTokens = (int)cacheRead,
                StopReason = stopReason,
                Reasoning = thinking.Count > 0 ? thinking.OrderBy(t => t.Key).Select(t => t.Value).ToList() : null
            };
        }

        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _client.Models.Retrieve(DefaultModel, cancellationToken: cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is AnthropicException or HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        public int EstimateTokens(string text) => text.Length / 4;

        // ── Request building ────────────────────────────────────────────────

        /// <summary>The SDK request for our messages/tools (the mapping itself is MapMessages).</summary>
        public MessageCreateParams BuildParams(List<ChatMessage> messages, List<ToolDefinition>? tools, string model)
        {
            var (system, turns) = MapMessages(messages);
            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = _options.MaxTokens,
                Messages = turns.Select(ToParam).ToList(),
                // Caches the longest stable prefix (tools, system, history) automatically on every request
                CacheControl = new BetaCacheControlEphemeral(),
            };
            if (!string.IsNullOrWhiteSpace(system))
                parameters = parameters with { System = new List<BetaTextBlockParam> { new() { Text = system } } };
            if (tools is { Count: > 0 })
                parameters = parameters with { Tools = tools.Select(ToTool).ToList() };
            // Effort: not accepted by Haiku 4.5
            if (!model.StartsWith("claude-haiku", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_options.Effort))
                parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = _options.Effort } };
            // Opus 5: a safety refusal is re-served by a fallback model instead of ending the agent's turn
            if (model.StartsWith("claude-opus-5", StringComparison.OrdinalIgnoreCase) && _options.RefusalFallbacks)
                parameters = parameters with
                {
                    Betas = [AnthropicBeta.ServerSideFallback2026_06_01],
                    Fallbacks = new List<BetaFallbackParam> { new() { Model = "claude-opus-4-8" } }
                };
            return parameters;
        }

        /// <summary>
        /// OpenAI-style history → (system text, Claude turns). Pure: no SDK types, so it is unit-tested directly.
        /// Consecutive messages with the same Claude role are merged (tool results + a following note → one user turn).
        /// </summary>
        public static (string System, List<ClaudeTurn> Turns) MapMessages(IEnumerable<ChatMessage> messages)
        {
            var system = new StringBuilder();
            var turns = new List<ClaudeTurn>();

            void Add(string role, ClaudeBlock block)
            {
                if (turns.Count > 0 && turns[^1].Role == role) turns[^1].Blocks.Add(block);
                else turns.Add(new ClaudeTurn(role, new List<ClaudeBlock> { block }));
            }

            foreach (var m in messages)
            {
                switch (m.Role)
                {
                    case "system":
                        if (system.Length > 0) system.Append("\n\n");
                        system.Append(m.Content);
                        break;

                    case "tool":
                        Add("user", ClaudeBlock.ToolResult(m.ToolCallId ?? string.Empty, m.Content, m.Content.StartsWith("ERROR")));
                        break;

                    case "assistant":
                        // Signed thinking first (replayed unchanged), then text, then tool calls
                        foreach (var r in m.Reasoning ?? new())
                            Add("assistant", r.RedactedData != null ? ClaudeBlock.Redacted(r.RedactedData) : ClaudeBlock.Thinking(r.Thinking ?? "", r.Signature ?? ""));
                        if (!string.IsNullOrWhiteSpace(m.Content))
                            Add("assistant", ClaudeBlock.TextBlock(m.Content));
                        foreach (var call in m.ToolCalls ?? new())
                            Add("assistant", ClaudeBlock.ToolUse(call.Id, call.Function.Name, ParseInput(call.Function.Arguments)));
                        if (turns.Count == 0 || turns[^1].Role != "assistant")
                            Add("assistant", ClaudeBlock.TextBlock("(no reply)"));   // Claude rejects empty turns
                        break;

                    default:
                        if (!string.IsNullOrWhiteSpace(m.Content))
                            Add("user", ClaudeBlock.TextBlock(m.Content));
                        break;
                }
            }
            return (system.ToString(), turns);
        }

        /// <summary>Tool-call arguments as the JSON object Claude expects; invalid JSON (a cut-off call) becomes {}.</summary>
        private static Dictionary<string, JsonElement> ParseInput(string argumentsJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                    : new Dictionary<string, JsonElement>();
            }
            catch (JsonException)
            {
                return new Dictionary<string, JsonElement>();
            }
        }

        private static BetaMessageParam ToParam(ClaudeTurn turn) => new()
        {
            Role = turn.Role == "assistant" ? Role.Assistant : Role.User,
            Content = turn.Blocks.Select(ToBlockParam).ToList()
        };

        private static BetaContentBlockParam ToBlockParam(ClaudeBlock b) => b.Kind switch
        {
            ClaudeBlockKind.Thinking => new BetaThinkingBlockParam { Thinking = b.Text ?? "", Signature = b.Signature ?? "" },
            ClaudeBlockKind.RedactedThinking => new BetaRedactedThinkingBlockParam { Data = b.Data ?? "" },
            ClaudeBlockKind.ToolUse => new BetaToolUseBlockParam { ID = b.ToolUseId!, Name = b.ToolName!, Input = b.Input! },
            ClaudeBlockKind.ToolResult => new BetaToolResultBlockParam { ToolUseID = b.ToolUseId!, Content = b.Text ?? "", IsError = b.IsError },
            _ => new BetaTextBlockParam { Text = b.Text ?? "" }
        };

        /// <summary>Our tool definition (OpenAI function schema) → a Claude tool; input streams as it is generated.</summary>
        private static BetaToolUnion ToTool(ToolDefinition tool)
        {
            var schema = JsonSerializer.SerializeToElement(tool.Function.Parameters);
            var properties = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                ? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>();
            var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
                ? req.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0).ToList()
                : new List<string>();

            return new BetaTool
            {
                Name = tool.Function.Name,
                Description = tool.Function.Description,
                InputSchema = new() { Properties = properties, Required = required },
                // Large inputs (write_file content) stream as generated; ToolArguments.Parse rejects invalid/cut-off JSON
                EagerInputStreaming = true,
            };
        }
    }

    public enum ClaudeBlockKind { Text, Thinking, RedactedThinking, ToolUse, ToolResult }

    public sealed record ClaudeTurn(string Role, List<ClaudeBlock> Blocks);

    public sealed record ClaudeBlock(ClaudeBlockKind Kind)
    {
        public string? Text { get; init; }
        public string? Signature { get; init; }
        public string? Data { get; init; }
        public string? ToolUseId { get; init; }
        public string? ToolName { get; init; }
        public Dictionary<string, JsonElement>? Input { get; init; }
        public bool IsError { get; init; }

        public static ClaudeBlock TextBlock(string text) => new(ClaudeBlockKind.Text) { Text = text };
        public static ClaudeBlock Thinking(string thinking, string signature) => new(ClaudeBlockKind.Thinking) { Text = thinking, Signature = signature };
        public static ClaudeBlock Redacted(string data) => new(ClaudeBlockKind.RedactedThinking) { Data = data };
        public static ClaudeBlock ToolUse(string id, string name, Dictionary<string, JsonElement> input) =>
            new(ClaudeBlockKind.ToolUse) { ToolUseId = id, ToolName = name, Input = input };
        public static ClaudeBlock ToolResult(string id, string content, bool isError) =>
            new(ClaudeBlockKind.ToolResult) { ToolUseId = id, Text = content, IsError = isError };
    }
}
