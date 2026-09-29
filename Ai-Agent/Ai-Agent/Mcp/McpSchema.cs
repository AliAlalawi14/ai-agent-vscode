using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace Ai_Agent.Mcp
{
    /// <summary>
    /// Translation between MCP tools and the agent's tool format: exposed names, provider-safe parameter
    /// schemas, typed arguments, and results as text.
    /// </summary>
    public static class McpSchema
    {
        public const string Prefix = "mcp__";
        private const int MaxToolNameLength = 64;   // OpenAI / Anthropic limit for function names

        /// <summary>"mcp__server__tool", only [A-Za-z0-9_-], at most 64 characters (a hash keeps long names unique).</summary>
        public static string ExposedName(string server, string tool)
        {
            var toolPart = Regex.Replace(tool, "[^A-Za-z0-9_-]", "_");
            var name = $"{Prefix}{server}__{toolPart}";
            if (name.Length <= MaxToolNameLength) return name;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool)))[..6].ToLowerInvariant();
            var room = MaxToolNameLength - Prefix.Length - server.Length - 2 - hash.Length - 1;
            return room > 3
                ? $"{Prefix}{server}__{toolPart[..room]}_{hash}"
                : $"{Prefix}{server[..Math.Min(server.Length, 20)]}__{hash}";
        }

        /// <summary>
        /// The tool's input schema, normalized so every provider accepts it: local $refs inlined, type unions
        /// reduced to one type, and keywords some providers reject (Gemini in particular) removed.
        /// </summary>
        public static (Dictionary<string, JsonNode?> Properties, HashSet<string> Required) NormalizeInputSchema(JsonElement inputSchema)
        {
            var properties = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (inputSchema.ValueKind != JsonValueKind.Object) return (properties, required);

            var root = JsonNode.Parse(inputSchema.GetRawText())!.AsObject();
            var definitions = (root["$defs"] ?? root["definitions"]) as JsonObject;

            if (root["properties"] is JsonObject props)
                foreach (var (name, schema) in props)
                    properties[name] = Normalize(schema, definitions, depth: 0);

            if (root["required"] is JsonArray req)
                foreach (var r in req)
                    if (r?.GetValueKind() == JsonValueKind.String) required.Add(r.GetValue<string>());

            return (properties, required);
        }

        private static readonly HashSet<string> DroppedKeywords = new(StringComparer.Ordinal)
        {
            "$schema", "$id", "$comment", "$defs", "definitions", "$anchor", "additionalProperties",
            "default", "examples", "format", "pattern", "patternProperties", "unevaluatedProperties",
            "propertyNames", "const", "contentEncoding", "contentMediaType", "readOnly", "writeOnly", "deprecated", "title"
        };

        private static JsonNode? Normalize(JsonNode? node, JsonObject? definitions, int depth)
        {
            if (node is not JsonObject obj) return node?.DeepClone();
            if (depth > 8) return new JsonObject { ["type"] = "object" };

            // Inline "#/$defs/X" / "#/definitions/X"; anything else becomes a plain object
            if (obj["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
            {
                var key = reference.StartsWith("#/$defs/") ? reference["#/$defs/".Length..]
                        : reference.StartsWith("#/definitions/") ? reference["#/definitions/".Length..] : null;
                var target = key != null ? definitions?[key] : null;
                var resolved = target != null ? Normalize(target, definitions, depth + 1)!.AsObject() : new JsonObject { ["type"] = "object" };
                if (obj["description"] is JsonNode d && resolved["description"] == null) resolved["description"] = d.DeepClone();
                return resolved;
            }

            var result = new JsonObject();
            foreach (var (key, value) in obj)
            {
                if (DroppedKeywords.Contains(key) || key == "$ref") continue;
                switch (key)
                {
                    case "type" when value is JsonArray types:
                        // ["string","null"] → "string" (+ nullable): one type is accepted everywhere
                        var first = types.Select(t => t?.GetValue<string>()).FirstOrDefault(t => t != null && t != "null");
                        result["type"] = first ?? "string";
                        if (types.Any(t => t?.GetValue<string>() == "null")) result["nullable"] = true;
                        break;
                    case "properties" when value is JsonObject props:
                        var normalized = new JsonObject();
                        foreach (var (name, schema) in props) normalized[name] = Normalize(schema, definitions, depth + 1);
                        result["properties"] = normalized;
                        break;
                    case "items":
                        result["items"] = Normalize(value, definitions, depth + 1);
                        break;
                    case "anyOf" or "oneOf" or "allOf" when value is JsonArray options:
                        result[key] = new JsonArray(options.Select(o => Normalize(o, definitions, depth + 1)).ToArray());
                        break;
                    default:
                        result[key] = value?.DeepClone();
                        break;
                }
            }
            // An array without "items" is rejected by some providers
            if (result["type"]?.GetValue<string>() == "array" && result["items"] == null) result["items"] = new JsonObject();
            return result;
        }

        /// <summary>The JSON type a parameter expects ("string", "integer"...), or null when unknown.</summary>
        public static string? TypeOf(JsonNode? schema) =>
            schema is JsonObject o && o["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;

        /// <summary>
        /// Arguments arrive as strings (nested values as raw JSON text). String parameters stay strings;
        /// everything else is parsed back to JSON, falling back to the string (the server then reports the problem).
        /// </summary>
        public static Dictionary<string, object?> ToArguments(Dictionary<string, string> parameters, IReadOnlyDictionary<string, JsonNode?> schemas)
        {
            var args = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, raw) in parameters)
            {
                schemas.TryGetValue(name, out var schema);
                if (TypeOf(schema) == "string")
                {
                    args[name] = raw;
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    args[name] = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    args[name] = raw;
                }
            }
            return args;
        }

        /// <summary>A tool result as text for the model: text blocks joined, binary content summarized.</summary>
        public static string ResultToText(CallToolResult result, int maxChars)
        {
            var parts = new List<string>();
            foreach (var block in result.Content ?? new List<ContentBlock>())
            {
                parts.Add(block switch
                {
                    TextContentBlock textBlock => textBlock.Text,
                    ImageContentBlock image => $"[image {image.MimeType}, {image.Data.Length} bytes: not shown]",
                    EmbeddedResourceBlock { Resource: TextResourceContents res } => res.Text,
                    EmbeddedResourceBlock embedded => $"[binary resource {embedded.Resource?.Uri}: not shown]",
                    ResourceLinkBlock link => $"[resource: {link.Uri}{(string.IsNullOrEmpty(link.Name) ? "" : $" ({link.Name})")}]",
                    AudioContentBlock => "[audio: not shown]",
                    _ => $"[{block.Type} content: not shown]"
                });
            }

            var text = string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)));
            if (text.Length == 0 && result.StructuredContent is JsonElement structured)
                text = structured.GetRawText();
            if (text.Length == 0)
                text = result.IsError == true ? "(the tool reported an error without details)" : "(the tool returned nothing)";

            if (text.Length > maxChars)
                text = text[..maxChars] + $"\n…(truncated: {text.Length - maxChars:N0} more characters)";

            return result.IsError == true ? $"ERROR: {text}" : text;
        }
    }
}
