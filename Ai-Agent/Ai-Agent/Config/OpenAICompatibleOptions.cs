using Ai_Agent.LLM;

namespace Ai_Agent.Config
{
    /// <summary>
    /// One OpenAI-compatible provider ("Providers:Custom:N"): Gemini, Mistral, xAI, Groq, OpenRouter, Together, Fireworks,
    /// Azure OpenAI, Ollama, LM Studio, vLLM... Nearly every provider speaks this format; add as many as you like.
    /// The extension's "AI Agent: Add Provider" wizard fills these in (with presets) and passes them as environment variables.
    /// </summary>
    public class CustomProviderOptions
    {
        public const string SectionName = "Providers:Custom";

        /// <summary>Unique name, shown in the model picker and used for routing (e.g. "gemini", "ollama")</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>FULL base URL including the version path, e.g. https://api.mistral.ai/v1 or http://localhost:11434/v1</summary>
        public string BaseUrl { get; set; } = string.Empty;

        public string ApiKey { get; set; } = string.Empty;

        /// <summary>bearer (most providers) | api-key (Azure OpenAI) | none (local servers)</summary>
        public string Auth { get; set; } = "bearer";

        public List<OpenAIModelOptions> Models { get; set; } = new();

        /// <summary>Default model; the first of Models when empty</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>Null = the provider's default (some models reject anything else)</summary>
        public double? Temperature { get; set; }

        public int MaxTokens { get; set; } = 16000;

        /// <summary>true for OpenAI's newer models, which reject max_tokens</summary>
        public bool UseMaxCompletionTokens { get; set; }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Name) && Uri.TryCreate(BaseUrl, UriKind.Absolute, out _) && Models.Count > 0;

        /// <summary>The base URL as an HttpClient BaseAddress: relative paths ("chat/completions") append to it.</summary>
        public Uri BaseAddress => new(BaseUrl.TrimEnd('/') + "/");

        public OpenAIProviderSettings ToSettings() => new(
            ProviderName: Name,
            DefaultModel: string.IsNullOrWhiteSpace(Model) ? Models.FirstOrDefault()?.Id ?? string.Empty : Model,
            Models: Models.Select(m => new ModelInfo
            {
                Id = m.Id,
                Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
                Provider = Name,
                MaxContextTokens = m.ContextTokens,
                SupportsFunctionCalling = m.SupportsTools
            }).ToList(),
            Temperature: Temperature,
            MaxTokens: MaxTokens,
            UseMaxCompletionTokens: UseMaxCompletionTokens,
            ChatPath: "chat/completions",   // relative to the full BaseUrl (Gemini: /v1beta/openai, Azure: /openai/v1...)
            HealthPath: "models",
            ApiKey: ApiKey,
            AuthStyle: string.IsNullOrWhiteSpace(ApiKey) ? "none" : Auth.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// The original single OpenAI-compatible slot ("OpenAI" section, base URL WITHOUT /v1). Still accepted:
    /// it becomes one entry of the provider list.
    /// </summary>
    public class OpenAICompatibleOptions
    {
        public const string SectionName = "OpenAI";

        public string ProviderName { get; set; } = "openai";
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public List<OpenAIModelOptions> Models { get; set; } = new();
        public double? Temperature { get; set; }
        public int MaxTokens { get; set; } = 16000;
        public bool UseMaxCompletionTokens { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && Models.Count > 0;

        public CustomProviderOptions ToCustomProvider() => new()
        {
            Name = string.IsNullOrWhiteSpace(ProviderName) ? "openai" : ProviderName,
            BaseUrl = BaseUrl.TrimEnd('/') + "/v1",
            ApiKey = ApiKey,
            Auth = "bearer",
            Model = Model,
            Models = Models,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            UseMaxCompletionTokens = UseMaxCompletionTokens
        };
    }

    public class OpenAIModelOptions
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>The agent needs tool calling; models without it are listed but never used for a run</summary>
        public bool SupportsTools { get; set; } = true;
        public int? ContextTokens { get; set; }
    }
}
