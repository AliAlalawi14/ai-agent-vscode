using Ai_Agent.Config;
using Microsoft.Extensions.Options;

namespace Ai_Agent.LLM
{
    /// <summary>DeepSeek: an OpenAI-format provider with its own models and cache-hit reporting (prompt_cache_hit_tokens).</summary>
    public class DeepSeekClient : OpenAICompatibleClient
    {
        public DeepSeekClient(HttpClient httpClient, IOptions<LLMOptions> options, ILogger<DeepSeekClient> logger)
            : base(httpClient, Settings(options.Value), logger)
        {
        }

        private static OpenAIProviderSettings Settings(LLMOptions options) => new(
            ProviderName: "deepseek",
            DefaultModel: string.IsNullOrWhiteSpace(options.Model) ? "deepseek-chat" : options.Model,
            Models: new List<ModelInfo>
            {
                new() { Id = "deepseek-chat", Name = "DeepSeek V3", Provider = "deepseek", MaxContextTokens = 64000, SupportsFunctionCalling = true },
                new()
                {
                    Id = "deepseek-reasoner", Name = "DeepSeek R1", Provider = "deepseek", MaxContextTokens = 64000,
                    SupportsFunctionCalling = false, Capabilities = new() { ["reasoning"] = true }
                }
            },
            Temperature: 0.1,
            MaxTokens: 8000,
            UseMaxCompletionTokens: false,
            HealthPath: "/models");
    }
}
