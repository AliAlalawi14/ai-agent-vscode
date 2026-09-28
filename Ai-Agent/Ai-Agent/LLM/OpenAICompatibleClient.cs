using Ai_Agent.Config;
using Ai_Agent.Models;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Ai_Agent.LLM
{
    /// <summary>What differs between OpenAI-format providers.</summary>
    public sealed record OpenAIProviderSettings(
        string ProviderName,
        string DefaultModel,
        IReadOnlyList<ModelInfo> Models,
        double? Temperature,
        int MaxTokens,
        bool UseMaxCompletionTokens,
        string ChatPath = "/v1/chat/completions",
        string HealthPath = "/v1/models");

    /// <summary>
    /// Chat completions in the OpenAI wire format: DeepSeek, OpenAI, OpenRouter, Groq, a local Ollama...
    /// Streams text and tool-call deltas; usage (including cached prompt tokens) comes on the last chunk.
    /// </summary>
    public class OpenAICompatibleClient : ILLMClient
    {
        private readonly HttpClient _httpClient;
        private readonly OpenAIProviderSettings _settings;
        private readonly ILogger _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            // OpenAI format is snake_case: tool_calls, finish_reason, max_tokens...
            // CamelCase silently dropped every streamed tool call.
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>DI constructor for the configurable "OpenAI" provider (OpenAI, OpenRouter, Groq, Ollama...).</summary>
        public OpenAICompatibleClient(HttpClient httpClient, IOptions<OpenAICompatibleOptions> options, ILogger<OpenAICompatibleClient> logger)
            : this(httpClient, options.Value.ToSettings(), logger)
        {
        }

        protected OpenAICompatibleClient(HttpClient httpClient, OpenAIProviderSettings settings, ILogger logger)
        {
            _httpClient = httpClient;
            _settings = settings;
            _logger = logger;
        }

        public string ProviderName => _settings.ProviderName;
        public string DefaultModel => _settings.DefaultModel;

        public Task<List<ModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings.Models.ToList());

        private DeepSeekRequest NewRequest(List<ChatMessage> messages, List<ToolDefinition>? tools, string? model, bool stream) => new()
        {
            Model = model ?? DefaultModel,
            Messages = messages,
            Temperature = _settings.Temperature,
            MaxTokens = _settings.UseMaxCompletionTokens ? null : _settings.MaxTokens,
            MaxCompletionTokens = _settings.UseMaxCompletionTokens ? _settings.MaxTokens : null,
            Stream = stream,
            StreamOptions = stream ? new StreamOptions { IncludeUsage = true } : null,
            Tools = tools,
            ToolChoice = tools is { Count: > 0 } ? "auto" : null
        };

        public async Task<LLMResponse> SendMessageAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            string? model = null,
            CancellationToken cancellationToken = default)
        {
            var request = NewRequest(messages, tools, model, stream: false);
            var jsonContent = JsonSerializer.Serialize(request, JsonOptions);
            _logger.LogInformation(
                "[TRACE] LLM_REQUEST | Provider={Provider} | Model={Model} | MsgCount={MsgCount} | ToolCount={ToolCount} | BodyLen={BodyLen}",
                ProviderName, request.Model, request.Messages.Count, tools?.Count ?? 0, jsonContent.Length);

            try
            {
                var response = await _httpClient.PostAsync(_settings.ChatPath,
                    new StringContent(jsonContent, Encoding.UTF8, "application/json"), cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                    throw new LLMException(
                        $"{ProviderName} API error {(int)response.StatusCode} ({response.StatusCode}): {ErrorMessage(responseBody)}",
                        ProviderName, (int)response.StatusCode, responseBody);

                DeepSeekResponse? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<DeepSeekResponse>(responseBody, JsonOptions);
                }
                catch (JsonException jex)
                {
                    _logger.LogError(jex, "Failed to deserialize the {Provider} response", ProviderName);
                    throw new HttpRequestException($"{ProviderName} deserialization failed: {jex.Message}");
                }

                var msg = parsed?.Choices?.FirstOrDefault()?.Message;
                return new LLMResponse
                {
                    Content = msg?.Content ?? string.Empty,
                    ToolCalls = msg?.ToolCalls,
                    InputTokens = parsed?.Usage?.PromptTokens,
                    OutputTokens = parsed?.Usage?.CompletionTokens
                };
            }
            catch (TaskCanceledException ex)
            {
                throw new LLMException($"{ProviderName} request timed out", ProviderName, ex);
            }
        }

        public async IAsyncEnumerable<LLMStreamChunk> StreamMessageAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            string? model = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = NewRequest(messages, tools, model, stream: true);
            var json = JsonSerializer.Serialize(request, JsonOptions);
            _logger.LogInformation(
                "[TRACE] LLM_STREAM | Provider={Provider} | Model={Model} | MsgCount={MsgCount} | ToolCount={ToolCount} | BodyLen={BodyLen}",
                ProviderName, request.Model, request.Messages.Count, tools?.Count ?? 0, json.Length);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, _settings.ChatPath)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Keep the provider's reason (bad key, context too long, rate limit...): the chat shows ex.Message
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                throw new LLMException(
                    $"{ProviderName} API error {(int)response.StatusCode} ({response.StatusCode}): {ErrorMessage(errorBody)}",
                    ProviderName, (int)response.StatusCode, errorBody);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            string? finishReason = null;

            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;

                var data = line["data: ".Length..];
                if (data == "[DONE]") break;

                StreamChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<StreamChunk>(data, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                // The usage chunk has no choices: report it, then keep reading until [DONE]
                if (chunk?.Usage != null)
                {
                    yield return new LLMStreamChunk
                    {
                        InputTokens = chunk.Usage.PromptTokens,
                        OutputTokens = chunk.Usage.CompletionTokens,
                        CacheHitTokens = chunk.Usage.CachedTokens
                    };
                }

                if (chunk?.Choices == null || chunk.Choices.Count == 0) continue;
                var choice = chunk.Choices[0];
                if (choice.FinishReason != null) finishReason = choice.FinishReason;
                var delta = choice.Delta;
                if (delta == null) continue;

                var result = new LLMStreamChunk();
                if (!string.IsNullOrEmpty(delta.Content))
                    result.Content = delta.Content;

                if (delta.ToolCalls is { Count: > 0 })
                {
                    result.ToolCallDeltas = delta.ToolCalls.Select(tc => new ToolCallDelta
                    {
                        Index = tc.Index,
                        Id = tc.Id,
                        Name = tc.Function?.Name,
                        Arguments = tc.Function?.Arguments
                    }).ToList();
                }

                if (result.Content != null || result.ToolCallDeltas != null)
                    yield return result;
            }

            // OpenAI's "length" is Claude's "max_tokens": the reply (and any tool call in it) was cut off
            if (finishReason != null)
                yield return new LLMStreamChunk { StopReason = finishReason == "length" ? "max_tokens" : finishReason };
        }

        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Lists models: checks reachability and the API key without spending tokens
                using var response = await _httpClient.GetAsync(_settings.HealthPath, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        public int EstimateTokens(string text) => text.Length / 4;

        /// <summary>The "error.message" of an OpenAI-style error body, or the (shortened) raw body.</summary>
        internal static string ErrorMessage(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? body;
                    if (error.TryGetProperty("message", out var message)) return message.GetString() ?? body;
                }
            }
            catch (JsonException) { /* not JSON: use the raw text */ }
            return string.IsNullOrWhiteSpace(body) ? "(no details)" : body.Length > 500 ? body[..500] + "…" : body;
        }

        private class StreamChunk
        {
            public List<StreamChoice>? Choices { get; set; }
            public UsageInfo? Usage { get; set; }
        }

        private class StreamChoice
        {
            public StreamDelta? Delta { get; set; }
            public string? FinishReason { get; set; }
        }

        private class StreamDelta
        {
            public string? Content { get; set; }
            public List<StreamToolCallDelta>? ToolCalls { get; set; }
        }

        private class StreamToolCallDelta
        {
            public int Index { get; set; }
            public string? Id { get; set; }
            public StreamFunctionDelta? Function { get; set; }
        }

        private class StreamFunctionDelta
        {
            public string? Name { get; set; }
            public string? Arguments { get; set; }
        }
    }
}
