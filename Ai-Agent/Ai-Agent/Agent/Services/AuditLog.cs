using System.Text.Json;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Append-only audit trail of what the agent did: every tool call (with redacted, truncated arguments and
    /// its outcome), every approval decision, and a summary per run. One NDJSON file per day in logs/.
    /// Best-effort: an audit failure is logged but never fails the user's request.
    /// </summary>
    public class AuditLog
    {
        private readonly string _directory;
        private readonly ILogger<AuditLog> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private const int MaxValueChars = 400;

        public AuditLog(IHostEnvironment environment, ILogger<AuditLog> logger)
        {
            _directory = Path.Combine(environment.ContentRootPath, "logs");
            _logger = logger;
        }

        public Task ToolCallAsync(string correlationId, string sessionId, string workspace, string mode,
            string tool, IReadOnlyDictionary<string, string> arguments, string outcome) =>
            WriteAsync(new
            {
                type = "tool",
                correlationId,
                sessionId,
                workspace,
                mode,
                tool,
                arguments = arguments.ToDictionary(a => a.Key, a => Shorten(SecretRedactor.Redact(a.Value))),
                outcome
            });

        public Task ApprovalAsync(string correlationId, string approvalId, string tool, string decision) =>
            WriteAsync(new { type = "approval", correlationId, approvalId, tool, decision });

        public Task RunAsync(object summary) => WriteAsync(new { type = "run", summary });

        private async Task WriteAsync(object entry)
        {
            try
            {
                var line = JsonSerializer.Serialize(new { time = DateTime.UtcNow, entry }) + "\n";
                await _lock.WaitAsync();
                try
                {
                    Directory.CreateDirectory(_directory);
                    await File.AppendAllTextAsync(Path.Combine(_directory, $"audit-{DateTime.UtcNow:yyyyMMdd}.ndjson"), line);
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit log write failed");
            }
        }

        private static string Shorten(string value) =>
            value.Length <= MaxValueChars ? value : value[..MaxValueChars] + $"…({value.Length} chars)";
    }
}
