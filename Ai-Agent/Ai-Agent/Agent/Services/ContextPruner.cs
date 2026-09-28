using Ai_Agent.Models;
using System.Text.Json;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Keeps a long agent run inside the model's context window without breaking it:
    /// an assistant message with tool_calls must stay together with its tool results (the API rejects
    /// orphans), so whole "rounds" are compacted or dropped, never split.
    ///
    /// 1. Compact: old tool results (file contents, command output) shrink to a short preview, and big
    ///    string arguments of old calls (write_file content) become a note. Recent rounds stay intact.
    /// 2. Only if that is not enough: drop the oldest complete rounds, keeping the system prompt,
    ///    the conversation up to the current request, and the last rounds.
    /// </summary>
    public class ContextPruner
    {
        private readonly ILogger<ContextPruner> _logger;
        private readonly TokenCounter _tokenCounter;

        private const int KeepRecentRounds = 4;
        private const int CompactResultAbove = 600;       // chars
        private const int ResultPreviewChars = 300;
        private const int CompactArgumentAbove = 1500;    // chars per string argument

        public ContextPruner(ILogger<ContextPruner> logger, TokenCounter tokenCounter)
        {
            _logger = logger;
            _tokenCounter = tokenCounter;
        }

        /// <summary>Returns a (possibly) smaller copy of the messages that fits in targetTokens when possible.</summary>
        public List<ChatMessage> Prune(List<ChatMessage> messages, int targetTokens)
        {
            var before = _tokenCounter.EstimateTokens(messages);
            if (before <= targetTokens) return messages;

            // The prefix (system, history, current user request) ends at the first tool-calling assistant message
            var firstRound = messages.FindIndex(m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 });
            if (firstRound < 0) return messages;

            var prefix = messages.Take(firstRound).ToList();
            var rounds = SplitRounds(messages.Skip(firstRound).ToList());

            // 1. Compact everything except the most recent rounds
            var compacted = rounds.Select((round, i) =>
                i < rounds.Count - KeepRecentRounds ? round.Select(Compact).ToList() : round).ToList();
            var result = prefix.Concat(compacted.SelectMany(r => r)).ToList();

            // 2. Still too big: drop the oldest whole rounds (keep at least the last two)
            var dropped = 0;
            while (_tokenCounter.EstimateTokens(result) > targetTokens && compacted.Count - dropped > 2)
            {
                dropped++;
                var note = new ChatMessage
                {
                    Role = "user",
                    Content = $"(Context note: {dropped} earlier tool round(s) of this request were removed to save space. " +
                              "Re-read files if you need their current content.)"
                };
                result = prefix.Append(note).Concat(compacted.Skip(dropped).SelectMany(r => r)).ToList();
            }

            _logger.LogInformation(
                "Context pruned: ~{Before} → ~{After} tokens ({Rounds} rounds, {Dropped} dropped, {Compacted} compacted)",
                before, _tokenCounter.EstimateTokens(result), rounds.Count, dropped, Math.Max(0, rounds.Count - KeepRecentRounds));
            return result;
        }

        /// <summary>Each round = one assistant message plus the tool/other messages that follow it.</summary>
        private static List<List<ChatMessage>> SplitRounds(List<ChatMessage> messages)
        {
            var rounds = new List<List<ChatMessage>>();
            foreach (var message in messages)
            {
                if (message.Role == "assistant" || rounds.Count == 0)
                    rounds.Add(new List<ChatMessage>());
                rounds[^1].Add(message);
            }
            return rounds;
        }

        private static ChatMessage Compact(ChatMessage message)
        {
            if (message.Role == "tool" && message.Content.Length > CompactResultAbove)
            {
                return new ChatMessage
                {
                    Role = message.Role,
                    ToolCallId = message.ToolCallId,
                    Name = message.Name,
                    Content = $"[Earlier result trimmed to save context ({message.Content.Length} chars). Start:]\n" +
                              message.Content[..ResultPreviewChars] + "\n[...]"
                };
            }

            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 } calls &&
                calls.Any(c => c.Function.Arguments.Length > CompactArgumentAbove))
            {
                return new ChatMessage
                {
                    Role = message.Role,
                    Content = message.Content,
                    Reasoning = message.Reasoning,   // signed thinking blocks are replayed unchanged
                    ToolCalls = calls.Select(c => new ToolCall
                    {
                        Id = c.Id,
                        Type = c.Type,
                        Function = new ToolCallFunction { Name = c.Function.Name, Arguments = CompactArguments(c.Function.Arguments) }
                    }).ToList()
                };
            }

            return message;
        }

        /// <summary>Shortens long string values but keeps the arguments valid JSON.</summary>
        private static string CompactArguments(string argumentsJson)
        {
            if (argumentsJson.Length <= CompactArgumentAbove) return argumentsJson;
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var shortened = new Dictionary<string, object?>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    shortened[prop.Name] = prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString()!.Length > 200
                        ? $"({prop.Value.GetString()!.Length} chars, trimmed to save context)"
                        : JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                }
                return JsonSerializer.Serialize(shortened);
            }
            catch (JsonException)
            {
                return "{}";
            }
        }
    }
}
