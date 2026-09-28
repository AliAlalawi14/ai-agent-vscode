using System.Collections.Concurrent;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Tracks runtime metrics for the agent system.
    /// Thread-safe for concurrent operations.
    /// </summary>
    public class AgentMetrics
    {
        private readonly ConcurrentDictionary<string, long> _counters = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _timings = new();
        private readonly ILogger<AgentMetrics> _logger;

        public AgentMetrics(ILogger<AgentMetrics> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Record an agent iteration completion.
        /// </summary>
        public void RecordIteration(string correlationId, int iterationNumber, int tokenCount)
        {
            IncrementCounter("agent_iterations_total");
            _logger.LogDebug("Iteration {Iteration} completed for {CorrelationId}. Tokens: {TokenCount}",
                iterationNumber, correlationId, tokenCount);
        }

        /// <summary>
        /// Record token usage for a request.
        /// </summary>
        public void RecordTokenUsage(string correlationId, int inputTokens, int outputTokens, string model)
        {
            IncrementCounter("agent_tokens_input", inputTokens);
            IncrementCounter("agent_tokens_output", outputTokens);
            IncrementCounter("agent_tokens_total", inputTokens + outputTokens);

            _logger.LogInformation("Token usage for {CorrelationId}: Input={Input}, Output={Output}, Model={Model}",
                correlationId, inputTokens, outputTokens, model);
        }

        /// <summary>
        /// Record tool execution timing.
        /// </summary>
        public void RecordToolExecution(string toolName, TimeSpan duration, bool success)
        {
            var metricKey = $"tool_duration_{toolName}";
            RecordTiming(metricKey, duration.TotalMilliseconds);

            if (success)
            {
                IncrementCounter($"tool_executions_success_{toolName}");
            }
            else
            {
                IncrementCounter($"tool_executions_failure_{toolName}");
            }

            _logger.LogDebug("Tool {ToolName} executed in {DurationMs:F0}ms (Success={Success})",
                toolName, duration.TotalMilliseconds, success);
        }

        /// <summary>
        /// Record LLM request timing.
        /// </summary>
        public void RecordLLMRequest(TimeSpan duration, bool success)
        {
            RecordTiming("llm_request_duration", duration.TotalMilliseconds);
            IncrementCounter(success ? "llm_requests_success" : "llm_requests_failure");

            _logger.LogDebug("LLM request completed in {DurationMs:F0}ms (Success={Success})",
                duration.TotalMilliseconds, success);
        }

        /// <summary>
        /// Record a completed agent run.
        /// </summary>
        public void RecordAgentRun(string correlationId, bool success, int totalIterations, int totalTokens)
        {
            IncrementCounter(success ? "agent_runs_success" : "agent_runs_failure");
            RecordTiming("agent_run_duration", GetElapsedForCorrelation(correlationId));

            _logger.LogInformation(
                "Agent run {CorrelationId} completed: Success={Success}, Iterations={Iterations}, TotalTokens={TotalTokens}",
                correlationId, success, totalIterations, totalTokens);
        }

        /// <summary>
        /// Get current metric snapshot.
        /// </summary>
        public Dictionary<string, object> GetMetricsSnapshot()
        {
            var snapshot = new Dictionary<string, object>();

            foreach (var counter in _counters)
            {
                snapshot[counter.Key] = counter.Value;
            }

            foreach (var timing in _timings)
            {
                var values = timing.Value.ToArray();
                if (values.Length > 0)
                {
                    snapshot[$"{timing.Key}_avg"] = values.Average();
                    snapshot[$"{timing.Key}_count"] = values.Length;
                }
            }

            return snapshot;
        }

        /// <summary>
        /// Reset all metrics (useful for testing).
        /// </summary>
        public void Reset()
        {
            _counters.Clear();
            _timings.Clear();
        }

        private void IncrementCounter(string key, long value = 1)
        {
            _counters.AddOrUpdate(key, value, (_, current) => current + value);
        }

        private void RecordTiming(string key, double milliseconds)
        {
            var queue = _timings.GetOrAdd(key, _ => new ConcurrentQueue<double>());
            queue.Enqueue(milliseconds);

            // Keep only last 1000 measurements to prevent unbounded growth
            while (queue.Count > 1000)
            {
                queue.TryDequeue(out _);
            }
        }

        private readonly ConcurrentDictionary<string, DateTime> _startTimes = new();

        internal void StartCorrelation(string correlationId)
        {
            _startTimes[correlationId] = DateTime.UtcNow;
        }

        private double GetElapsedForCorrelation(string correlationId)
        {
            if (_startTimes.TryRemove(correlationId, out var startTime))
            {
                return (DateTime.UtcNow - startTime).TotalMilliseconds;
            }
            return 0;
        }
    }
}
