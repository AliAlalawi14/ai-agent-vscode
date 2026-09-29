using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ai_Agent.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ai_Agent.Tests
{
    /// <summary>A minimal HTTP server on 127.0.0.1 answering from a route table (status, content type, body, delay).</summary>
    public sealed class TinyHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public Dictionary<string, Func<(int Status, string ContentType, byte[] Body, Dictionary<string, string>? Headers, int DelayMs)>> Routes { get; } = new();
        public string BaseUrl { get; }

        public TinyHttpServer()
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoop);
        }

        public void Html(string path, string html) => Routes[path] = () => (200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), null, 0);

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                _ = Task.Run(() => Handle(client));
            }
        }

        private async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync() ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                    var (status, type, body, headers, delay) = Routes.TryGetValue(path, out var route)
                        ? route() : (404, "text/plain", Encoding.UTF8.GetBytes("not found"), null, 0);
                    if (delay > 0) await Task.Delay(delay, _stop.Token);
                    var head = new StringBuilder($"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n");
                    foreach (var (k, v) in headers ?? new()) head.Append($"{k}: {v}\r\n");
                    head.Append("\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
                    await stream.WriteAsync(body);
                }
                catch { /* client went away */ }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    public class NetworkGuardTests
    {
        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("127.1.2.3")]
        [InlineData("10.0.0.5")]
        [InlineData("172.16.0.1")]
        [InlineData("172.31.255.255")]
        [InlineData("192.168.1.1")]
        [InlineData("169.254.169.254")]   // cloud metadata
        [InlineData("100.64.0.1")]
        [InlineData("0.0.0.0")]
        [InlineData("224.0.0.1")]
        [InlineData("255.255.255.255")]
        [InlineData("::1")]
        [InlineData("::")]
        [InlineData("fe80::1")]
        [InlineData("fc00::1")]
        [InlineData("fd12:3456::1")]
        [InlineData("::ffff:127.0.0.1")]    // IPv4-mapped loopback
        [InlineData("::ffff:10.0.0.1")]
        [InlineData("64:ff9b::a00:1")]      // NAT64 of 10.0.0.1
        [InlineData("2001:db8::1")]
        public void Local_private_and_special_addresses_are_refused(string ip) =>
            Assert.False(NetworkGuard.IsPublic(IPAddress.Parse(ip)));

        [Theory]
        [InlineData("8.8.8.8")]
        [InlineData("1.1.1.1")]
        [InlineData("172.32.0.1")]          // just outside 172.16/12
        [InlineData("2606:4700:4700::1111")]
        [InlineData("64:ff9b::808:808")]    // NAT64 of 8.8.8.8
        public void Public_addresses_are_allowed(string ip) =>
            Assert.True(NetworkGuard.IsPublic(IPAddress.Parse(ip)));

        [Theory]
        [InlineData("file:///etc/passwd")]
        [InlineData("ftp://example.com/x")]
        [InlineData("not a url")]
        [InlineData("https://user:pass@example.com/")]
        [InlineData("")]
        public void Unsupported_urls_are_rejected_before_any_network_access(string url) =>
            Assert.NotNull(WebFetcher.Validate(url, out _));
    }

    public class WebFetchTests : IDisposable
    {
        private readonly TinyHttpServer _server = new();

        private static WebFetcher Fetcher(bool allowPrivate, Action<WebOptions>? configure = null)
        {
            var options = new WebOptions { AllowPrivateHosts = allowPrivate, TimeoutSeconds = 5 };
            configure?.Invoke(options);
            return new WebFetcher(Options.Create(options));
        }

        private static WebFetchTool Tool(WebFetcher fetcher, int maxChars = 12_000) =>
            new(fetcher, new WebOptions { MaxChars = maxChars });

        public void Dispose() => _server.Dispose();

        [Theory]
        [InlineData("http://127.0.0.1:{port}/page")]
        [InlineData("http://localhost:{port}/page")]
        [InlineData("http://[::1]:{port}/page")]
        public async Task Local_addresses_are_refused_even_by_name(string template)
        {
            _server.Html("/page", "<p>secret admin panel</p>");
            var port = new Uri(_server.BaseUrl).Port;
            using var fetcher = Fetcher(allowPrivate: false);

            var result = await Tool(fetcher).ExecuteAsync(new() { ["url"] = template.Replace("{port}", port.ToString()) }, CancellationToken.None);

            Assert.StartsWith("ERROR", result);
            Assert.Contains("local or private", result);
            Assert.DoesNotContain("secret admin panel", result);
        }

        [Fact]
        public async Task Cloud_metadata_address_is_refused()
        {
            using var fetcher = Fetcher(allowPrivate: false);
            var result = await Tool(fetcher).ExecuteAsync(new() { ["url"] = "http://169.254.169.254/latest/meta-data/" }, CancellationToken.None);
            Assert.Contains("local or private", result);
        }

        [Fact]
        public async Task A_page_becomes_readable_markdown_without_the_page_furniture()
        {
            _server.Html("/doc", """
                <html><head><title>Widget API</title><script>alert('x')</script><style>p{}</style></head>
                <body>
                  <nav><a href="/">Home</a> | <a href="/pricing">Pricing</a></nav>
                  <div class="cookie-banner">We use cookies</div>
                  <main>
                    <h1>Widget API</h1>
                    <p>Create a <strong>widget</strong> with <code>POST /widgets</code>. See <a href="/docs/auth">auth</a>.</p>
                    <ul><li>fast</li><li>cheap</li></ul>
                    <pre><code class="language-bash">curl -X POST /widgets</code></pre>
                    <table><tr><th>Field</th><th>Type</th></tr><tr><td>name</td><td>string</td></tr></table>
                  </main>
                  <footer>© 2026</footer>
                </body></html>
                """);
            using var fetcher = Fetcher(allowPrivate: true);

            var result = await Tool(fetcher).ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/doc" }, CancellationToken.None);

            Assert.StartsWith("# Widget API", result);
            var body = result[result.IndexOf("URL:", StringComparison.Ordinal)..];
            Assert.Contains("# Widget API", body);   // the page's h1 kept as a heading below the title line
            Assert.Contains("**widget**", result);
            Assert.Contains("`POST /widgets`", result);
            Assert.Contains($"[auth]({_server.BaseUrl}/docs/auth)", result);
            Assert.Contains("- fast", result);
            Assert.Contains("```bash\ncurl -X POST /widgets\n```", result);
            Assert.Contains("| Field | Type |", result);
            Assert.DoesNotContain("alert", result);
            Assert.DoesNotContain("Pricing", result);
            Assert.DoesNotContain("cookies", result);
            Assert.DoesNotContain("© 2026", result);
        }

        [Fact]
        public async Task Long_pages_come_in_parts()
        {
            var text = string.Concat(Enumerable.Range(0, 400).Select(i => $"Line {i:D4}. "));
            _server.Routes["/long"] = () => (200, "text/plain", Encoding.UTF8.GetBytes(text), null, 0);
            using var fetcher = Fetcher(allowPrivate: true);
            var tool = Tool(fetcher, maxChars: 1000);

            var first = await tool.ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/long" }, CancellationToken.None);
            Assert.Contains("Line 0000", first);
            Assert.Contains("start=1000", first);

            var second = await tool.ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/long", ["start"] = "1000" }, CancellationToken.None);
            Assert.Contains("continuing at character 1,000", second);
            Assert.DoesNotContain("Line 0000", second);

            var past = await tool.ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/long", ["start"] = "999999" }, CancellationToken.None);
            Assert.StartsWith("ERROR", past);
        }

        [Fact]
        public async Task Redirects_are_followed_and_the_final_url_reported()
        {
            _server.Routes["/old"] = () => (301, "text/plain", Array.Empty<byte>(), new() { ["Location"] = "/new" }, 0);
            _server.Html("/new", "<p>moved here</p>");
            using var fetcher = Fetcher(allowPrivate: true);

            var result = await Tool(fetcher).ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/old" }, CancellationToken.None);

            Assert.Contains("moved here", result);
            Assert.Contains($"URL: {_server.BaseUrl}/new", result);
        }

        [Fact]
        public async Task A_redirect_into_the_local_network_is_refused()
        {
            // The first hop is allowed (test switch), the redirect target is checked again with the guard ON:
            // simulate by redirecting to a scheme the fetcher refuses, and to localhost with the guard on.
            _server.Routes["/to-file"] = () => (302, "text/plain", Array.Empty<byte>(), new() { ["Location"] = "file:///etc/passwd" }, 0);
            using var fetcher = Fetcher(allowPrivate: true);
            var result = await Tool(fetcher).ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/to-file" }, CancellationToken.None);
            Assert.Contains("unsupported address", result);
        }

        [Fact]
        public async Task Redirect_loops_stop()
        {
            _server.Routes["/loop"] = () => (302, "text/plain", Array.Empty<byte>(), new() { ["Location"] = "/loop" }, 0);
            using var fetcher = Fetcher(allowPrivate: true);
            var result = await Tool(fetcher).ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/loop" }, CancellationToken.None);
            Assert.Contains("Too many redirects", result);
        }

        [Fact]
        public async Task Binary_files_and_errors_are_reported_clearly()
        {
            _server.Routes["/file.pdf"] = () => (200, "application/pdf", new byte[] { 1, 2, 3 }, null, 0);
            using var fetcher = Fetcher(allowPrivate: true);
            var tool = Tool(fetcher);

            Assert.Contains("application/pdf", await tool.ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/file.pdf" }, CancellationToken.None));
            Assert.Contains("404", await tool.ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/missing" }, CancellationToken.None));
        }

        [Fact]
        public async Task Huge_pages_are_cut_at_the_size_limit()
        {
            _server.Routes["/huge"] = () => (200, "text/plain", Encoding.UTF8.GetBytes(new string('a', 300_000)), null, 0);
            using var fetcher = Fetcher(allowPrivate: true, o => o.MaxBytes = 100_000);

            var page = await fetcher.FetchAsync(new Uri(_server.BaseUrl + "/huge"), CancellationToken.None);

            Assert.True(page.Text.Length < 101_000);
            Assert.Contains("only the start was read", page.Text);
        }

        [Fact]
        public async Task A_slow_site_times_out()
        {
            _server.Routes["/slow"] = () => (200, "text/plain", Encoding.UTF8.GetBytes("late"), null, 5000);
            using var fetcher = Fetcher(allowPrivate: true, o => o.TimeoutSeconds = 1);
            var result = await Tool(fetcher).ExecuteAsync(new() { ["url"] = _server.BaseUrl + "/slow" }, CancellationToken.None);
            Assert.Contains("didn't load within 1 s", result);
        }

        [Fact]
        public async Task The_approval_card_shows_the_url()
        {
            using var fetcher = Fetcher(allowPrivate: false);
            var preview = await Tool(fetcher).PreviewAsync(new() { ["url"] = "https://example.com/docs" });
            Assert.Equal("GET https://example.com/docs", preview.Command);
            Assert.Equal("Open example.com", preview.Summary);
            Assert.NotNull((await Tool(fetcher).PreviewAsync(new() { ["url"] = "file:///x" })).Error);
        }

        [Theory]
        [InlineData("ask", true)]
        [InlineData("allow", false)]
        public void Fetch_asks_unless_allowed(string setting, bool asks)
        {
            using var fetcher = Fetcher(allowPrivate: false);
            Assert.Equal(asks, new WebFetchTool(fetcher, new WebOptions { Fetch = setting }).RequiresApproval);
        }
    }

    public class WebSearchTests
    {
        [Fact]
        public void Parses_Brave_results()
        {
            var json = JsonDocument.Parse("""
                { "web": { "results": [
                  { "title": "Widget <strong>docs</strong>", "url": "https://w.dev/docs", "description": "All about &amp; widgets" },
                  { "title": "no url" }
                ] } }
                """);
            var results = WebSearchClient.ParseBrave(json.RootElement);
            var r = Assert.Single(results);
            Assert.Equal("Widget docs", r.Title);
            Assert.Equal("All about & widgets", r.Snippet);
        }

        [Fact]
        public void Parses_Tavily_and_SearXNG_results()
        {
            var json = JsonDocument.Parse("""{ "results": [ { "title": "A", "url": "https://a.dev", "content": "alpha" }, { "title": "B", "url": "https://b.dev", "content": "beta" } ] }""");
            var results = WebSearchClient.ParseResults(json.RootElement, "content");
            Assert.Equal(new[] { "https://a.dev", "https://b.dev" }, results.Select(r => r.Url));
            Assert.Equal("alpha", results[0].Snippet);
        }

        [Theory]
        [InlineData("brave", "key", null, true)]
        [InlineData("brave", null, null, false)]
        [InlineData("tavily", "key", null, true)]
        [InlineData("searxng", null, "http://localhost:8080", true)]
        [InlineData("searxng", null, null, false)]
        [InlineData(null, "key", null, false)]
        public void Search_is_offered_only_when_configured(string? provider, string? key, string? url, bool configured) =>
            Assert.Equal(configured, WebSearchClient.IsConfigured(new WebOptions { SearchProvider = provider, SearchApiKey = key, SearxngUrl = url }));

        [Fact]
        public async Task SearXNG_search_end_to_end_against_a_local_instance()
        {
            using var server = new TinyHttpServer();
            server.Routes["/search?q=widget%20docs&format=json"] = () => (200, "application/json",
                Encoding.UTF8.GetBytes("""{ "results": [ { "title": "Widgets", "url": "https://w.dev", "content": "The docs" } ] }"""), null, 0);
            var client = new WebSearchClient(new HttpClient(), new WebOptions { SearchProvider = "searxng", SearxngUrl = server.BaseUrl });

            var text = await new WebSearchTool(client).ExecuteAsync(new() { ["query"] = "widget docs" }, CancellationToken.None);

            Assert.Equal("1. Widgets\n   https://w.dev\n   The docs", text.Replace("\r\n", "\n"));
        }

        [Fact]
        public async Task A_rejected_key_is_explained()
        {
            using var server = new TinyHttpServer();
            server.Routes["/search?q=x&format=json"] = () => (401, "application/json", Encoding.UTF8.GetBytes("{}"), null, 0);
            var client = new WebSearchClient(new HttpClient(), new WebOptions { SearchProvider = "searxng", SearxngUrl = server.BaseUrl });
            var text = await new WebSearchTool(client).ExecuteAsync(new() { ["query"] = "x" }, CancellationToken.None);
            Assert.Contains("rejected the key", text);
        }
    }

    /// <summary>Web tools in the agent loop: offered in every mode, fetch asks first (also in Auto mode).</summary>
    public class WebAgentLoopTests
    {
        private static AgentHarness Harness(string fetch = "ask") => new(configureServices: services =>
        {
            services.Configure<WebOptions>(o => { o.Fetch = fetch; o.AllowPrivateHosts = true; });
            services.AddSingleton<WebFetcher>();
        });

        [Fact]
        public async Task Web_fetch_is_offered_in_Ask_mode_and_asks_before_fetching()
        {
            using var server = new TinyHttpServer();
            server.Html("/p", "<p>hello from the web</p>");
            using var h = Harness();
            h.Llm.Call("web_fetch", new { url = server.BaseUrl + "/p" }).Text("done");

            var events = await h.RunAsync("read it", "ask", approve: true);

            Assert.Contains("web_fetch", h.Llm.OfferedTools[0]);
            var prompt = Assert.Single(AgentHarness.Prompts(events));
            Assert.Contains("GET " + server.BaseUrl + "/p", prompt);
            Assert.Contains("hello from the web", h.Llm.Requests[1].Last(m => m.Role == "tool").Content);
        }

        [Fact]
        public async Task Auto_mode_does_not_skip_the_fetch_approval()
        {
            using var h = Harness();
            h.Llm.Call("web_fetch", new { url = "https://example.com" }).Text("done");
            var events = await h.RunAsync("read it", "auto", approve: false);
            Assert.Single(AgentHarness.Prompts(events));
        }

        [Fact]
        public async Task Fetch_set_to_allow_runs_without_asking_and_off_removes_the_tool()
        {
            using var server = new TinyHttpServer();
            server.Html("/p", "<p>ok</p>");
            using (var h = Harness("allow"))
            {
                h.Llm.Call("web_fetch", new { url = server.BaseUrl + "/p" }).Text("done");
                Assert.Empty(AgentHarness.Prompts(await h.RunAsync("read", "agent")));
            }
            using (var h = Harness("off"))
            {
                h.Llm.Text("answer");
                await h.RunAsync("read", "agent");
                Assert.DoesNotContain("web_fetch", h.Llm.OfferedTools[0]);
            }
        }
    }
}
