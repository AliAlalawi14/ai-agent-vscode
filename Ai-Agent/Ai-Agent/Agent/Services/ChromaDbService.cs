using System.Text;
using System.Text.Json;

namespace Ai_Agent.Agent.Services
{
    public class ChromaDbService
    {
        private readonly HttpClient _httpClient;
        private readonly string _collectionId;

        private string BasePath => $"/api/v2/tenants/default_tenant/databases/default_database/collections/{_collectionId}";

        public ChromaDbService(HttpClient httpClient, string collectionId = "b8743815-887b-4076-9802-ff1f123307c8")
        {
            _httpClient = httpClient;
            _collectionId = collectionId;
        }

        public async Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync("/api/v2/heartbeat", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        public async Task InitializeAsync()
        {
            try
            {
                // Check if collection exists; create if it doesn't
                var response = await _httpClient.GetAsync($"{BasePath}");
                if (!response.IsSuccessStatusCode)
                {
                    // Collection doesn't exist — create it
                    var createPayload = new
                    {
                        name = _collectionId,
                        metadata = new { description = "Ai-Agent code embeddings" }
                    };
                    var json = JsonSerializer.Serialize(createPayload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var createResponse = await _httpClient.PostAsync(
                        "/api/v2/tenants/default_tenant/databases/default_database/collections",
                        content);
                    createResponse.EnsureSuccessStatusCode();
                }
            }
            catch (HttpRequestException)
            {
                // ChromaDB might not be running — defer to indexer error handling
            }
        }

        /// <summary>
        /// Stores chunks with their ids and metadata (workspace, file, fileHash, lines). Upsert, so re-indexing is safe.
        /// </summary>
        public async Task UpsertChunksAsync(
            IReadOnlyList<string> ids,
            IReadOnlyList<CodeChunk> chunks,
            IReadOnlyList<float[]> embeddings,
            IReadOnlyList<Dictionary<string, object>> metadatas)
        {
            if (ids.Count == 0) return;
            var request = new
            {
                ids,
                embeddings = embeddings.Select(e => (object)e.ToList()).ToList(),
                documents = chunks.Select(c => c.Content).ToList(),
                metadatas
            };

            var response = await _httpClient.PostAsync($"{BasePath}/upsert", Json(request));
            response.EnsureSuccessStatusCode();
        }

        /// <summary>Metadata of every stored chunk matching <paramref name="where"/> (no documents or vectors).</summary>
        public async Task<List<Dictionary<string, JsonElement>>> GetMetadataAsync(object where)
        {
            var response = await _httpClient.PostAsync($"{BasePath}/get", Json(new { where, include = new[] { "metadatas" } }));
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var result = new List<Dictionary<string, JsonElement>>();
            if (doc.RootElement.TryGetProperty("metadatas", out var metas) && metas.ValueKind == JsonValueKind.Array)
            {
                foreach (var meta in metas.EnumerateArray())
                {
                    if (meta.ValueKind != JsonValueKind.Object) continue;
                    result.Add(meta.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
                }
            }
            return result;
        }

        /// <summary>Deletes every chunk matching <paramref name="where"/>.</summary>
        public async Task DeleteAsync(object where)
        {
            var response = await _httpClient.PostAsync($"{BasePath}/delete", Json(new { where }));
            response.EnsureSuccessStatusCode();
        }

        private static StringContent Json(object value) =>
            new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

        /// <summary>Nearest chunks to the query; <paramref name="where"/> limits the search (e.g. to one workspace).</summary>
        public async Task<List<CodeSearchResult>> SearchAsync(float[] queryEmbedding, int nResults = 5, object? where = null)
        {
            object request = where == null
                ? new { query_embeddings = new[] { queryEmbedding.ToList() }, n_results = nResults }
                : new { query_embeddings = new[] { queryEmbedding.ToList() }, n_results = nResults, where };

            var response = await _httpClient.PostAsync($"{BasePath}/query", Json(request));
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var results = new List<CodeSearchResult>();

            if (root.TryGetProperty("documents", out var documents) && documents.GetArrayLength() > 0)
            {
                var docs = documents[0];
                var metas = root.TryGetProperty("metadatas", out var m) ? m[0] : default;
                var distances = root.TryGetProperty("distances", out var d) ? d[0] : default;

                for (int i = 0; i < docs.GetArrayLength(); i++)
                {
                    results.Add(new CodeSearchResult
                    {
                        Content = docs[i].GetString() ?? "",
                        FilePath = GetMetaString(metas, i, "file"),
                        StartLine = GetMetaInt(metas, i, "startLine"),
                        EndLine = GetMetaInt(metas, i, "endLine"),
                        Score = distances.ValueKind != JsonValueKind.Undefined && i < distances.GetArrayLength()
                            ? distances[i].GetDouble() : 0
                    });
                }
            }

            return results;
        }

        private string GetMetaString(JsonElement metas, int i, string key)
        {
            try
            {
                if (metas.ValueKind == JsonValueKind.Array && i < metas.GetArrayLength())
                    if (metas[i].TryGetProperty(key, out var v))
                        return v.GetString() ?? "unknown";
            }
            catch { }
            return "unknown";
        }

        private int GetMetaInt(JsonElement metas, int i, string key)
        {
            try
            {
                if (metas.ValueKind == JsonValueKind.Array && i < metas.GetArrayLength())
                    if (metas[i].TryGetProperty(key, out var v))
                        return v.GetInt32();
            }
            catch { }
            return 0;
        }

    }

    public class CodeChunk
    {
        public string Content { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string ClassName { get; set; } = string.Empty;
        public string MethodName { get; set; } = string.Empty;
        /// <summary>"method", "declarations" or "code" (fixed window)</summary>
        public string Kind { get; set; } = "code";
    }

    public class CodeSearchResult
    {
        public string Content { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public double Score { get; set; }
    }
}
