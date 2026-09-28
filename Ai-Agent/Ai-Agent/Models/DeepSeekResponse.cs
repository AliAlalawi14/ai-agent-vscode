namespace Ai_Agent.Models
{
    public class DeepSeekResponse
    {
        public string Id { get; set; } = string.Empty;
        public List<Choice> Choices { get; set; } = new();
        public UsageInfo? Usage { get; set; }
    }

    public class Choice
    {
        public int Index { get; set; }
        public ResponseMessage Message { get; set; } = new();
        public string FinishReason { get; set; } = string.Empty;
    }

    public class ResponseMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<ToolCall>? ToolCalls { get; set; }
    }

    public class UsageInfo
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        // DeepSeek context caching: the part of PromptTokens served from the prefix cache (billed cheaper)
        public int? PromptCacheHitTokens { get; set; }

        // OpenAI's name for the same thing: usage.prompt_tokens_details.cached_tokens
        public PromptTokensDetails? PromptTokensDetails { get; set; }

        public int? CachedTokens => PromptCacheHitTokens ?? PromptTokensDetails?.CachedTokens;
    }

    public class PromptTokensDetails
    {
        public int? CachedTokens { get; set; }
    }
}
