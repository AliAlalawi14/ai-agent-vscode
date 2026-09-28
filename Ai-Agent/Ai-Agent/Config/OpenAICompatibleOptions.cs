using Ai_Agent.LLM;

namespace Ai_Agent.Config
{
    /// <summary>
    /// Any OpenAI-format provider ("OpenAI" section): OpenAI, OpenRouter, Groq, or a local Ollama. Registered when BaseUrl
    /// and at least one model are set. Examples:
    ///   OpenAI:   BaseUrl https://api.openai.com, UseMaxCompletionTokens true, Temperature unset
    ///   Ollama:   BaseUrl http://localhost:11434, ApiKey empty, ProviderName "ollama", Models [{ "Id": "qwen3:14b" }]
    ///   OpenRouter: BaseUrl https://openrouter.ai/api
    /// </summary>
    public class OpenAICompatibleOptions
    {
        public const string SectionName = "OpenAI";

        /// <summary>Shown in the model picker and used for routing, e.g. "openai", "ollama", "openrouter"</summary>
        public string ProviderName { get; set; } = "openai";

        /// <summary>Base URL WITHOUT /v1 (the client calls /v1/chat/completions)</summary>
        public string BaseUrl { get; set; } = string.Empty;

        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Default model; the first of Models when empty</summary>
        public string Model { get; set; } = string.Empty;

        public List<OpenAIModelOptions> Models { get; set; } = new();

        /// <summary>Null = the provider's default (required by OpenAI's newer models)</summary>
        public double? Temperature { get; set; }

        public int MaxTokens { get; set; } = 16000;

        /// <summary>true for OpenAI's newer models, which reject max_tokens</summary>
        public bool UseMaxCompletionTokens { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && Models.Count > 0;

        public OpenAIProviderSettings ToSettings() => new(
            ProviderName,
            string.IsNullOrWhiteSpace(Model) ? Models.FirstOrDefault()?.Id ?? string.Empty : Model,
            Models.Select(m => new ModelInfo
            {
                Id = m.Id,
                Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
                Provider = ProviderName,
                MaxContextTokens = m.ContextTokens,
                SupportsFunctionCalling = m.SupportsTools
            }).ToList(),
            Temperature,
            MaxTokens,
            UseMaxCompletionTokens);
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
