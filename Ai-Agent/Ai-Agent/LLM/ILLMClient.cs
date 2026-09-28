using Ai_Agent.Models;

namespace Ai_Agent.LLM
{
    public interface ILLMClient
    {
        /// <summary>
        /// Send a message and get a complete response.
        /// </summary>
        Task<LLMResponse> SendMessageAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            string? model = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Stream a response, yielding text chunks and tool call deltas.
        /// </summary>
        IAsyncEnumerable<LLMStreamChunk> StreamMessageAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            string? model = null,
            CancellationToken cancellationToken = default);

        Task<List<ModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

        string ProviderName { get; }
        string DefaultModel { get; }

        Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

        int EstimateTokens(string text);
    }

    public class ModelInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public int? MaxContextTokens { get; set; }
        public bool SupportsStreaming { get; set; } = true;
        public bool SupportsFunctionCalling { get; set; } = false;
        public Dictionary<string, object> Capabilities { get; set; } = new();
    }

    public class TokenUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens => InputTokens + OutputTokens;
    }

    public class LLMException : Exception
    {
        public string Provider { get; set; } = string.Empty;
        public int? StatusCode { get; set; }
        public string? ResponseBody { get; set; }

        public LLMException(string message, string provider, Exception? inner = null)
            : base(message, inner)
        {
            Provider = provider;
        }

        public LLMException(string message, string provider, int statusCode, string? responseBody = null)
            : base(message)
        {
            Provider = provider;
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
