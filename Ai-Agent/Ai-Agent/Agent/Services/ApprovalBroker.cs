using System.Collections.Concurrent;

namespace Ai_Agent.Agent.Services
{
    public enum ApprovalDecision { Approved, Rejected, TimedOut, Cancelled }

    /// <summary>
    /// Holds the agent loop at a side-effecting tool call (file write, command) until the user
    /// clicks Accept or Reject in the chat. The loop registers an id, streams an approval event,
    /// then awaits; POST /api/Agent/approve resolves it.
    /// </summary>
    public class ApprovalBroker
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();

        /// <summary>Must be called before the approval event is sent, so an instant click is not lost.</summary>
        public void Register(string approvalId) =>
            _pending[approvalId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Returns false if nothing is waiting for this id (already decided, timed out, or unknown).</summary>
        public bool Resolve(string approvalId, bool approved) =>
            _pending.TryGetValue(approvalId, out var waiter) && waiter.TrySetResult(approved);

        /// <summary>Never throws: timeouts and client disconnects come back as a decision.</summary>
        public async Task<ApprovalDecision> WaitAsync(string approvalId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!_pending.TryGetValue(approvalId, out var waiter))
                return ApprovalDecision.Cancelled;

            try
            {
                var approved = await waiter.Task.WaitAsync(timeout, cancellationToken);
                return approved ? ApprovalDecision.Approved : ApprovalDecision.Rejected;
            }
            catch (TimeoutException)
            {
                return ApprovalDecision.TimedOut;
            }
            catch (OperationCanceledException)
            {
                return ApprovalDecision.Cancelled;
            }
            finally
            {
                _pending.TryRemove(approvalId, out _);
            }
        }
    }
}
