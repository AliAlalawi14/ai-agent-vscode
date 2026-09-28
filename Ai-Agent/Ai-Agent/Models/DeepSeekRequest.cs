namespace Ai_Agent.Models
{
    /// <summary>OpenAI chat-completions request (DeepSeek, OpenAI, OpenRouter, Groq, Ollama). Null fields are not sent.</summary>
    public class DeepSeekRequest
    {
        public string Model { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();

        // Null = provider default (newer OpenAI models reject anything but their default temperature)
        public double? Temperature { get; set; }

        // Classic providers take max_tokens; newer OpenAI models require max_completion_tokens instead
        public int? MaxTokens { get; set; }
        public int? MaxCompletionTokens { get; set; }

        public bool Stream { get; set; } = false;
        public List<ToolDefinition>? Tools { get; set; }
        public string? ToolChoice { get; set; } = "auto";

        // stream_options.include_usage: the last stream chunk then carries the token usage
        public StreamOptions? StreamOptions { get; set; }
    }

    public class StreamOptions
    {
        public bool IncludeUsage { get; set; } = true;
    }
}
