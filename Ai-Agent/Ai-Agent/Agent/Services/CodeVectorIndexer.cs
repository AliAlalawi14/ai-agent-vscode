using Ai_Agent.LLM;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Keeps each workspace's code in ChromaDB for semantic_search.
    ///
    /// Incremental: every chunk carries workspace + file + fileHash metadata. A re-index only embeds files
    /// whose content changed, and removes chunks of changed or deleted files first, so the index never holds
    /// stale copies. Chunking (CodeChunker) covers every line of a file, not only method bodies.
    /// </summary>
    public class CodeVectorIndexer
    {
        private readonly OllamaEmbeddingService _embeddingService;
        private readonly ChromaDbService _chromaDbService;
        private readonly IOptions<Config.AgentOptions> _options;
        private readonly ILogger<CodeVectorIndexer> _logger;

        // Workspaces whose index is ready (semantic_search is offered only for these)
        private readonly ConcurrentDictionary<string, bool> _ready = new(StringComparer.OrdinalIgnoreCase);
        // One index run per workspace at a time
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".js", ".jsx", ".ts", ".tsx", ".py" };
        private static readonly string[] ExcludedFolders = { "bin", "obj", ".git", ".vs", "node_modules" };

        public CodeVectorIndexer(
            OllamaEmbeddingService embeddingService,
            ChromaDbService chromaDbService,
            IOptions<Config.AgentOptions> options,
            ILogger<CodeVectorIndexer> logger)
        {
            _embeddingService = embeddingService;
            _chromaDbService = chromaDbService;
            _options = options;
            _logger = logger;
        }

        /// <summary>True once this workspace was indexed successfully (Chroma + Ollama reachable).</summary>
        public bool IsAvailable(string workspaceRoot) => _ready.TryGetValue(Normalize(workspaceRoot), out var ok) && ok;

        /// <summary>True if any workspace is indexed (for the health endpoint).</summary>
        public bool AnyAvailable => _ready.Values.Any(v => v);

        /// <summary>Starts indexing a workspace in the background unless it was already tried.</summary>
        public void EnsureIndexedInBackground(string workspaceRoot)
        {
            // Done, running or failed: a failure is not retried on every message (POST /api/agent/index retries)
            if (!_ready.TryAdd(Normalize(workspaceRoot), false)) return;
            _ = Task.Run(() => IndexAsync(workspaceRoot));
        }

        /// <summary>
        /// Brings the workspace's index up to date. Returns what was (re-)embedded, skipped and removed.
        /// </summary>
        public async Task<IndexStats> IndexAsync(string workspaceRoot, int? maxFiles = null)
        {
            var key = Normalize(workspaceRoot);
            var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                // Chroma and Ollama are optional: a quick probe, so a machine without them gets one calm log line
                // instead of retries and a stack trace on every start
                using (var probe = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    if (!await _chromaDbService.HeartbeatAsync(probe.Token) || !await _embeddingService.PingAsync(probe.Token))
                    {
                        _ready[key] = false;
                        _logger.LogInformation(
                            "Semantic search is off for {Workspace}: Chroma ({Chroma}) or Ollama ({Ollama}) isn't running. " +
                            "It's optional; the other search tools work without it.",
                            key, _options.Value.ChromaUrl, _options.Value.OllamaUrl);
                        return new IndexStats { Error = "Chroma or Ollama is not reachable" };
                    }
                }

                await _chromaDbService.InitializeAsync();

                var max = maxFiles ?? _options.Value.MaxIndexedFiles;
                var files = EnumerateCodeFiles(key).Take(max).ToList();

                // What is stored now for this workspace: file -> hash
                var stored = (await _chromaDbService.GetMetadataAsync(WhereWorkspace(key)))
                    .Where(m => m.ContainsKey("file"))
                    .GroupBy(m => m["file"].GetString() ?? "")
                    .ToDictionary(g => g.Key, g => g.First().TryGetValue("fileHash", out var h) ? h.GetString() : null);

                var stats = new IndexStats { Files = files.Count };
                var seen = new HashSet<string>();
                foreach (var file in files)
                {
                    var relative = Path.GetRelativePath(key, file).Replace('\\', '/');
                    seen.Add(relative);
                    string content;
                    try { content = await File.ReadAllTextAsync(file); }
                    catch (IOException) { continue; }

                    var hash = Hash(content);
                    if (stored.TryGetValue(relative, out var storedHash) && storedHash == hash)
                    {
                        stats.UnchangedFiles++;
                        continue;
                    }

                    stats.EmbeddedChunks += await IndexFileContentAsync(key, relative, content, hash, replaceExisting: stored.ContainsKey(relative));
                    stats.ChangedFiles++;
                }

                // Files that no longer exist (or fell out of the file cap): drop their chunks
                foreach (var gone in stored.Keys.Where(f => !seen.Contains(f)))
                {
                    await _chromaDbService.DeleteAsync(WhereFile(key, gone));
                    stats.RemovedFiles++;
                }

                _ready[key] = files.Count > 0;
                _logger.LogInformation(
                    "Semantic index {Workspace}: {Files} files, {Unchanged} unchanged, {Changed} changed ({Chunks} chunks embedded), {Removed} removed",
                    key, stats.Files, stats.UnchangedFiles, stats.ChangedFiles, stats.EmbeddedChunks, stats.RemovedFiles);
                return stats;
            }
            catch (Exception ex)
            {
                _ready[key] = false;
                _logger.LogWarning("Code indexing unavailable for {Workspace} (semantic_search disabled): {Message}", key, ex.Message);
                return new IndexStats { Error = ex.Message };
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Re-indexes one file after the agent changed it (no-op if the workspace isn't indexed).</summary>
        public async Task ReindexFileAsync(string workspaceRoot, string relativePath)
        {
            var key = Normalize(workspaceRoot);
            if (!IsAvailable(key) || !SupportedExtensions.Contains(Path.GetExtension(relativePath))) return;

            var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                var relative = relativePath.Replace('\\', '/');
                var full = Path.Combine(key, relative);
                if (!File.Exists(full))
                {
                    await _chromaDbService.DeleteAsync(WhereFile(key, relative));
                    return;
                }
                var content = await File.ReadAllTextAsync(full);
                await IndexFileContentAsync(key, relative, content, Hash(content), replaceExisting: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Re-indexing {File} failed: {Message}", relativePath, ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<int> IndexFileContentAsync(string workspaceKey, string relative, string content, string hash, bool replaceExisting)
        {
            if (replaceExisting)
                await _chromaDbService.DeleteAsync(WhereFile(workspaceKey, relative));

            var chunks = CodeChunker.Chunk(relative, content.Replace("\r\n", "\n").Split('\n'));
            if (chunks.Count == 0) return 0;

            var embeddings = await _embeddingService.GetEmbeddingsAsync(chunks.Select(c => c.Content).ToList());
            var workspaceId = Hash(workspaceKey)[..8];
            var ids = chunks.Select((_, i) => $"{workspaceId}:{relative}:{i}").ToList();
            var metadatas = chunks.Select(c => new Dictionary<string, object>
            {
                ["workspace"] = workspaceKey,
                ["file"] = relative,
                ["fileHash"] = hash,
                ["startLine"] = c.StartLine,
                ["endLine"] = c.EndLine,
                ["kind"] = c.Kind
            }).ToList();

            await _chromaDbService.UpsertChunksAsync(ids, chunks, embeddings, metadatas);
            return chunks.Count;
        }

        /// <summary>Chroma filter: one workspace (used by semantic_search).</summary>
        public static object WhereWorkspace(string workspaceRoot) =>
            new Dictionary<string, object> { ["workspace"] = Normalize(workspaceRoot) };

        /// <summary>Chroma filter: one file in one workspace.</summary>
        private static object WhereFile(string workspaceKey, string relative) =>
            new Dictionary<string, object>
            {
                ["$and"] = new object[]
                {
                    new Dictionary<string, object> { ["workspace"] = workspaceKey },
                    new Dictionary<string, object> { ["file"] = relative }
                }
            };

        private static IEnumerable<string> EnumerateCodeFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] subdirs, files;
                try
                {
                    subdirs = Directory.GetDirectories(dir);
                    files = Directory.GetFiles(dir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var sub in subdirs.OrderBy(d => d, StringComparer.Ordinal))
                    if (!ExcludedFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                        pending.Push(sub);
                foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
                    if (SupportedExtensions.Contains(Path.GetExtension(file)))
                        yield return file;
            }
        }

        private static string Normalize(string workspaceRoot) =>
            Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static string Hash(string content) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public class IndexStats
    {
        public int Files { get; set; }
        public int UnchangedFiles { get; set; }
        public int ChangedFiles { get; set; }
        public int RemovedFiles { get; set; }
        public int EmbeddedChunks { get; set; }
        public string? Error { get; set; }
    }
}
