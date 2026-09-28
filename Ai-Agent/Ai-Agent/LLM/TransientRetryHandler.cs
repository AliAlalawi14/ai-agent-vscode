using System.Net;

namespace Ai_Agent.LLM
{
    /// <summary>
    /// Retries an HTTP call on transient failures: network errors, 408, 429 and 5xx (except 501).
    /// Exponential backoff with jitter; honours Retry-After (capped). Only the request/response headers
    /// are retried: a streaming body that already started is never replayed.
    /// </summary>
    public class TransientRetryHandler : DelegatingHandler
    {
        private readonly ILogger<TransientRetryHandler> _logger;
        private const int MaxAttempts = 3;
        private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

        public TransientRetryHandler(ILogger<TransientRetryHandler> logger)
        {
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                HttpResponseMessage? response = null;
                try
                {
                    response = await base.SendAsync(request, cancellationToken);
                    if (!IsTransient(response.StatusCode) || attempt == MaxAttempts)
                        return response;
                }
                catch (HttpRequestException ex) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("HTTP {Method} {Uri} failed (attempt {Attempt}/{Max}): {Error}",
                        request.Method, request.RequestUri, attempt, MaxAttempts, ex.Message);
                }

                var delay = RetryDelay(response, attempt);
                if (response != null)
                {
                    _logger.LogWarning("HTTP {Method} {Uri} returned {Status} (attempt {Attempt}/{Max}); retrying in {Delay} ms",
                        request.Method, request.RequestUri, (int)response.StatusCode, attempt, MaxAttempts, (int)delay.TotalMilliseconds);
                    response.Dispose();
                }
                await Task.Delay(delay, cancellationToken);
            }
        }

        private static bool IsTransient(HttpStatusCode status) =>
            status == HttpStatusCode.RequestTimeout ||
            status == HttpStatusCode.TooManyRequests ||
            ((int)status >= 500 && status != HttpStatusCode.NotImplemented);

        private static TimeSpan RetryDelay(HttpResponseMessage? response, int attempt)
        {
            var retryAfter = response?.Headers.RetryAfter?.Delta;
            if (retryAfter.HasValue)
                return retryAfter.Value < MaxDelay ? retryAfter.Value : MaxDelay;

            var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));
            return backoff < MaxDelay ? backoff : MaxDelay;
        }
    }
}
