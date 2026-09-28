namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Estimates token count for the conversation.
    /// Simple character-based estimation (no external library needed).
    /// </summary>
    public class TokenCounter
    {
        /// <summary>
        /// Rough estimate: 1 token ≈ 3.5 characters for mixed code/text.
        /// </summary>
        private const double CharsPerToken = 3.5;

        /// <summary>
        /// Estimates total tokens across all messages.
        /// </summary>
        public int EstimateTokens(List<Models.ChatMessage> messages)
        {
            var totalChars = 0;
            foreach (var message in messages)
            {
                totalChars += message.Content?.Length ?? 0;
                totalChars += message.Role?.Length ?? 0;
                // Tool-call arguments count too (write_file sends a whole file here)
                if (message.ToolCalls != null)
                    foreach (var call in message.ToolCalls)
                        totalChars += call.Function.Name.Length + call.Function.Arguments.Length;
            }
            return (int)(totalChars / CharsPerToken);
        }

        /// <summary>
        /// Estimates tokens in a single string.
        /// </summary>
        public int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)(text.Length / CharsPerToken);
        }

        /// <summary>
        /// Checks if adding new content would exceed the limit.
        /// </summary>
        public bool WouldExceedLimit(List<Models.ChatMessage> messages, int maxTokens, int newTokens = 500)
        {
            var current = EstimateTokens(messages);
            return (current + newTokens) > maxTokens;
        }

        /// <summary>
        /// Returns usage percentage.
        /// </summary>
        public double GetUsagePercentage(List<Models.ChatMessage> messages, int maxTokens)
        {
            var current = EstimateTokens(messages);
            return (double)current / maxTokens * 100;
        }
    }
}
