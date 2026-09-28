using Ai_Agent.Agent.Services;
using Ai_Agent.LLM;
using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Searches code by meaning using embeddings.
    /// Finds relevant code even when different words are used.
    /// </summary>
    public class SemanticSearchTool : ITool
    {
        private readonly OllamaEmbeddingService _embeddingService;
        private readonly ChromaDbService _chromaDbService;
        private readonly string _workspaceRoot;

        public string Name => "semantic_search";
        public bool IsReadOnly => true;
        public string Description => "Searches code by meaning (not just exact text). Finds relevant code even with different wording.";

        public Dictionary<string, string> Parameters => new()
    {
        { "query", "What you're looking for in plain English (e.g., 'login logic', 'error handling')" }
    };

        public SemanticSearchTool(OllamaEmbeddingService embeddingService, ChromaDbService chromaDbService, string workspaceRoot)
        {
            _embeddingService = embeddingService;
            _chromaDbService = chromaDbService;
            _workspaceRoot = workspaceRoot;
        }

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
                return "ERROR: Missing 'query' parameter";

            try
            {
                // Same embedding endpoint as the indexed chunks (/api/embed normalizes; mixing endpoints skews distances)
                var queryEmbedding = (await _embeddingService.GetEmbeddingsAsync(new[] { query }))[0];

                // Only this workspace's chunks
                var results = await _chromaDbService.SearchAsync(queryEmbedding, nResults: 5,
                    where: CodeVectorIndexer.WhereWorkspace(_workspaceRoot));

                if (results.Count == 0)
                    return $"No results found for: {query}";

                var output = new System.Text.StringBuilder();
                output.AppendLine($"Semantic search results for: '{query}'");
                output.AppendLine();

                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    output.AppendLine($"### Result {i + 1} (Score: {r.Score:F2})");
                    output.AppendLine($"File: {r.FilePath} (lines {r.StartLine}-{r.EndLine})");
                    output.AppendLine($"```");
                    output.AppendLine(r.Content);
                    output.AppendLine($"```");
                    output.AppendLine();
                }

                return output.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }
    }
}
