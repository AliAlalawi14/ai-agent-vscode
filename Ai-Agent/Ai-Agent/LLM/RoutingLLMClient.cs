using Ai_Agent.Models;
using System.Runtime.CompilerServices;

namespace Ai_Agent.LLM
{
    /// <summary>
    /// The ILLMClient the agent uses: sends each request to the provider that owns the requested model
    /// (DeepSeek, an OpenAI-compatible provider, Claude...), or to the default provider (LLM:DefaultProvider)
    /// when no model is picked. The agent loop doesn't know which provider it talks to.
    /// </summary>
    public class RoutingLLMClient : ILLMClient
    {
        private readonly LLMProviderRegistry _registry;
        private readonly ILLMClient _default;

        public RoutingLLMClient(LLMProviderRegistry registry, string? defaultProvider)
        {
            _registry = registry;
            _default = (defaultProvider != null ? registry.GetProvider(defaultProvider) : null)
                       ?? registry.GetDefaultProvider()
                       ?? throw new InvalidOperationException(
                           "No LLM provider is configured. Set DeepSeek:ApiKey, Anthropic:ApiKey (or ANTHROPIC_API_KEY), " +
                           "or an OpenAI-compatible provider (Providers:Custom:N; in VS Code: AI Agent: Add Provider).");
        }

        public string ProviderName => "router";
        public string DefaultModel => _default.DefaultModel;

        /// <summary>The provider serving <paramref name="model"/> (null or unknown → the default provider).</summary>
        public async Task<ILLMClient> ProviderForAsync(string? model, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(model)) return _default;
            foreach (var provider in _registry.GetAllProviders())
            {
                var models = await provider.GetAvailableModelsAsync(cancellationToken);
                if (models.Any(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase)))
                    return provider;
            }
            return _default;
        }

        public async Task<LLMResponse> SendMessageAsync(
            List<ChatMessage> messages, List<ToolDefinition>? tools = null, string? model = null,
            CancellationToken cancellationToken = default) =>
            await (await ProviderForAsync(model, cancellationToken)).SendMessageAsync(messages, tools, model, cancellationToken);

        public async IAsyncEnumerable<LLMStreamChunk> StreamMessageAsync(
            List<ChatMessage> messages, List<ToolDefinition>? tools = null, string? model = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var provider = await ProviderForAsync(model, cancellationToken);
            await foreach (var chunk in provider.StreamMessageAsync(messages, tools, model, cancellationToken))
                yield return chunk;
        }

        public Task<List<ModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
            _registry.GetAllModelsAsync(cancellationToken);

        public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default) =>
            _default.HealthCheckAsync(cancellationToken);

        public int EstimateTokens(string text) => _default.EstimateTokens(text);
    }
}
