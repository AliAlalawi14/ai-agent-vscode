using System.Collections;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ai_Agent.Mcp
{
    /// <summary>A live connection to one MCP server.</summary>
    public interface IMcpConnection : IAsyncDisposable
    {
        IReadOnlyList<Tool> Tools { get; }

        /// <summary>False once the server process exited or the remote session ended.</summary>
        bool IsAlive { get; }

        Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
    }

    /// <summary>Starts/connects a configured server (tests plug in an in-process server here).</summary>
    public interface IMcpConnector
    {
        Task<IMcpConnection> ConnectAsync(McpServerConfig config, CancellationToken cancellationToken);
    }

    /// <summary>An MCP connection through the official SDK client, over any transport.</summary>
    public sealed class SdkMcpConnection : IMcpConnection
    {
        private readonly McpClient _client;

        private SdkMcpConnection(McpClient client, IReadOnlyList<Tool> tools)
        {
            _client = client;
            Tools = tools;
        }

        public IReadOnlyList<Tool> Tools { get; }
        public bool IsAlive => !_client.Completion.IsCompleted;

        public static async Task<SdkMcpConnection> ConnectAsync(IClientTransport transport, ILoggerFactory? loggerFactory, CancellationToken cancellationToken)
        {
            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "Stoat", Version = typeof(SdkMcpConnection).Assembly.GetName().Version?.ToString() ?? "0" }
            };
            var client = await McpClient.CreateAsync(transport, options, loggerFactory, cancellationToken);
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                return new SdkMcpConnection(client, tools.Select(t => t.ProtocolTool).ToList());
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        public async Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
            await _client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

        public ValueTask DisposeAsync() => _client.DisposeAsync();
    }

    /// <summary>Connects real servers: a child process over stdio, or a remote Streamable HTTP / SSE endpoint.</summary>
    public sealed class SdkMcpConnector : IMcpConnector
    {
        private readonly ILoggerFactory _loggerFactory;

        public SdkMcpConnector(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

        public async Task<IMcpConnection> ConnectAsync(McpServerConfig config, CancellationToken cancellationToken)
        {
            if (config.IsRemote)
            {
                var http = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(config.Url!),
                    Name = config.Name,
                    TransportMode = HttpTransportMode.AutoDetect,
                    AdditionalHeaders = config.Headers.Count > 0 ? new Dictionary<string, string>(config.Headers) : null,
                }, _loggerFactory);
                return await SdkMcpConnection.ConnectAsync(http, _loggerFactory, cancellationToken);
            }

            // The SDK starts .cmd scripts (npx, uvx) through cmd.exe on Windows and appends the server's
            // stderr to the error when it fails to start, which explains most failures (missing package, bad key...)
            var stdio = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command!,
                Arguments = config.Args.ToList(),
                InheritEnvironmentVariables = false,
                EnvironmentVariables = McpProcess.Environment(config.Env),
                ShutdownTimeout = TimeSpan.FromSeconds(3),
            }, _loggerFactory);
            return await SdkMcpConnection.ConnectAsync(stdio, _loggerFactory, cancellationToken);
        }
    }

    /// <summary>The environment a stdio server process gets.</summary>
    public static class McpProcess
    {
        /// <summary>
        /// The server's environment: this process's environment WITHOUT the backend's own configuration
        /// (every "Section__Key" variable: provider API keys, the agent token, the MCP config itself) and
        /// ASP.NET settings, plus the variables configured for this server. A third-party server never sees
        /// the user's model keys.
        /// </summary>
        public static Dictionary<string, string?> Environment(IReadOnlyDictionary<string, string> configured)
        {
            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            {
                var name = (string)entry.Key;
                if (IsBackendSetting(name)) continue;
                env[name] = entry.Value as string;
            }
            foreach (var (name, value) in configured) env[name] = value;
            return env;
        }

        public static bool IsBackendSetting(string name) =>
            name.Contains("__", StringComparison.Ordinal) ||
            name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase);
    }
}
