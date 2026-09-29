namespace Ai_Agent.Config
{
    /// <summary>
    /// Configuration for the agent behavior.
    /// Reads from the "Agent" section in appsettings.json.
    /// </summary>
    public class AgentOptions
    {
        // Must match the section name in appsettings.json
        public const string SectionName = "Agent";

        // How many times the agent can loop before forcing a stop
        public int MaxIterations { get; set; } = 10;

        // Maximum tokens allowed in the conversation context
        public int MaxContextTokens { get; set; } = 48000;

        // LLM calls in flight at once across all chats. Held only while the model streams,
        // never while a run waits for the user's Accept/Reject.
        public int MaxConcurrentLlmCalls { get; set; } = 2;

        // The folder where the agent can read/write files
        public string WorkspaceRoot { get; set; } = string.Empty;

        // Extra folders a client may pass as "workspace" (WorkspaceRoot is always allowed)
        public List<string> AllowedWorkspaces { get; set; } = new();

        // Long-term conversation memory (Postgres). The eval runner turns it off so evals leave no rows behind.
        public bool MemoryEnabled { get; set; } = true;

        // Auto mode runs these commands without asking (prefix match); anything else still needs approval
        public List<string> AutoApproveCommands { get; set; } = new()
        {
            "dotnet build", "dotnet test", "dotnet restore", "dotnet --version", "dotnet --list-sdks", "dotnet --info",
            "git status", "git diff", "git log", "git branch", "git --version"
        };

        // Step budget per request, by mode (Ask needs few steps; Agent/Auto implement multi-file changes)
        public Dictionary<string, int> MaxIterationsByMode { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ask"] = 8,
            ["plan"] = 15,
            ["agent"] = 40,
            ["auto"] = 40
        };

        public int MaxIterationsFor(string mode) =>
            MaxIterationsByMode.TryGetValue(mode, out var max) && max > 0 ? max : MaxIterations;

        // Change log (.ai_changes.ndjson) keeps this many recent sessions per workspace; older ones can't be reverted
        public int ChangeLogMaxSessions { get; set; } = 50;

        // Commands run_terminal may run, as "program subcommand" (e.g. "npm test", "npx tsc").
        // Config entries are ADDED to these defaults. Each command still needs the user's approval
        // unless it is also in AutoApproveCommands (Auto mode).
        public List<string> AllowedCommands { get; set; } = new()
        {
            "dotnet build", "dotnet test", "dotnet restore", "dotnet clean",
            "dotnet --version", "dotnet --list-sdks", "dotnet --info",
            "git status", "git diff", "git log", "git branch", "git --version"
        };

        // Optional services for semantic_search (the tool is simply not offered when they don't answer)
        public string ChromaUrl { get; set; } = "http://localhost:8000";
        public string OllamaUrl { get; set; } = LLM.OllamaEmbeddingService.DefaultBaseUrl;

        // Semantic index: at most this many code files per workspace
        public int MaxIndexedFiles { get; set; } = 2000;

        // Verify loop: commands to run after the agent changed files (empty = detect from the project's files)
        public List<string> VerifyCommands { get; set; } = new();

        // How many times the agent may try to fix a failed build/test before handing back to the user
        public int MaxVerifyFixes { get; set; } = 2;

        // Shared secret clients must send in the X-Agent-Token header
        public string ApiToken { get; set; } = string.Empty;
    }
}
