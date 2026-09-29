using Ai_Agent.Agent.Services;
using Ai_Agent.LLM;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Creates tool instances for a specific workspace.
    /// Allows multi-workspace support without changing tool internals.
    /// </summary>
    public class ToolFactory
    {
        private readonly ChangeTracker _changeTracker;
        private readonly OllamaEmbeddingService _embeddingService;
        private readonly ChromaDbService _chromaDbService;
        private readonly CodeVectorIndexer _vectorIndexer;
        private readonly IServiceProvider _serviceProvider;

        public ToolFactory(
            ChangeTracker changeTracker,
            OllamaEmbeddingService embeddingService,
            ChromaDbService chromaDbService,
            CodeVectorIndexer vectorIndexer,
            IServiceProvider serviceProvider)
        {
            _vectorIndexer = vectorIndexer;
            _changeTracker = changeTracker;
            _embeddingService = embeddingService;
            _chromaDbService = chromaDbService;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Creates a ToolRegistry with tools configured for a specific workspace.
        /// </summary>
        /// <param name="mode">ask/plan: read-only tools only (plan adds submit_plan); agent/auto: everything</param>
        public ToolRegistry CreateRegistry(string workspaceRoot, string mode = Models.AgentModes.Agent)
        {
            var logger = _serviceProvider.GetRequiredService<ILogger<ReplaceLinesTool>>();

            var registry = new ToolRegistry();
            registry.RegisterTool(new FileReaderTool(workspaceRoot));
            registry.RegisterTool(new FileWriterTool(workspaceRoot));
            registry.RegisterTool(new DirectoryBrowserTool(workspaceRoot));
            registry.RegisterTool(new CodeSearchTool(workspaceRoot));
            registry.RegisterTool(new MultiFileReaderTool(workspaceRoot));
            registry.RegisterTool(new ReplaceLinesTool(workspaceRoot, logger));
            var allowedCommands = _serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Config.AgentOptions>>().Value.AllowedCommands;
            registry.RegisterTool(new TerminalTool(workspaceRoot, allowedCommands));
            registry.RegisterTool(new EditFileTool(workspaceRoot));
            registry.RegisterTool(new DeleteFileTool(workspaceRoot));
            registry.RegisterTool(new MoveFileTool(workspaceRoot));
            registry.RegisterTool(new FindFilesTool(workspaceRoot));
            // Only once THIS workspace is indexed (Chroma + Ollama reachable); otherwise the model would get errors.
            // A workspace seen for the first time starts indexing in the background and gets the tool when ready.
            if (_vectorIndexer.IsAvailable(workspaceRoot))
                registry.RegisterTool(new SemanticSearchTool(_embeddingService, _chromaDbService, workspaceRoot));
            else
                _vectorIndexer.EnsureIndexedInBackground(workspaceRoot);

            // Tools of connected MCP servers ("mcp__server__tool"); read-only ones survive the filter below
            if (_serviceProvider.GetService<Mcp.McpConnectionManager>() is { } mcp)
                foreach (var tool in mcp.CreateTools())
                    registry.RegisterTool(tool);

            if (mode is Models.AgentModes.Ask or Models.AgentModes.Plan)
                registry.RemoveWhere(tool => !tool.IsReadOnly);   // enforced here, not only in the prompt

            if (mode == Models.AgentModes.Plan)
            {
                registry.RegisterTool(new AskQuestionsTool());
                registry.RegisterTool(new SubmitPlanTool());
            }
            else if (mode is Models.AgentModes.Agent or Models.AgentModes.Auto)
                registry.RegisterTool(new UpdatePlanTool());

            return registry;
        }
    }
}
