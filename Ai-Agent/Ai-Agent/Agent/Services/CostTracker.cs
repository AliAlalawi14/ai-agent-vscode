using Ai_Agent.Config;
using Microsoft.Extensions.Options;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Tracks LLM costs based on token usage and model pricing.
    /// </summary>
    public class CostTracker
    {
        private readonly ILogger<CostTracker> _logger;
        private readonly Dictionary<string, ModelPricing> _pricing;

        public CostTracker(IOptions<LLMOptions> options, ILogger<CostTracker> logger)
        {
            _logger = logger;

            // Approximate list prices, USD per 1K tokens (input cache miss / input cache hit / output).
            // Override any of them in config: DeepSeek:Pricing:<model>:Input|CachedInput|Output
            _pricing = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
            {
                ["deepseek-chat"] = new(0.00027, 0.00007, 0.0011),
                ["deepseek-reasoner"] = new(0.00055, 0.00014, 0.00219),
                // Claude: cache reads ~0.1x input. Cache WRITES (1.25x, first request of a prefix) aren't modelled,
                // so a conversation's first answer is slightly underestimated.
                ["claude-opus-5"] = new(0.005, 0.0005, 0.025),
                ["claude-sonnet-5"] = new(0.002, 0.0002, 0.010),
                ["claude-haiku-4-5"] = new(0.001, 0.0001, 0.005),
                ["gpt-4o"] = new(0.0025, 0.00125, 0.01),
                ["gpt-4o-mini"] = new(0.00015, 0.000075, 0.0006),
                ["claude-3-5-sonnet"] = new(0.003, 0.0003, 0.015),
                ["claude-3-haiku"] = new(0.00025, 0.00003, 0.00125),
            };

            foreach (var (model, price) in options.Value.Pricing)
                _pricing[model] = new ModelPricing(price.Input, price.CachedInput, price.Output);
        }

        /// <summary>
        /// Calculate cost for a request. <paramref name="cachedInputTokens"/> is the part of
        /// <paramref name="inputTokens"/> served from the provider's prompt cache (billed at the cheaper rate).
        /// </summary>
        public CostEstimate CalculateCost(string model, int inputTokens, int outputTokens, int cachedInputTokens = 0)
        {
            var pricing = _pricing.GetValueOrDefault(model)
                          ?? _pricing.GetValueOrDefault(NormalizeModelName(model))
                          ?? new ModelPricing(0, 0, 0);

            var cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
            var inputCost = ((inputTokens - cached) / 1000.0) * pricing.InputPricePer1K +
                            (cached / 1000.0) * pricing.CachedInputPricePer1K;
            var outputCost = (outputTokens / 1000.0) * pricing.OutputPricePer1K;
            var totalCost = inputCost + outputCost;

            return new CostEstimate
            {
                Model = model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                InputCost = inputCost,
                OutputCost = outputCost,
                TotalCost = totalCost,
                Currency = "USD"
            };
        }

        /// <summary>
        /// Format cost for display/logging.
        /// </summary>
        public string FormatCost(CostEstimate cost)
        {
            return $"${cost.TotalCost:F6} (Input: ${cost.InputCost:F6}, Output: ${cost.OutputCost:F6})";
        }

        /// <summary>
        /// Log cost information for a request.
        /// </summary>
        public void LogCost(string correlationId, CostEstimate cost)
        {
            _logger.LogInformation(
                "Cost for {CorrelationId}: {FormattedCost} | Model: {Model} | Tokens: {Input}/{Output}",
                correlationId,
                FormatCost(cost),
                cost.Model,
                cost.InputTokens,
                cost.OutputTokens);
        }

        private string NormalizeModelName(string model)
        {
            // Handle common model name variations
            if (model.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                if (model.Contains("reasoner", StringComparison.OrdinalIgnoreCase) ||
                    model.Contains("r1", StringComparison.OrdinalIgnoreCase))
                    return "deepseek-reasoner";
                return "deepseek-chat";
            }

            return model.ToLowerInvariant();
        }

        private record ModelPricing(double InputPricePer1K, double CachedInputPricePer1K, double OutputPricePer1K);
    }

    /// <summary>
    /// Cost calculation result.
    /// </summary>
    public class CostEstimate
    {
        public string Model { get; set; } = string.Empty;
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public double InputCost { get; set; }
        public double OutputCost { get; set; }
        public double TotalCost { get; set; }
        public string Currency { get; set; } = "USD";
    }
}
