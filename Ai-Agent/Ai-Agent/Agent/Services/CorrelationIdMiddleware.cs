namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Middleware that adds correlation IDs to all requests for distributed tracing.
    /// </summary>
    public class CorrelationIdMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<CorrelationIdMiddleware> _logger;
        public const string CorrelationIdHeader = "X-Correlation-ID";

        public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Get or generate correlation ID
            var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
                ?? Guid.NewGuid().ToString("N")[..16];

            // Store in HttpContext.Items for access throughout the request
            context.Items["CorrelationId"] = correlationId;

            // Add to response headers
            context.Response.Headers[CorrelationIdHeader] = correlationId;

            // Create a scope with the correlation ID for all logs in this request
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["RequestPath"] = context.Request.Path,
                ["RequestMethod"] = context.Request.Method
            }))
            {
                _logger.LogDebug("Request started with CorrelationId: {CorrelationId}", correlationId);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    await _next(context);
                }
                finally
                {
                    stopwatch.Stop();
                    _logger.LogDebug(
                        "Request completed in {ElapsedMs}ms - Status: {StatusCode}",
                        stopwatch.ElapsedMilliseconds,
                        context.Response.StatusCode);
                }
            }
        }
    }

    /// <summary>
    /// Extension methods for accessing correlation ID in controllers/services.
    /// </summary>
    public static class CorrelationIdExtensions
    {
        /// <summary>
        /// Get the current correlation ID from HttpContext.
        /// </summary>
        public static string GetCorrelationId(this HttpContext context)
        {
            return context.Items["CorrelationId"]?.ToString() ?? "unknown";
        }

        /// <summary>
        /// Get the current correlation ID from IHttpContextAccessor.
        /// </summary>
        public static string GetCorrelationId(this IHttpContextAccessor accessor)
        {
            return accessor.HttpContext?.GetCorrelationId() ?? "unknown";
        }
    }
}
