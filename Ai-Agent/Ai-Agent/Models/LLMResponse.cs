using System.Text.Json.Serialization;

namespace Ai_Agent.Models;

/// <summary>
/// Unified response from any LLM provider.
/// Contains both text content and any tool calls the model decided to make.
/// </summary>
public class LLMResponse
{
    public string Content { get; set; } = string.Empty;
    public List<ToolCall>? ToolCalls { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
}

/// <summary>
/// A single tool call from the LLM (OpenAI-compatible format).
/// </summary>
public class ToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ToolCallFunction Function { get; set; } = new();
}

public class ToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>
/// Represents an incremental tool call delta during streaming.
/// </summary>
public class ToolCallDelta
{
    public int Index { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }
}

/// <summary>
/// A single chunk from a streaming LLM response.
/// May contain text content, tool call deltas, or both.
/// </summary>
public class LLMStreamChunk
{
    public string? Content { get; set; }
    public List<ToolCallDelta>? ToolCallDeltas { get; set; }

    // Set on the final usage chunk of a stream
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    // Part of InputTokens served from the provider's prompt cache (null if the provider doesn't report it)
    public int? CacheHitTokens { get; set; }

    // Set on the final chunk when the provider reports why it stopped ("max_tokens", "refusal", "tool_use"...)
    public string? StopReason { get; set; }

    // Reasoning blocks of this reply that must be sent back with it (Claude thinking blocks), on the final chunk
    public List<ReasoningBlock>? Reasoning { get; set; }
}

/// <summary>
/// Defines a tool for the LLM's function calling API (OpenAI-compatible).
/// </summary>
public class ToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public FunctionDefinition Function { get; set; } = new();
}

/// <summary>
/// The function schema for a tool definition.
/// </summary>
public class FunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public object Parameters { get; set; } = new();
}
