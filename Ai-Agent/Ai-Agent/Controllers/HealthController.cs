using Ai_Agent.Agent.Services;
using Ai_Agent.Data;
using Ai_Agent.LLM;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Ai_Agent.Agent.Services.CorrelationIdExtensions;

namespace Ai_Agent.Controllers
{
    /// <summary>
    /// Health check endpoints for monitoring system status.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        private readonly AgentMetrics _metrics;
        private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
        private readonly ILLMClient _llmClient;
        private readonly ILogger<HealthController> _logger;

        public HealthController(
            AgentMetrics metrics,
            IDbContextFactory<AppDbContext> dbContextFactory,
            ILLMClient llmClient,
            ILogger<HealthController> logger)
        {
            _metrics = metrics;
            _dbContextFactory = dbContextFactory;
            _llmClient = llmClient;
            _logger = logger;
        }

        /// <summary>
        /// Basic health check - returns 200 if service is running.
        /// </summary>
        [HttpGet]
        public IActionResult Health()
        {
            return Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                // The extension compares these to detect a stale backend process
                protocolVersion = Agent.AgentProtocol.Version,
                buildTime = Agent.AgentProtocol.BuildTimeUtc,
                correlationId = HttpContext.GetCorrelationId()
            });
        }

        /// <summary>
        /// Detailed health check with dependency status.
        /// </summary>
        [HttpGet("detailed")]
        public async Task<IActionResult> DetailedHealth()
        {
            var checks = new Dictionary<string, object>();
            var overallHealthy = true;

            // Database check
            try
            {
                await using var context = await _dbContextFactory.CreateDbContextAsync();
                await context.Database.ExecuteSqlRawAsync("SELECT 1");
                checks["database"] = new { status = "healthy", latency = 0 };
            }
            catch (Exception ex)
            {
                checks["database"] = new { status = "unhealthy", error = ex.Message };
                overallHealthy = false;
            }

            // LLM: GET /models (reachability + API key, no tokens spent)
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var llmOk = await _llmClient.HealthCheckAsync(timeout.Token);
            checks["llm"] = new { status = llmOk ? "healthy" : "unhealthy", provider = _llmClient.ProviderName };
            overallHealthy &= llmOk;

            // Optional: semantic search needs Chroma + Ollama; the agent works without them
            var chroma = HttpContext.RequestServices.GetRequiredService<ChromaDbService>();
            var ollama = HttpContext.RequestServices.GetRequiredService<OllamaEmbeddingService>();
            var chromaOk = await chroma.HeartbeatAsync(timeout.Token);
            var ollamaOk = await ollama.PingAsync(timeout.Token);
            checks["vector_db"] = new { status = chromaOk ? "healthy" : "unavailable", provider = "chromadb", optional = true };
            checks["embeddings"] = new { status = ollamaOk ? "healthy" : "unavailable", provider = "ollama", optional = true };
            checks["semantic_search"] = new { status = HttpContext.RequestServices.GetRequiredService<CodeVectorIndexer>().AnyAvailable ? "enabled" : "disabled", optional = true };

            var response = new
            {
                status = !overallHealthy ? "unhealthy" : (chromaOk && ollamaOk ? "healthy" : "degraded"),
                timestamp = DateTime.UtcNow,
                correlationId = HttpContext.GetCorrelationId(),
                checks,
                llmProvider = _llmClient.ProviderName
            };

            return overallHealthy ? Ok(response) : StatusCode(503, response);
        }

        /// <summary>
        /// Get current metrics snapshot.
        /// </summary>
        [HttpGet("metrics")]
        public IActionResult Metrics()
        {
            var snapshot = _metrics.GetMetricsSnapshot();

            return Ok(new
            {
                timestamp = DateTime.UtcNow,
                correlationId = HttpContext.GetCorrelationId(),
                metrics = snapshot
            });
        }

        /// <summary>
        /// Get current system information.
        /// </summary>
        [HttpGet("info")]
        public IActionResult Info()
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();

            return Ok(new
            {
                service = "Ai-Agent",
                version = GetType().Assembly.GetName().Version?.ToString() ?? "unknown",
                timestamp = DateTime.UtcNow,
                correlationId = HttpContext.GetCorrelationId(),
                runtime = new
                {
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    processorCount = Environment.ProcessorCount,
                    memory = new
                    {
                        workingSetMB = process.WorkingSet64 / 1024 / 1024,
                        gcTotalMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024
                    },
                    uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime()
                }
            });
        }

        /// <summary>
        /// Reset metrics (use with caution).
        /// </summary>
        [HttpPost("metrics/reset")]
        public IActionResult ResetMetrics()
        {
            _metrics.Reset();
            _logger.LogInformation("Metrics reset by request {CorrelationId}", HttpContext.GetCorrelationId());
            return Ok(new { message = "Metrics reset", timestamp = DateTime.UtcNow });
        }
    }
}
