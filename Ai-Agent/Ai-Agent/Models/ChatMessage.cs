using System.Text.Json.Serialization;

namespace Ai_Agent.Models
{
    public sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolCall>? ToolCalls { get; set; }

        [JsonPropertyName("tool_call_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolCallId { get; set; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// Provider reasoning blocks of an assistant turn (Claude's signed thinking blocks), echoed back unchanged
        /// on the next request of a tool loop. Never sent to OpenAI-format providers.
        /// </summary>
        [JsonIgnore]
        public List<ReasoningBlock>? Reasoning { get; set; }
    }

    /// <summary>One opaque reasoning block: thinking text + signature, or redacted data. Must be replayed byte-for-byte.</summary>
    public sealed class ReasoningBlock
    {
        public string? Thinking { get; set; }
        public string? Signature { get; set; }
        /// <summary>Set for a redacted thinking block (encrypted by the provider)</summary>
        public string? RedactedData { get; set; }
    }
}
