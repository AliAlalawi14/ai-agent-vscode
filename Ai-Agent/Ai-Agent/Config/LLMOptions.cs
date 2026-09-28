namespace Ai_Agent.Config
{

    // This class matches your appsettings.json structure
    public class LLMOptions
    {
        // The section name in appsettings.json: "DeepSeek"
        public const string SectionName = "DeepSeek";

        public string ApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;

        // Optional price overrides, USD per 1K tokens, keyed by model id (e.g. "DeepSeek:Pricing:deepseek-chat:Input").
        // Unset models use CostTracker's built-in list prices; check your provider's current prices.
        public Dictionary<string, ModelPriceOptions> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class ModelPriceOptions
    {
        public double Input { get; set; }          // cache miss
        public double CachedInput { get; set; }    // cache hit
        public double Output { get; set; }
    }

}