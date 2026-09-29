using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ai_Agent.Mcp
{
    public enum McpServerState { Disabled, Starting, Connected, Error }

    /// <summary>
    /// Owns the connections to the configured MCP servers. Servers start in the background when the backend
    /// starts (a slow "npx" download never delays startup); requests wait briefly for servers still starting.
    /// A server that fails or exits is reported with its error and its tools are no longer offered.
    /// </summary>
    public sealed class McpConnectionManager : IHostedService, IAsyncDisposable, IDisposable
    {
        private readonly IMcpConnector _connector;
        private readonly McpOptions _options;
        private readonly ILogger<McpConnectionManager> _logger;
        private readonly ConcurrentDictionary<string, ServerEntry> _servers = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _shutdown = new();

        /// <summary>Problems in the configuration itself (invalid JSON, a server without command/url...).</summary>
        public IReadOnlyList<string> ConfigErrors { get; }

        public McpConnectionManager(IMcpConnector connector, IOptions<McpOptions> options, ILogger<McpConnectionManager> logger)
        {
            _connector = connector;
            _options = options.Value;
            _logger = logger;

            var configs = McpServerConfig.ParseAll(_options.ServersJson, out var errors);
            ConfigErrors = errors;
            foreach (var error in errors) _logger.LogWarning("[MCP] {Error}", error);
            foreach (var config in configs)
                _servers[config.Name] = new ServerEntry(config) { State = config.Enabled ? McpServerState.Starting : McpServerState.Disabled };
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var entry in _servers.Values.Where(s => s.State == McpServerState.Starting))
                entry.Startup = Task.Run(() => ConnectAsync(entry));
            return Task.CompletedTask;
        }

        private async Task ConnectAsync(ServerEntry entry)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
            try
            {
                entry.Connection = await _connector.ConnectAsync(entry.Config, timeout.Token);
                entry.State = McpServerState.Connected;
                entry.Error = null;
                _logger.LogInformation("[MCP] {Server} connected: {Count} tools", entry.Config.Name, entry.Connection.Tools.Count);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                entry.State = McpServerState.Error;
                entry.Error = "Stopped before it finished starting.";
            }
            catch (OperationCanceledException)
            {
                entry.State = McpServerState.Error;
                entry.Error = $"Didn't start within {_options.ConnectTimeoutSeconds} s. Check the command, or run it in a terminal to see what it needs.";
                _logger.LogWarning("[MCP] {Server} timed out while starting", entry.Config.Name);
            }
            catch (Exception e)
            {
                entry.State = McpServerState.Error;
                entry.Error = e.Message;
                _logger.LogWarning("[MCP] {Server} failed to start: {Error}", entry.Config.Name, e.Message);
            }
        }

        /// <summary>Waits (bounded) for servers that are still starting, so the first request gets their tools.</summary>
        public async Task WaitForStartupAsync(CancellationToken cancellationToken)
        {
            var pending = _servers.Values.Where(s => s.State == McpServerState.Starting && s.Startup != null).Select(s => s.Startup!).ToList();
            if (pending.Count == 0) return;
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(_options.StartupWaitSeconds), cancellationToken);
            }
            catch (TimeoutException) { /* still starting: this request runs without them */ }
        }

        /// <summary>Tools of connected servers (disabled tools left out). A server that died is marked as failed.</summary>
        public List<McpTool> CreateTools()
        {
            var tools = new List<McpTool>();
            foreach (var entry in _servers.Values.OrderBy(s => s.Config.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.State != McpServerState.Connected || entry.Connection == null) continue;
                if (!entry.Connection.IsAlive)
                {
                    entry.State = McpServerState.Error;
                    entry.Error = "The server stopped. Restart the backend to start it again.";
                    continue;
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tool in entry.Connection.Tools)
                {
                    if (entry.Config.DisabledTools.Contains(tool.Name)) continue;
                    var exposed = McpSchema.ExposedName(entry.Config.Name, tool.Name);
                    if (!names.Add(exposed)) continue;   // two tools sanitized to the same name: keep the first
                    tools.Add(new McpTool(entry.Config, tool, entry.Connection, _options, exposed));
                }
            }
            return tools;
        }

        public List<McpServerStatus> GetStatus() => _servers.Values
            .OrderBy(s => s.Config.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var tools = s.Connection?.Tools ?? Array.Empty<ModelContextProtocol.Protocol.Tool>();
                var offered = tools.Where(t => !s.Config.DisabledTools.Contains(t.Name)).ToList();
                return new McpServerStatus
                {
                    Name = s.Config.Name,
                    State = (s.State == McpServerState.Connected && s.Connection is { IsAlive: false } ? McpServerState.Error : s.State)
                        .ToString().ToLowerInvariant(),
                    Error = s.State == McpServerState.Connected && s.Connection is { IsAlive: false }
                        ? "The server stopped. Restart the backend to start it again." : s.Error,
                    Transport = s.Config.IsRemote ? "http" : "stdio",
                    // What the offered tools add to every request (name + description + schema), ~4 chars per token
                    PromptTokens = offered.Sum(t => (t.Name.Length + (t.Description?.Length ?? 0) + t.InputSchema.GetRawText().Length) / 4),
                    Tools = tools.Select(t => new McpToolStatus
                    {
                        Name = t.Name,
                        Description = t.Description ?? string.Empty,
                        ReadOnly = t.Annotations?.ReadOnlyHint == true,
                        Enabled = !s.Config.DisabledTools.Contains(t.Name),
                        AlwaysAllowed = s.Config.IsAllowedWithoutAsking(t.Name),
                    }).ToList()
                };
            }).ToList();

        public Task StopAsync(CancellationToken cancellationToken) => DisposeConnectionsAsync();

        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            await DisposeConnectionsAsync();
            _shutdown.Dispose();
        }

        /// <summary>For containers disposed synchronously: server processes must not outlive the backend.</summary>
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        private async Task DisposeConnectionsAsync()
        {
            try { _shutdown.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
            foreach (var entry in _servers.Values)
            {
                var connection = entry.Connection;
                entry.Connection = null;
                if (connection == null) continue;
                try { await connection.DisposeAsync(); }   // stops the server process
                catch (Exception e) { _logger.LogDebug("[MCP] {Server} dispose: {Error}", entry.Config.Name, e.Message); }
            }
        }

        private sealed class ServerEntry
        {
            public ServerEntry(McpServerConfig config) => Config = config;
            public McpServerConfig Config { get; }
            public volatile McpServerState State;
            public string? Error;
            public IMcpConnection? Connection;
            public Task? Startup;
        }
    }

    public sealed class McpServerStatus
    {
        public string Name { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string? Error { get; init; }
        public string Transport { get; init; } = string.Empty;
        public int PromptTokens { get; init; }
        public List<McpToolStatus> Tools { get; init; } = new();
    }

    public sealed class McpToolStatus
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public bool ReadOnly { get; init; }
        public bool Enabled { get; init; }
        public bool AlwaysAllowed { get; init; }
    }
}
