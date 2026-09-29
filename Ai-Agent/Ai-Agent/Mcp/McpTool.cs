using System.Text.Json;
using System.Text.Json.Nodes;
using Ai_Agent.Tools.Interfaces;
using ModelContextProtocol.Protocol;

namespace Ai_Agent.Mcp
{
    /// <summary>
    /// One tool of an MCP server, offered to the model as "mcp__server__tool". It goes through the same
    /// approval gate as commands (unless the server or tool is "always allow"), and its output is redacted
    /// and truncated like every other tool's.
    /// </summary>
    public sealed class McpTool : ITool
    {
        private readonly McpServerConfig _server;
        private readonly Tool _tool;
        private readonly IMcpConnection _connection;
        private readonly McpOptions _options;
        private readonly Dictionary<string, JsonNode?> _schemas;

        public McpTool(McpServerConfig server, Tool tool, IMcpConnection connection, McpOptions options, string exposedName)
        {
            _server = server;
            _tool = tool;
            _connection = connection;
            _options = options;
            Name = exposedName;

            var description = string.IsNullOrWhiteSpace(tool.Description) ? tool.Title ?? tool.Name : tool.Description.Trim();
            if (description.Length > options.MaxDescriptionChars) description = description[..options.MaxDescriptionChars] + "…";
            Description = $"[MCP server '{server.Name}'] {description}";

            var (properties, required) = McpSchema.NormalizeInputSchema(tool.InputSchema);
            _schemas = properties;
            Parameters = properties.ToDictionary(
                p => p.Key,
                p => (required.Contains(p.Key) ? "" : "[Optional] ") + (DescriptionOf(p.Value) ?? p.Key));
            ParameterSchemas = properties.ToDictionary(
                p => p.Key,
                p => (object)SchemaWithoutDescription(p.Value));
        }

        public string Name { get; }
        public string Description { get; }
        public Dictionary<string, string> Parameters { get; }
        public IReadOnlyDictionary<string, object> ParameterSchemas { get; }

        public string ServerName => _server.Name;
        public string ToolName => _tool.Name;

        /// <summary>The server marks it read-only: also offered in Ask and Plan mode.</summary>
        public bool IsReadOnly => _tool.Annotations?.ReadOnlyHint == true;

        public bool RequiresApproval => !_server.IsAllowedWithoutAsking(_tool.Name);

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters) =>
            ExecuteAsync(parameters, CancellationToken.None);

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            if (!_connection.IsAlive)
                return $"ERROR: MCP server '{_server.Name}' is not running (it stopped). Tell the user; they can restart the backend.";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.CallTimeoutSeconds));
            try
            {
                var result = await _connection.CallToolAsync(_tool.Name, McpSchema.ToArguments(parameters, _schemas), timeout.Token);
                return McpSchema.ResultToText(result, _options.MaxResultChars);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return "ERROR: Cancelled by the user.";
            }
            catch (OperationCanceledException)
            {
                return $"ERROR: MCP tool '{_tool.Name}' didn't answer within {_options.CallTimeoutSeconds} s.";
            }
            catch (Exception e)
            {
                return $"ERROR: MCP tool '{_tool.Name}' failed: {e.Message}";
            }
        }

        /// <summary>The approval card: which server, which tool, with what arguments.</summary>
        public Task<ToolPreview> PreviewAsync(Dictionary<string, string> parameters)
        {
            if (!_connection.IsAlive)
                return Task.FromResult(ToolPreview.Fail($"MCP server '{_server.Name}' is not running."));

            var args = JsonSerializer.Serialize(McpSchema.ToArguments(parameters, _schemas));
            if (args.Length > 600) args = args[..600] + "…";
            return Task.FromResult(new ToolPreview
            {
                Command = $"{_server.Name} › {_tool.Name} {args}",
                Summary = $"Use {_tool.Title ?? _tool.Name} ({_server.Name})"
            });
        }

        private static string? DescriptionOf(JsonNode? schema) =>
            schema is JsonObject o && o["description"] is JsonValue v && v.TryGetValue<string>(out var d) && !string.IsNullOrWhiteSpace(d)
                ? d.Trim()
                : null;

        /// <summary>The property schema as a dictionary (the registry adds the description itself).</summary>
        private static Dictionary<string, object> SchemaWithoutDescription(JsonNode? schema)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (schema is JsonObject o)
                foreach (var (key, value) in o)
                    if (key != "description" && value != null)
                        result[key] = JsonSerializer.SerializeToElement(value);
            if (result.Count == 0) result["type"] = "string";
            return result;
        }
    }
}
