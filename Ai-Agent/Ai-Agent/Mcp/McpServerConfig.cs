using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ai_Agent.Mcp
{
    /// <summary>
    /// One MCP server the agent may use: a local program (stdio: command + args + env) or a remote
    /// endpoint (Streamable HTTP / SSE: url + headers). Parsed from the familiar "mcpServers" JSON
    /// (Cursor, Claude Desktop), VS Code's "servers" format, or a bare name → server object.
    /// </summary>
    public sealed class McpServerConfig
    {
        public string Name { get; init; } = string.Empty;
        public string? Command { get; init; }
        public List<string> Args { get; init; } = new();
        public Dictionary<string, string> Env { get; init; } = new();
        public string? Url { get; init; }
        public Dictionary<string, string> Headers { get; init; } = new();
        public bool Enabled { get; init; } = true;

        /// <summary>Every tool of this server runs without asking.</summary>
        public bool AlwaysAllow { get; init; }

        /// <summary>These tools run without asking (Cline's "alwaysAllow": [..] form).</summary>
        public HashSet<string> AlwaysAllowTools { get; init; } = new(StringComparer.Ordinal);

        /// <summary>Tools not offered to the model (keeps the prompt small).</summary>
        public HashSet<string> DisabledTools { get; init; } = new(StringComparer.Ordinal);

        public bool IsRemote => !string.IsNullOrWhiteSpace(Url);

        public bool IsAllowedWithoutAsking(string toolName) => AlwaysAllow || AlwaysAllowTools.Contains(toolName);

        /// <summary>
        /// Parses a config document. Invalid entries are skipped and reported in <paramref name="errors"/>
        /// (one bad server must not take the others down).
        /// </summary>
        public static List<McpServerConfig> ParseAll(string? json, out List<string> errors)
        {
            errors = new List<string>();
            var result = new List<McpServerConfig>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (JsonException e)
            {
                errors.Add($"MCP config is not valid JSON: {e.Message}");
                return result;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("MCP config must be a JSON object.");
                    return result;
                }

                var servers = Get(root, "mcpServers") ?? Get(root, "servers") ?? root;
                if (servers.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("\"mcpServers\" must be an object of name → server.");
                    return result;
                }

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in servers.EnumerateObject())
                {
                    var name = SanitizeName(entry.Name);
                    if (name.Length == 0) { errors.Add($"Server '{entry.Name}': the name needs letters or digits."); continue; }
                    if (!names.Add(name)) { errors.Add($"Server '{entry.Name}': duplicate name '{name}'."); continue; }
                    if (entry.Value.ValueKind != JsonValueKind.Object) { errors.Add($"Server '{entry.Name}': must be an object."); continue; }

                    var config = ParseServer(name, entry.Value, out var error);
                    if (config == null) errors.Add($"Server '{entry.Name}': {error}");
                    else result.Add(config);
                }
            }
            return result;
        }

        private static McpServerConfig? ParseServer(string name, JsonElement e, out string? error)
        {
            error = null;
            var command = GetString(e, "command")?.Trim();
            var url = GetString(e, "url")?.Trim();

            if (string.IsNullOrEmpty(command) && string.IsNullOrEmpty(url))
            {
                error = "needs a \"command\" (local program) or a \"url\" (remote server).";
                return null;
            }
            if (!string.IsNullOrEmpty(url) &&
                !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
            {
                error = $"\"url\" must be an http(s) address, got '{url}'.";
                return null;
            }

            var alwaysAllow = Get(e, "alwaysAllow");
            var disabled = GetBool(e, "disabled") == true;   // Cursor / Claude Desktop form
            return new McpServerConfig
            {
                Name = name,
                Command = string.IsNullOrEmpty(url) ? command : null,
                Args = GetStringList(e, "args"),
                Env = GetStringMap(e, "env"),
                Url = string.IsNullOrEmpty(url) ? null : url,
                Headers = GetStringMap(e, "headers"),
                Enabled = !disabled && GetBool(e, "enabled") != false,
                AlwaysAllow = alwaysAllow?.ValueKind == JsonValueKind.True,
                AlwaysAllowTools = alwaysAllow?.ValueKind == JsonValueKind.Array
                    ? new HashSet<string>(GetStringList(e, "alwaysAllow"), StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal),
                DisabledTools = new HashSet<string>(GetStringList(e, "disabledTools"), StringComparer.Ordinal),
            };
        }

        /// <summary>Letters, digits, '-' and single '_' (tool names are "mcp__server__tool", so "__" is reserved).</summary>
        public static string SanitizeName(string name)
        {
            var cleaned = Regex.Replace(name.Trim(), "[^A-Za-z0-9_-]", "_");
            cleaned = Regex.Replace(cleaned, "_{2,}", "_").Trim('_', '-');
            return cleaned.Length > 32 ? cleaned[..32].TrimEnd('_', '-') : cleaned;
        }

        private static JsonElement? Get(JsonElement e, string name)
        {
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }

        private static string? GetString(JsonElement e, string name) =>
            Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

        private static bool? GetBool(JsonElement e, string name) => Get(e, name) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => null
        };

        private static List<string> GetStringList(JsonElement e, string name) =>
            Get(e, name) is { ValueKind: JsonValueKind.Array } arr
                ? arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).ToList()
                : new List<string>();

        private static Dictionary<string, string> GetStringMap(JsonElement e, string name)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Get(e, name) is { ValueKind: JsonValueKind.Object } obj)
                foreach (var p in obj.EnumerateObject())
                    map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            return map;
        }
    }

    public class McpOptions
    {
        /// <summary>The servers, as "mcpServers" JSON (the extension passes it as Mcp__ServersJson).</summary>
        public string? ServersJson { get; set; }

        /// <summary>Starting a server (npx may download it first) and listing its tools.</summary>
        public int ConnectTimeoutSeconds { get; set; } = 60;

        /// <summary>One tool call; a browser navigation or a slow API stays well inside it.</summary>
        public int CallTimeoutSeconds { get; set; } = 120;

        /// <summary>A request waits at most this long for servers that are still starting.</summary>
        public int StartupWaitSeconds { get; set; } = 15;

        public int MaxResultChars { get; set; } = 20_000;
        public int MaxDescriptionChars { get; set; } = 1_000;
    }
}
