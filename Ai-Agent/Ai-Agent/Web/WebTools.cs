using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Web
{
    /// <summary>
    /// web_fetch: read a public web page (docs, an issue, a changelog) as Markdown. Read-only, so it is also
    /// offered in Ask and Plan mode; by default each fetch is shown for approval, because a hijacked prompt could
    /// try to send data out through a URL.
    /// </summary>
    public sealed class WebFetchTool : ITool
    {
        private readonly WebFetcher _fetcher;
        private readonly WebOptions _options;

        public WebFetchTool(WebFetcher fetcher, WebOptions options)
        {
            _fetcher = fetcher;
            _options = options;
        }

        public string Name => "web_fetch";

        public string Description =>
            "Read a public web page (documentation, a GitHub issue, a changelog, an API reference) as Markdown. " +
            "Use it when the answer depends on information that is not in the project. Long pages come in parts: " +
            "call again with 'start' to continue. Local and private network addresses are refused.";

        public Dictionary<string, string> Parameters => new()
        {
            ["url"] = "The full http(s) URL of the page",
            ["start"] = "[Optional] Character offset to continue a long page (from the previous result)"
        };

        public IReadOnlyDictionary<string, object> ParameterSchemas => new Dictionary<string, object>
        {
            ["start"] = ParamSchema.Integer(minimum: 0)
        };

        public bool IsReadOnly => true;
        public bool RequiresApproval => !string.Equals(_options.Fetch, "allow", StringComparison.OrdinalIgnoreCase);

        public Task<ToolPreview> PreviewAsync(Dictionary<string, string> parameters)
        {
            var url = parameters.GetValueOrDefault("url");
            if (WebFetcher.Validate(url, out var uri) is { } problem) return Task.FromResult(ToolPreview.Fail(problem));
            return Task.FromResult(new ToolPreview { Command = $"GET {uri}", Summary = $"Open {uri!.Host}" });
        }

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters) => ExecuteAsync(parameters, CancellationToken.None);

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            if (WebFetcher.Validate(parameters.GetValueOrDefault("url"), out var uri) is { } problem) return $"ERROR: {problem}";
            var start = int.TryParse(parameters.GetValueOrDefault("start"), out var s) && s > 0 ? s : 0;

            WebPage page;
            try
            {
                page = await _fetcher.FetchAsync(uri!, cancellationToken);
            }
            catch (WebFetchException e)
            {
                return $"ERROR: {e.Message}";
            }
            catch (OperationCanceledException)
            {
                return "ERROR: Cancelled by the user.";
            }

            if (page.Text.Length == 0) return $"(The page at {page.FinalUrl} has no readable text.)";
            if (start >= page.Text.Length) return $"ERROR: 'start' {start} is past the end of the page ({page.Text.Length} characters).";

            var length = Math.Min(_options.MaxChars, page.Text.Length - start);
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(page.Title)) sb.Append("# ").AppendLine(page.Title);
            sb.Append("URL: ").AppendLine(page.FinalUrl);
            if (start > 0) sb.AppendLine($"(continuing at character {start:N0})");
            sb.AppendLine().Append(page.Text, start, length);
            var next = start + length;
            if (next < page.Text.Length)
                sb.AppendLine().AppendLine().Append($"…(more: {page.Text.Length - next:N0} characters left; call web_fetch with start={next} to continue)");
            return sb.ToString();
        }
    }

    public sealed record SearchResult(string Title, string Url, string Snippet);

    /// <summary>
    /// web_search: search the web with the provider the user configured (Brave Search API, Tavily, or their own
    /// SearXNG). Read-only; it doesn't ask, like a search box, but only runs when the user configured a provider.
    /// </summary>
    public sealed class WebSearchTool : ITool
    {
        private readonly WebSearchClient _client;

        public WebSearchTool(WebSearchClient client) => _client = client;

        public string Name => "web_search";

        public string Description =>
            "Search the web. Returns the top results (title, URL, snippet); open one with web_fetch for details. " +
            "Use it for current information: library versions, error messages, documentation not in the project.";

        public Dictionary<string, string> Parameters => new() { ["query"] = "What to search for" };
        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters) => ExecuteAsync(parameters, CancellationToken.None);

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            var query = parameters.GetValueOrDefault("query")?.Trim();
            if (string.IsNullOrEmpty(query)) return "ERROR: Give a search query.";
            try
            {
                var results = await _client.SearchAsync(query, cancellationToken);
                if (results.Count == 0) return $"No results for \"{query}\".";
                var sb = new StringBuilder();
                for (var i = 0; i < results.Count; i++)
                {
                    sb.Append(i + 1).Append(". ").AppendLine(results[i].Title);
                    sb.Append("   ").AppendLine(results[i].Url);
                    if (!string.IsNullOrWhiteSpace(results[i].Snippet)) sb.Append("   ").AppendLine(results[i].Snippet);
                }
                return sb.ToString().TrimEnd();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return "ERROR: Cancelled by the user.";
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException or WebFetchException)
            {
                return $"ERROR: Web search failed: {e.Message}";
            }
        }
    }

    /// <summary>Calls the configured search provider. The provider URL is the user's own choice, so no address guard.</summary>
    public sealed class WebSearchClient
    {
        private const int Count = 8;
        private readonly HttpClient _http;
        private readonly WebOptions _options;

        public WebSearchClient(HttpClient http, WebOptions options)
        {
            _http = http;
            _options = options;
        }

        public static bool IsConfigured(WebOptions options) => (options.SearchProvider?.ToLowerInvariant()) switch
        {
            "brave" or "tavily" => !string.IsNullOrWhiteSpace(options.SearchApiKey),
            "searxng" => Uri.TryCreate(options.SearxngUrl, UriKind.Absolute, out _),
            _ => false
        };

        public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var ct = timeout.Token;

            switch (_options.SearchProvider?.ToLowerInvariant())
            {
                case "brave":
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={Count}");
                    request.Headers.Add("X-Subscription-Token", _options.SearchApiKey);
                    request.Headers.Accept.ParseAdd("application/json");
                    using var response = await Send(request, ct);
                    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    return ParseBrave(doc.RootElement);
                }
                case "tavily":
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
                    {
                        Content = JsonContent.Create(new { query, max_results = Count })
                    };
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.SearchApiKey);
                    using var response = await Send(request, ct);
                    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    return ParseResults(doc.RootElement, "content");
                }
                case "searxng":
                {
                    var baseUrl = _options.SearxngUrl!.TrimEnd('/');
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/search?q={Uri.EscapeDataString(query)}&format=json");
                    using var response = await Send(request, ct);
                    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    return ParseResults(doc.RootElement, "content");
                }
                default:
                    throw new WebFetchException("No search provider is configured.");
            }
        }

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new WebFetchException(status is 401 or 403
                    ? $"the search provider rejected the key (HTTP {status})."
                    : $"the search provider answered HTTP {status}.");
            }
            return response;
        }

        public static List<SearchResult> ParseBrave(JsonElement root) =>
            root.TryGetProperty("web", out var web) ? ParseResults(web, "description") : new List<SearchResult>();

        /// <summary>{ "results": [ { "title", "url", snippetField } ] } (Tavily, SearXNG, and Brave's "web" object).</summary>
        public static List<SearchResult> ParseResults(JsonElement root, string snippetField)
        {
            var list = new List<SearchResult>();
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return list;
            foreach (var r in results.EnumerateArray().Take(Count))
            {
                var url = r.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;
                var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
                var snippet = r.TryGetProperty(snippetField, out var sn) ? sn.GetString() : null;
                list.Add(new SearchResult(StripTags(title ?? url), url, StripTags(snippet ?? string.Empty)));
            }
            return list;
        }

        private static string StripTags(string text) =>
            System.Text.RegularExpressions.Regex.Replace(System.Net.WebUtility.HtmlDecode(text), "<[^>]+>", "").Trim();
    }
}
