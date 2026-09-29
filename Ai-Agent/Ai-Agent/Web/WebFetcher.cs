using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace Ai_Agent.Web
{
    /// <summary>A fetched page as text, ready to hand to the model.</summary>
    public sealed record WebPage(string FinalUrl, string Title, string Text, string ContentType);

    /// <summary>
    /// Downloads public web pages for web_fetch: every connection goes through <see cref="NetworkGuard"/>,
    /// redirects are followed one checked hop at a time, and size and time are capped. Pages are cached for a
    /// few minutes so reading a long page in parts downloads it once.
    /// </summary>
    public sealed class WebFetcher : IDisposable
    {
        private const int MaxRedirects = 5;
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

        private readonly WebOptions _options;
        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, (DateTime At, WebPage Page)> _cache = new();

        public WebFetcher(IOptions<WebOptions> options)
        {
            _options = options.Value;
            _http = NetworkGuard.CreateClient(_options);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Stoat/0.2; +https://alialalawi14.github.io/stoat/)");
            _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/json;q=0.9,*/*;q=0.5");
        }

        /// <summary>Why a URL can't be fetched before any network access, or null.</summary>
        public static string? Validate(string? url, out Uri? uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return "Give a URL.";
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) return $"'{url}' is not a valid URL.";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "Only http and https URLs can be fetched.";
            if (!string.IsNullOrEmpty(uri.UserInfo)) return "URLs with a user name or password are not fetched.";
            return null;
        }

        public async Task<WebPage> FetchAsync(Uri uri, CancellationToken cancellationToken)
        {
            var key = uri.AbsoluteUri;
            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Page;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            try
            {
                var page = await FetchUncachedAsync(uri, timeout.Token);
                _cache[key] = (DateTime.UtcNow, page);
                if (_cache.Count > 50)
                    foreach (var old in _cache.Where(e => DateTime.UtcNow - e.Value.At > CacheFor).Select(e => e.Key).ToList())
                        _cache.TryRemove(old, out _);
                return page;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WebFetchException($"The page didn't load within {_options.TimeoutSeconds} s.");
            }
        }

        private async Task<WebPage> FetchUncachedAsync(Uri uri, CancellationToken cancellationToken)
        {
            var current = uri;
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (HttpRequestException e) when (e is BlockedAddressException || e.InnerException is BlockedAddressException)
                {
                    throw new WebFetchException((e as BlockedAddressException ?? (BlockedAddressException)e.InnerException!).Message);
                }
                catch (HttpRequestException e)
                {
                    throw new WebFetchException($"Couldn't reach {current.Host}: {e.Message}");
                }

                using (response)
                {
                    if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                    {
                        if (hop >= MaxRedirects) throw new WebFetchException("Too many redirects.");
                        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                        if (Validate(next.ToString(), out _) is { } problem) throw new WebFetchException($"Redirected to an unsupported address: {problem}");
                        current = next;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                        throw new WebFetchException($"The site answered {(int)response.StatusCode} {response.ReasonPhrase}.");

                    var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/html";
                    if (!IsText(mediaType))
                        throw new WebFetchException($"It's a {mediaType} file, not a web page or text; only pages and text can be read.");

                    var bytes = await ReadCappedAsync(response.Content, cancellationToken);
                    var body = Decode(bytes.Data, response.Content.Headers.ContentType);
                    var suffix = bytes.Truncated ? $"\n\n…(the page is larger than {_options.MaxBytes / 1024 / 1024} MB; only the start was read)" : "";

                    if (mediaType is "text/html" or "application/xhtml+xml")
                    {
                        var (title, markdown) = HtmlToMarkdown.Convert(body, current);
                        return new WebPage(current.ToString(), title, markdown + suffix, mediaType);
                    }
                    return new WebPage(current.ToString(), string.Empty, body + suffix, mediaType);
                }
            }
        }

        private static bool IsText(string mediaType) =>
            mediaType.StartsWith("text/") || mediaType is "application/json" or "application/xml" or "application/xhtml+xml" or "application/javascript"
            || mediaType.EndsWith("+json") || mediaType.EndsWith("+xml");

        private async Task<(byte[] Data, bool Truncated)> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                var room = _options.MaxBytes - (int)buffer.Length;
                if (read >= room)
                {
                    buffer.Write(chunk, 0, room);
                    return (buffer.ToArray(), true);
                }
                buffer.Write(chunk, 0, read);
            }
            return (buffer.ToArray(), false);
        }

        private static string Decode(byte[] data, MediaTypeHeaderValue? contentType)
        {
            try
            {
                var charset = contentType?.CharSet?.Trim('"');
                if (!string.IsNullOrEmpty(charset)) return Encoding.GetEncoding(charset).GetString(data);
            }
            catch (ArgumentException) { /* unknown charset: fall back to UTF-8 */ }
            return Encoding.UTF8.GetString(data);
        }

        public void Dispose() => _http.Dispose();
    }

    public sealed class WebFetchException : Exception
    {
        public WebFetchException(string message) : base(message) { }
    }
}
