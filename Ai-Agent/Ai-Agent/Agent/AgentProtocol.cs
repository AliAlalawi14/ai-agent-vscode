namespace Ai_Agent.Agent
{
    /// <summary>
    /// Version of the backend ↔ extension contract (request fields, stream events).
    /// Bump it whenever either side must change together; the extension compares it with its own
    /// PROTOCOL_VERSION (ai-chat-extension/src/shared/protocol.ts) and warns when they differ.
    /// 3 = history/context/model in requests, before/after in change events.
    /// 4 = plan files (planPath in requests, path in plan events), clarifying-questions events, isDeletion in change events.
    /// 5 = reviewEdits in requests (edits apply at once and are reviewed afterwards; commands still ask).
    /// 6 = MCP servers (Mcp__ServersJson, GET /api/Mcp/status, "mcp__server__tool" tools).
    /// 7 = verify loop (verify in requests, verify events) and budget cap (budgetUsd, limit events with reason "budget").
    /// </summary>
    public static class AgentProtocol
    {
        public const int Version = 7;

        /// <summary>When this backend build was compiled, so a stale running process is easy to spot.</summary>
        public static readonly DateTime BuildTimeUtc =
            File.GetLastWriteTimeUtc(typeof(AgentProtocol).Assembly.Location);
    }
}
