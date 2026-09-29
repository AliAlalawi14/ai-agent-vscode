using System.Text;
using System.Text.Json;

namespace Ai_Agent.LLM
{
    /// <summary>
    /// Calls Ollama's embedding API to convert text to vectors.
    /// Uses the nomic-embed-text model running locally on port 11434.
    /// </summary>
    public class OllamaEmbeddingService
    {
        private readonly HttpClient _httpClient;
        public const string DefaultBaseUrl = "http://localhost:11434";
        private readonly string _baseUrl;
        private string OllamaUrl => $"{_baseUrl}/api/embeddings";
        private string OllamaBatchUrl => $"{_baseUrl}/api/embed";
        private const string ModelName = "nomic-embed-text";

        public OllamaEmbeddingService(string baseUrl = DefaultBaseUrl)
            : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, baseUrl)
        {
        }

        /// <summary>For tests: a client whose handler stands in for Ollama.</summary>
        public OllamaEmbeddingService(HttpClient httpClient, string baseUrl = DefaultBaseUrl)
        {
            _httpClient = httpClient;
            _baseUrl = baseUrl.TrimEnd('/');
        }

        /// <summary>
        /// Converts text to an embedding vector.
        /// </summary>
        public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var request = new
            {
                model = ModelName,
                prompt = text
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(OllamaUrl, content);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OllamaEmbeddingResponse>(responseBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return result?.Embedding ?? Array.Empty<float>();
        }

        // Ollama without /api/embed (older than 0.3): fall back to one request per text
        private bool _batchUnsupported;

        /// <summary>
        /// Converts multiple texts to embeddings: one /api/embed request per batch of <paramref name="batchSize"/>.
        /// </summary>
        public async Task<List<float[]>> GetEmbeddingsAsync(IReadOnlyList<string> texts, int batchSize = 16)
        {
            var results = new List<float[]>(texts.Count);
            for (var i = 0; i < texts.Count; i += batchSize)
            {
                var batch = texts.Skip(i).Take(batchSize).ToList();
                var vectors = _batchUnsupported ? null : await TryEmbedBatchAsync(batch);
                if (vectors == null || vectors.Count != batch.Count)
                {
                    vectors = new List<float[]>();
                    foreach (var text in batch)
                        vectors.Add(await GetEmbeddingAsync(text));
                }
                results.AddRange(vectors);
            }
            return results;
        }

        private async Task<List<float[]>?> TryEmbedBatchAsync(List<string> batch)
        {
            var json = JsonSerializer.Serialize(new { model = ModelName, input = batch });
            using var response = await _httpClient.PostAsync(OllamaBatchUrl, new StringContent(json, Encoding.UTF8, "application/json"));
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _batchUnsupported = true;
                return null;
            }
            response.EnsureSuccessStatusCode();

            var result = JsonSerializer.Deserialize<OllamaBatchResponse>(await response.Content.ReadAsStringAsync(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return result?.Embeddings;
        }

        private class OllamaEmbeddingResponse
        {
            public float[] Embedding { get; set; } = Array.Empty<float>();
        }

        private class OllamaBatchResponse
        {
            public List<float[]> Embeddings { get; set; } = new();
        }
    }
}
