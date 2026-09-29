using System.ComponentModel;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai_Agent.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai_Agent.Tests
{
    /// <summary>A real MCP server running in this process, reached over in-memory pipes (the SDK on both ends).</summary>
    public sealed class InProcessMcpConnector : IMcpConnector, IAsyncDisposable
    {
        private readonly Func<McpServerTool[]> _tools;
        private readonly CancellationTokenSource _stop = new();
        public int Connects { get; private set; }

        public InProcessMcpConnector(Func<McpServerTool[]> tools) => _tools = tools;

        public async Task<IMcpConnection> ConnectAsync(McpServerConfig config, CancellationToken cancellationToken)
        {
            Connects++;
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = config.Name, Version = "1.0" },
                ToolCollection = new McpServerPrimitiveCollection<McpServerTool>()
            };
            foreach (var tool in _tools()) options.ToolCollection.Add(tool);

            var server = McpServer.Create(new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()), options);
            _ = server.RunAsync(_stop.Token);
            var transport = new ModelContextProtocol.Protocol.StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
            return await SdkMcpConnection.ConnectAsync(transport, NullLoggerFactory.Instance, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _stop.Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>The test server's tools: echo (read-only), add (typed ints), join (array + bool), fail, slow.</summary>
        public static McpServerTool[] StandardTools() => new[]
        {
            McpServerTool.Create(([Description("Text to echo")] string text) => $"echo:{text}",
                new McpServerToolCreateOptions { Name = "echo", Description = "Echoes the text", ReadOnly = true }),
            McpServerTool.Create((int a, int b) => (a + b).ToString(),
                new McpServerToolCreateOptions { Name = "add", Description = "Adds two integers" }),
            McpServerTool.Create((string[] items, bool upper) => string.Join("+", upper ? items.Select(i => i.ToUpperInvariant()) : items),
                new McpServerToolCreateOptions { Name = "join", Description = "Joins items" }),
            McpServerTool.Create(string () => throw new InvalidOperationException("boom"),
                new McpServerToolCreateOptions { Name = "fail", Description = "Always fails" }),
            McpServerTool.Create(async (CancellationToken ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return "late"; },
                new McpServerToolCreateOptions { Name = "slow", Description = "Takes 30 seconds" }),
        };
    }

    /// <summary>A connector whose servers fail, hang, or return a scripted connection.</summary>
    public sealed class ScriptedMcpConnector : IMcpConnector
    {
        public Func<McpServerConfig, CancellationToken, Task<IMcpConnection>> Connect { get; set; } =
            (_, _) => throw new InvalidOperationException("not scripted");

        public Task<IMcpConnection> ConnectAsync(McpServerConfig config, CancellationToken cancellationToken) => Connect(config, cancellationToken);
    }

    public sealed class FakeMcpConnection : IMcpConnection
    {
        public List<Tool> ToolList { get; } = new();
        public IReadOnlyList<Tool> Tools => ToolList;
        public bool IsAlive { get; set; } = true;
        public bool Disposed { get; private set; }
        public Func<string, IReadOnlyDictionary<string, object?>, CallToolResult> OnCall { get; set; } =
            (_, _) => new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = "ok" } } };

        public Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
            Task.FromResult(OnCall(toolName, arguments));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public static Tool MakeTool(string name, string schemaJson = "{\"type\":\"object\",\"properties\":{}}", bool readOnly = false) => new()
        {
            Name = name,
            Description = $"{name} tool",
            InputSchema = JsonDocument.Parse(schemaJson).RootElement.Clone(),
            Annotations = readOnly ? new ToolAnnotations { ReadOnlyHint = true } : null
        };
    }

    public class McpConfigTests
    {
        [Fact]
        public void Parses_the_mcpServers_format_with_stdio_and_remote_servers()
        {
            var servers = McpServerConfig.ParseAll("""
                { "mcpServers": {
                    "playwright": { "command": "npx", "args": ["@playwright/mcp@latest"], "env": { "DEBUG": "1" } },
                    "github": { "url": "https://api.githubcopilot.com/mcp/", "headers": { "Authorization": "Bearer x" }, "alwaysAllow": true }
                } }
                """, out var errors);

            Assert.Empty(errors);
            var pw = servers.Single(s => s.Name == "playwright");
            Assert.Equal("npx", pw.Command);
            Assert.Equal(new[] { "@playwright/mcp@latest" }, pw.Args);
            Assert.Equal("1", pw.Env["DEBUG"]);
            Assert.False(pw.IsRemote);
            var gh = servers.Single(s => s.Name == "github");
            Assert.True(gh.IsRemote);
            Assert.Null(gh.Command);
            Assert.True(gh.IsAllowedWithoutAsking("anything"));
        }

        [Fact]
        public void Accepts_the_VS_Code_servers_format_and_disabled_flags()
        {
            var servers = McpServerConfig.ParseAll("""
                { "servers": {
                    "a": { "type": "stdio", "command": "uvx", "args": ["x"], "disabled": true },
                    "b": { "command": "node", "enabled": false, "alwaysAllow": ["read"], "disabledTools": ["write"] },
                    "c": { "command": "node" }
                } }
                """, out var errors);

            Assert.Empty(errors);
            Assert.False(servers.Single(s => s.Name == "a").Enabled);
            var b = servers.Single(s => s.Name == "b");
            Assert.False(b.Enabled);
            Assert.True(b.IsAllowedWithoutAsking("read"));
            Assert.False(b.IsAllowedWithoutAsking("write"));
            Assert.Contains("write", b.DisabledTools);
            Assert.True(servers.Single(s => s.Name == "c").Enabled);
        }

        [Fact]
        public void Bad_entries_are_reported_and_skipped_without_losing_the_good_ones()
        {
            var servers = McpServerConfig.ParseAll("""
                { "mcpServers": {
                    "nothing": { },
                    "badurl": { "url": "ftp://x" },
                    "notobject": 5,
                    "!!!": { "command": "x" },
                    "good": { "command": "node" }
                } }
                """, out var errors);

            Assert.Equal("good", Assert.Single(servers).Name);
            Assert.Equal(4, errors.Count);
            Assert.Contains(errors, e => e.Contains("command") && e.Contains("url"));
            Assert.Contains(errors, e => e.Contains("http(s)"));
        }

        [Theory]
        [InlineData("my server", "my_server")]
        [InlineData("a__b", "a_b")]
        [InlineData("__x__", "x")]
        [InlineData("GitHub-MCP", "GitHub-MCP")]
        public void Server_names_are_made_safe_for_tool_names(string input, string expected) =>
            Assert.Equal(expected, McpServerConfig.SanitizeName(input));

        [Fact]
        public void Invalid_json_is_an_error_not_an_exception()
        {
            Assert.Empty(McpServerConfig.ParseAll("{ not json", out var errors));
            Assert.Single(errors);
            Assert.Empty(McpServerConfig.ParseAll(null, out var none));
            Assert.Empty(none);
        }
    }

    public class McpSchemaTests
    {
        [Fact]
        public void Exposed_names_follow_the_mcp__server__tool_convention_and_stay_within_64_chars()
        {
            Assert.Equal("mcp__github__create_issue", McpSchema.ExposedName("github", "create_issue"));
            Assert.Equal("mcp__s__a_b", McpSchema.ExposedName("s", "a.b"));

            var longA = McpSchema.ExposedName("server", new string('a', 80) + "1");
            var longB = McpSchema.ExposedName("server", new string('a', 80) + "2");
            Assert.True(longA.Length <= 64);
            Assert.NotEqual(longA, longB);
            Assert.Matches("^[A-Za-z0-9_-]+$", longA);
        }

        [Fact]
        public void Schemas_are_normalized_so_every_provider_accepts_them()
        {
            var (props, required) = McpSchema.NormalizeInputSchema(JsonDocument.Parse("""
                {
                  "$schema": "http://json-schema.org/draft-07/schema#",
                  "type": "object",
                  "additionalProperties": false,
                  "$defs": { "Point": { "type": "object", "properties": { "x": { "type": "number" } }, "additionalProperties": false } },
                  "properties": {
                    "name": { "type": ["string", "null"], "format": "uri", "default": "a", "description": "A name" },
                    "where": { "$ref": "#/$defs/Point", "description": "Where" },
                    "tags": { "type": "array" },
                    "missing": { "$ref": "#/$defs/Nope" }
                  },
                  "required": ["name"]
                }
                """).RootElement);

            Assert.Equal(new[] { "name" }, required);
            var name = props["name"]!.AsObject();
            Assert.Equal("string", name["type"]!.GetValue<string>());
            Assert.True(name["nullable"]!.GetValue<bool>());
            Assert.Null(name["format"]);
            Assert.Null(name["default"]);
            var where = props["where"]!.AsObject();
            Assert.Equal("object", where["type"]!.GetValue<string>());
            Assert.Equal("Where", where["description"]!.GetValue<string>());
            Assert.Null(where["additionalProperties"]);
            Assert.NotNull(where["properties"]!["x"]);
            Assert.NotNull(props["tags"]!["items"]);
            Assert.Equal("object", props["missing"]!["type"]!.GetValue<string>());
        }

        [Fact]
        public void Self_referencing_schemas_stop_at_a_safe_depth()
        {
            var (props, _) = McpSchema.NormalizeInputSchema(JsonDocument.Parse("""
                { "type": "object", "$defs": { "Node": { "type": "object", "properties": { "child": { "$ref": "#/$defs/Node" } } } },
                  "properties": { "tree": { "$ref": "#/$defs/Node" } } }
                """).RootElement);
            Assert.NotNull(props["tree"]);   // no stack overflow
        }

        [Fact]
        public void Arguments_are_rebuilt_with_their_json_types()
        {
            var schemas = new Dictionary<string, JsonNode?>
            {
                ["text"] = JsonNode.Parse("""{ "type": "string" }"""),
                ["count"] = JsonNode.Parse("""{ "type": "integer" }"""),
                ["items"] = JsonNode.Parse("""{ "type": "array", "items": { "type": "string" } }"""),
                ["flag"] = JsonNode.Parse("""{ "type": "boolean" }"""),
            };
            var args = McpSchema.ToArguments(new Dictionary<string, string>
            {
                ["text"] = "123",          // a string parameter stays a string even if it looks like a number
                ["count"] = "5",
                ["items"] = "[\"a\",\"b\"]",
                ["flag"] = "true",
                ["unknown"] = "not json"
            }, schemas);

            Assert.Equal("123", args["text"]);
            Assert.Equal(JsonValueKind.Number, ((JsonElement)args["count"]!).ValueKind);
            Assert.Equal(2, ((JsonElement)args["items"]!).GetArrayLength());
            Assert.Equal(JsonValueKind.True, ((JsonElement)args["flag"]!).ValueKind);
            Assert.Equal("not json", args["unknown"]);
        }

        [Fact]
        public void Results_become_text_with_binary_summarized_errors_marked_and_long_output_truncated()
        {
            var mixed = new CallToolResult
            {
                Content = new List<ContentBlock>
                {
                    new TextContentBlock { Text = "hello" },
                    new ImageContentBlock { MimeType = "image/png", Data = "aGk="u8.ToArray() },
                }
            };
            var text = McpSchema.ResultToText(mixed, 1000);
            Assert.StartsWith("hello", text);
            Assert.Contains("[image image/png", text);

            var error = new CallToolResult { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = "bad input" } } };
            Assert.Equal("ERROR: bad input", McpSchema.ResultToText(error, 1000));

            var big = new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = new string('x', 5000) } } };
            var truncated = McpSchema.ResultToText(big, 100);
            Assert.StartsWith(new string('x', 100), truncated);
            Assert.Contains("truncated", truncated);

            var structured = new CallToolResult { StructuredContent = JsonDocument.Parse("{\"a\":1}").RootElement.Clone(), Content = new List<ContentBlock>() };
            Assert.Equal("{\"a\":1}", McpSchema.ResultToText(structured, 100));
        }
    }

    public class McpProcessTests
    {
        [Fact]
        public void Servers_never_inherit_the_backends_keys_or_settings()
        {
            System.Environment.SetEnvironmentVariable("Anthropic__ApiKey", "sk-secret-test");
            System.Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:1");
            try
            {
                var env = McpProcess.Environment(new Dictionary<string, string> { ["GITHUB_TOKEN"] = "gh" });
                Assert.False(env.ContainsKey("Anthropic__ApiKey"));
                Assert.False(env.ContainsKey("ASPNETCORE_URLS"));
                Assert.Equal("gh", env["GITHUB_TOKEN"]);
                Assert.True(env.ContainsKey("PATH") || env.ContainsKey("Path"));   // normal environment kept
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("Anthropic__ApiKey", null);
                System.Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
            }
        }
    }

    /// <summary>The adapter against a real MCP server (SDK client ↔ SDK server over pipes).</summary>
    public class McpIntegrationTests : IAsyncLifetime
    {
        private readonly InProcessMcpConnector _connector = new(InProcessMcpConnector.StandardTools);
        private IMcpConnection _connection = null!;
        private readonly McpServerConfig _config = new() { Name = "test", Command = "x" };
        private readonly McpOptions _options = new() { CallTimeoutSeconds = 10 };

        public async Task InitializeAsync() => _connection = await _connector.ConnectAsync(_config, CancellationToken.None);

        public async Task DisposeAsync()
        {
            await _connection.DisposeAsync();
            await _connector.DisposeAsync();
        }

        private McpTool Tool(string name) =>
            new(_config, _connection.Tools.Single(t => t.Name == name), _connection, _options, McpSchema.ExposedName("test", name));

        [Fact]
        public void Lists_the_servers_tools_with_their_schemas_and_hints()
        {
            Assert.Equal(new[] { "add", "echo", "fail", "join", "slow" }, _connection.Tools.Select(t => t.Name).OrderBy(n => n));
            var echo = Tool("echo");
            Assert.True(echo.IsReadOnly);
            Assert.Equal("mcp__test__echo", echo.Name);
            Assert.Contains("Text to echo", echo.Parameters["text"]);
            Assert.False(Tool("add").IsReadOnly);
            Assert.True(Tool("add").RequiresApproval);
        }

        [Fact]
        public async Task Calls_tools_with_typed_arguments_rebuilt_from_strings()
        {
            Assert.Equal("echo:hi", await Tool("echo").ExecuteAsync(new() { ["text"] = "hi" }, CancellationToken.None));
            Assert.Equal("5", await Tool("add").ExecuteAsync(new() { ["a"] = "2", ["b"] = "3" }, CancellationToken.None));
            Assert.Equal("A+B", await Tool("join").ExecuteAsync(new() { ["items"] = "[\"a\",\"b\"]", ["upper"] = "true" }, CancellationToken.None));
        }

        [Fact]
        public async Task A_failing_tool_returns_an_error_to_the_model_instead_of_throwing()
        {
            var result = await Tool("fail").ExecuteAsync(new(), CancellationToken.None);
            Assert.StartsWith("ERROR", result);
        }

        [Fact]
        public async Task Stop_cancels_a_running_tool_quickly()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await Tool("slow").ExecuteAsync(new(), cts.Token);
            Assert.Equal("ERROR: Cancelled by the user.", result);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task A_tool_that_takes_too_long_times_out()
        {
            var tool = new McpTool(_config, _connection.Tools.Single(t => t.Name == "slow"), _connection,
                new McpOptions { CallTimeoutSeconds = 1 }, "mcp__test__slow");
            var result = await tool.ExecuteAsync(new(), CancellationToken.None);
            Assert.Contains("didn't answer within 1 s", result);
        }

        [Fact]
        public async Task The_approval_card_shows_server_tool_and_arguments()
        {
            var preview = await Tool("add").PreviewAsync(new() { ["a"] = "2", ["b"] = "3" });
            Assert.Null(preview.Error);
            Assert.Null(preview.FilePath);
            Assert.StartsWith("test › add ", preview.Command);
            Assert.Contains("\"a\":2", preview.Command);
            Assert.Equal("Use add (test)", preview.Summary);
        }

        [Fact]
        public void Tool_definitions_serialize_to_valid_function_schemas()
        {
            var registry = new Ai_Agent.Tools.Services.ToolRegistry();
            registry.RegisterTool(Tool("join"));
            var json = JsonSerializer.Serialize(registry.GetToolDefinitions());
            var fn = JsonNode.Parse(json)![0]!["function"]!;
            Assert.Equal("mcp__test__join", fn["name"]!.GetValue<string>());
            Assert.Equal("array", fn["parameters"]!["properties"]!["items"]!["type"]!.GetValue<string>());
            Assert.Equal("boolean", fn["parameters"]!["properties"]!["upper"]!["type"]!.GetValue<string>());
        }
    }

    public class McpConnectionManagerTests
    {
        private static McpConnectionManager Manager(IMcpConnector connector, string json, Action<McpOptions>? configure = null)
        {
            var options = new McpOptions { ServersJson = json, ConnectTimeoutSeconds = 5, StartupWaitSeconds = 5 };
            configure?.Invoke(options);
            return new McpConnectionManager(connector, Options.Create(options), NullLogger<McpConnectionManager>.Instance);
        }

        [Fact]
        public async Task A_server_that_fails_to_start_is_reported_and_the_others_still_work()
        {
            var good = new FakeMcpConnection();
            good.ToolList.Add(FakeMcpConnection.MakeTool("ping"));
            var connector = new ScriptedMcpConnector
            {
                Connect = (config, _) => config.Name == "broken"
                    ? throw new InvalidOperationException("npm ERR! 404 package not found")
                    : Task.FromResult<IMcpConnection>(good)
            };
            await using var manager = Manager(connector, """{ "mcpServers": { "broken": { "command": "x" }, "ok": { "command": "y" } } }""");
            await manager.StartAsync(CancellationToken.None);
            await manager.WaitForStartupAsync(CancellationToken.None);

            var status = manager.GetStatus();
            var broken = status.Single(s => s.Name == "broken");
            Assert.Equal("error", broken.State);
            Assert.Contains("404", broken.Error);
            Assert.Equal("connected", status.Single(s => s.Name == "ok").State);
            Assert.Equal("mcp__ok__ping", Assert.Single(manager.CreateTools()).Name);
        }

        [Fact]
        public async Task A_server_that_hangs_times_out_with_a_helpful_message()
        {
            var connector = new ScriptedMcpConnector
            {
                Connect = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; }
            };
            await using var manager = Manager(connector, """{ "mcpServers": { "slow": { "command": "x" } } }""", o => o.ConnectTimeoutSeconds = 1);
            await manager.StartAsync(CancellationToken.None);
            await manager.WaitForStartupAsync(CancellationToken.None);

            var slow = Assert.Single(manager.GetStatus());
            Assert.Equal("error", slow.State);
            Assert.Contains("within 1 s", slow.Error);
            Assert.Empty(manager.CreateTools());
        }

        [Fact]
        public async Task Disabled_servers_are_not_started_and_disabled_tools_are_not_offered()
        {
            var connection = new FakeMcpConnection();
            connection.ToolList.Add(FakeMcpConnection.MakeTool("keep"));
            connection.ToolList.Add(FakeMcpConnection.MakeTool("hide"));
            var started = new List<string>();
            var connector = new ScriptedMcpConnector
            {
                Connect = (config, _) => { started.Add(config.Name); return Task.FromResult<IMcpConnection>(connection); }
            };
            await using var manager = Manager(connector, """
                { "mcpServers": { "off": { "command": "x", "disabled": true }, "on": { "command": "y", "disabledTools": ["hide"] } } }
                """);
            await manager.StartAsync(CancellationToken.None);
            await manager.WaitForStartupAsync(CancellationToken.None);

            Assert.Equal(new[] { "on" }, started);
            Assert.Equal("disabled", manager.GetStatus().Single(s => s.Name == "off").State);
            Assert.Equal(new[] { "mcp__on__keep" }, manager.CreateTools().Select(t => t.Name));
            var hide = manager.GetStatus().Single(s => s.Name == "on").Tools.Single(t => t.Name == "hide");
            Assert.False(hide.Enabled);
        }

        [Fact]
        public async Task A_server_that_exits_later_is_marked_failed_and_its_tools_disappear()
        {
            var connection = new FakeMcpConnection();
            connection.ToolList.Add(FakeMcpConnection.MakeTool("t"));
            var connector = new ScriptedMcpConnector { Connect = (_, _) => Task.FromResult<IMcpConnection>(connection) };
            await using var manager = Manager(connector, """{ "mcpServers": { "s": { "command": "x" } } }""");
            await manager.StartAsync(CancellationToken.None);
            await manager.WaitForStartupAsync(CancellationToken.None);
            Assert.Single(manager.CreateTools());

            connection.IsAlive = false;
            Assert.Empty(manager.CreateTools());
            var s = Assert.Single(manager.GetStatus());
            Assert.Equal("error", s.State);
            Assert.Contains("stopped", s.Error);
        }

        [Fact]
        public async Task Stopping_the_backend_closes_every_connection()
        {
            var connection = new FakeMcpConnection();
            var connector = new ScriptedMcpConnector { Connect = (_, _) => Task.FromResult<IMcpConnection>(connection) };
            var manager = Manager(connector, """{ "mcpServers": { "s": { "command": "x" } } }""");
            await manager.StartAsync(CancellationToken.None);
            await manager.WaitForStartupAsync(CancellationToken.None);

            await manager.StopAsync(CancellationToken.None);
            Assert.True(connection.Disposed);
            await manager.DisposeAsync();
        }

        [Fact]
        public async Task Config_errors_are_exposed_in_the_status()
        {
            await using var manager = Manager(new ScriptedMcpConnector(), """{ "mcpServers": { "x": { } } }""");
            Assert.Single(manager.ConfigErrors);
            Assert.Empty(manager.GetStatus());
        }
    }

    /// <summary>MCP tools inside the agent loop: offered per mode, and always behind the approval gate.</summary>
    public class McpAgentLoopTests
    {
        private static async Task<(AgentHarness Harness, InProcessMcpConnector Connector)> HarnessAsync(string serverJson)
        {
            var connector = new InProcessMcpConnector(InProcessMcpConnector.StandardTools);
            var harness = new AgentHarness(configureServices: services =>
            {
                services.Configure<McpOptions>(o => { o.ServersJson = serverJson; o.CallTimeoutSeconds = 10; });
                services.AddSingleton<IMcpConnector>(connector);
                services.AddSingleton<McpConnectionManager>();
            });
            await harness.Services.GetRequiredService<McpConnectionManager>().StartAsync(CancellationToken.None);
            return (harness, connector);
        }

        private const string Server = """{ "mcpServers": { "test": { "command": "x" } } }""";

        [Fact]
        public async Task An_mcp_tool_asks_for_approval_in_Agent_mode_and_its_result_reaches_the_model()
        {
            var (h, connector) = await HarnessAsync(Server);
            using var _ = h; await using var __ = connector;
            h.Llm.Call("mcp__test__add", new { a = 2, b = 3 }).Text("done");

            var events = await h.RunAsync("add 2 and 3", "agent", approve: true);

            var prompt = Assert.Single(AgentHarness.Prompts(events));
            Assert.Contains("\"kind\":\"command\"", prompt);
            Assert.Contains("test \\u203A add", prompt);   // "test › add" (JSON-escaped)
            Assert.Equal("5", h.Llm.Requests[1].Last(m => m.Role == "tool").Content);
        }

        [Fact]
        public async Task Auto_mode_still_asks_before_an_mcp_tool_runs()
        {
            var (h, connector) = await HarnessAsync(Server);
            using var _ = h; await using var __ = connector;
            h.Llm.Call("mcp__test__add", new { a = 1, b = 1 }).Text("not run");

            var events = await h.RunAsync("add", "auto", approve: false, reviewEdits: true);

            Assert.Single(AgentHarness.Prompts(events));
            Assert.Contains("REJECTED", h.Llm.Requests[1].Last(m => m.Role == "tool").Content);
        }

        [Fact]
        public async Task An_always_allowed_server_runs_without_asking()
        {
            var (h, connector) = await HarnessAsync("""{ "mcpServers": { "test": { "command": "x", "alwaysAllow": true } } }""");
            using var _ = h; await using var __ = connector;
            h.Llm.Call("mcp__test__echo", new { text = "hi" }).Text("done");

            var events = await h.RunAsync("echo hi", "agent", approve: false);

            Assert.Empty(AgentHarness.Prompts(events));
            Assert.Equal("echo:hi", h.Llm.Requests[1].Last(m => m.Role == "tool").Content);
        }

        [Fact]
        public async Task Ask_mode_offers_only_the_read_only_mcp_tools()
        {
            var (h, connector) = await HarnessAsync(Server);
            using var _ = h; await using var __ = connector;
            h.Llm.Text("answer");

            await h.RunAsync("what can you do", "ask");

            var offered = h.Llm.OfferedTools[0];
            Assert.Contains("mcp__test__echo", offered);
            Assert.DoesNotContain("mcp__test__add", offered);
            Assert.DoesNotContain("mcp__test__slow", offered);
        }

        [Fact]
        public async Task Agent_mode_offers_every_mcp_tool_next_to_the_built_in_ones()
        {
            var (h, connector) = await HarnessAsync(Server);
            using var _ = h; await using var __ = connector;
            h.Llm.Text("answer");

            await h.RunAsync("hi", "agent");

            var offered = h.Llm.OfferedTools[0];
            Assert.Contains("read_file", offered);
            Assert.Contains("mcp__test__add", offered);
            Assert.Contains("mcp__test__echo", offered);
        }
    }
}
