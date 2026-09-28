namespace Ai_Agent.Config
{
    /// <summary>
    /// Claude provider ("Anthropic" section). Registered only when an API key is available: Anthropic:ApiKey,
    /// or the ANTHROPIC_API_KEY environment variable (keep keys in user-secrets / env, not in appsettings files).
    /// </summary>
    public class AnthropicOptions
    {
        public const string SectionName = "Anthropic";
        public const string DefaultModelId = "claude-opus-5";

        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Model used when the request doesn't pick one</summary>
        public string Model { get; set; } = DefaultModelId;

        /// <summary>low | medium | high | xhigh | max. Coding agents do well at high/xhigh; not sent to Haiku 4.5</summary>
        public string Effort { get; set; } = "high";

        /// <summary>Streamed, so a high ceiling doesn't risk HTTP timeouts; replies that hit it never run their tool calls</summary>
        public int MaxTokens { get; set; } = 64000;

        /// <summary>claude-opus-5: let a fallback model answer when the safety filter declines (server-side, beta)</summary>
        public bool RefusalFallbacks { get; set; } = true;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
    }
}
