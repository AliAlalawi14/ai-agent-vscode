using System.Text.Json;

namespace Ai_Agent.Agent.Services
{
    /// <summary>Turns a tool call's JSON arguments into the name → string map the tools take.</summary>
    public static class ToolArguments
    {
        /// <summary>
        /// Strings are unescaped (GetString: "\n" becomes a real newline); numbers, booleans, arrays and objects
        /// are kept as JSON text ("5", "true", "[...]"). Invalid JSON (usually a reply cut off mid-arguments)
        /// returns an empty map and an error to send back to the model instead of running the tool.
        /// </summary>
        public static Dictionary<string, string> Parse(string argumentsJson, out string? error)
        {
            var result = new Dictionary<string, string>();
            error = null;
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return result;

            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "Tool arguments must be a JSON object.";
                    return result;
                }

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.GetRawText();
                }
            }
            catch (JsonException ex)
            {
                error = $"Tool arguments were not valid JSON ({ex.Message}). " +
                        "If you were writing a large file, make smaller edit_file changes instead.";
            }

            return result;
        }
    }
}
