using Ai_Agent.Agent.Services;
using Ai_Agent.Config;
using Ai_Agent.LLM;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ai_Agent.Tests
{
    /// <summary>Phase 3: chunks cover whole files; indexing is incremental and per workspace.</summary>
    public class Phase3Tests
    {
        // ── Chunk line coverage (the Phase 3 KPI): share of non-blank lines that land in some chunk ──

        private static double Coverage(string relativePath, string[] lines)
        {
            var chunks = CodeChunker.Chunk(relativePath, lines);
            var covered = new bool[lines.Length];
            foreach (var c in chunks)
                for (var i = c.StartLine - 1; i < c.EndLine; i++) covered[i] = true;
            var nonBlank = Enumerable.Range(0, lines.Length).Where(i => !string.IsNullOrWhiteSpace(lines[i])).ToList();
            return nonBlank.Count == 0 ? 1 : (double)nonBlank.Count(i => covered[i]) / nonBlank.Count;
        }

        /// <summary>The repo root, found from this source file (works wherever the tests were built to).</summary>
        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

        [Fact]
        public void Chunks_cover_at_least_95_percent_of_every_source_line_in_the_repo()
        {
            var root = RepoRoot();
            var files = new[] { "evals", "Ai-Agent" }
                .Select(d => Path.Combine(root, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
                .ToList();
            Assert.NotEmpty(files);

            var worst = files
                .Select(f => (File: Path.GetRelativePath(root, f), Coverage: Coverage(f, File.ReadAllLines(f))))
                .OrderBy(x => x.Coverage)
                .First();

            Assert.True(worst.Coverage >= 0.95, $"{worst.File} coverage {worst.Coverage:P1}");
        }

        [Fact]
        public void Fields_properties_and_records_are_searchable_not_only_methods()
        {
            var lines = new[]
            {
                "namespace Shop",
                "{",
                "    public record Money(decimal Amount, string Currency);",
                "    public enum Status { Open, Closed }",
                "    public class Order",
                "    {",
                "        public int MaxItems { get; set; } = 50;",
                "        private readonly List<string> _items = new();",
                "",
                "        public void Add(string item)",
                "        {",
                "            if (_items.Count >= MaxItems) throw new InvalidOperationException();",
                "            _items.Add(item);",
                "        }",
                "    }",
                "}"
            };

            var chunks = CodeChunker.Chunk("Order.cs", lines);

            var method = Assert.Single(chunks, c => c.Kind == "method");
            Assert.Equal((10, 14), (method.StartLine, method.EndLine));
            var declarations = string.Join("\n", chunks.Where(c => c.Kind == "declarations").Select(c => c.Content));
            Assert.Contains("MaxItems", declarations);
            Assert.Contains("record Money", declarations);
            Assert.Contains("enum Status", declarations);
            Assert.Equal(1.0, Coverage("Order.cs", lines));
        }

        [Fact]
        public void Long_blocks_are_split_into_overlapping_chunks_of_at_most_80_lines()
        {
            var body = Enumerable.Range(0, 200).Select(i => $"        x += {i};");
            var lines = new[] { "public void Big()", "{" }.Concat(body).Append("}").ToArray();

            var chunks = CodeChunker.Chunk("Big.cs", lines);

            Assert.All(chunks, c => Assert.True(c.EndLine - c.StartLine + 1 <= CodeChunker.MaxChunkLines));
            Assert.True(chunks.Count >= 3);
            Assert.Equal(1.0, Coverage("Big.cs", lines));
        }

        [Fact]
        public void Python_functions_and_class_bodies_are_covered()
        {
            var lines = new[]
            {
                "import os",
                "class Repo:",
                "    root = '/tmp'",
                "    def load(self, name):",
                "        return open(os.path.join(self.root, name)).read()",
                "",
                "def main():",
                "    print(Repo().load('x'))",
            };

            var chunks = CodeChunker.Chunk("repo.py", lines);

            Assert.Equal(2, chunks.Count(c => c.Kind == "method"));
            Assert.Equal(1.0, Coverage("repo.py", lines));
        }

        // ── Incremental, per-workspace indexing against an in-memory Chroma + Ollama ──

        private static (CodeVectorIndexer Indexer, FakeVectorBackend Backend) Indexer()
        {
            var backend = new FakeVectorBackend();
            var http = new HttpClient(backend) { BaseAddress = new Uri("http://chroma.test") };
            var indexer = new CodeVectorIndexer(
                new OllamaEmbeddingService(new HttpClient(backend)),
                new ChromaDbService(http),
                Options.Create(new AgentOptions()),
                NullLogger<CodeVectorIndexer>.Instance);
            return (indexer, backend);
        }

        [Fact]
        public async Task Reindexing_an_unchanged_workspace_embeds_nothing()
        {
            using var ws = new TempWorkspace();
            ws.Write("A.cs", "class A\n{\n    public int One()\n    {\n        return 1;\n    }\n}\n");
            ws.Write("B.cs", "class B { }\n");
            var (indexer, backend) = Indexer();

            var first = await indexer.IndexAsync(ws.Root);
            var second = await indexer.IndexAsync(ws.Root);

            Assert.Null(first.Error);
            Assert.Equal(2, first.ChangedFiles);
            Assert.True(first.EmbeddedChunks > 0);
            Assert.Equal(0, second.EmbeddedChunks);
            Assert.Equal(2, second.UnchangedFiles);
            Assert.True(indexer.IsAvailable(ws.Root));
            Assert.Equal(first.EmbeddedChunks, backend.EmbeddedTexts);   // nothing embedded twice
        }

        [Fact]
        public async Task Changed_files_replace_their_chunks_and_deleted_files_disappear()
        {
            using var ws = new TempWorkspace();
            ws.Write("A.cs", "class A\n{\n    public int One()\n    {\n        return 1;\n    }\n}\n");
            ws.Write("B.cs", "class B { }\n");
            var (indexer, backend) = Indexer();
            await indexer.IndexAsync(ws.Root);

            ws.Write("A.cs", "class A\n{\n    public int Two()\n    {\n        return 2;\n    }\n}\n");
            File.Delete(Path.Combine(ws.Root, "B.cs"));
            var stats = await indexer.IndexAsync(ws.Root);

            Assert.Equal(1, stats.ChangedFiles);
            Assert.Equal(1, stats.RemovedFiles);
            Assert.DoesNotContain(backend.Documents, d => d.Contains("One()") || d.Contains("class B"));
            Assert.Contains(backend.Documents, d => d.Contains("Two()"));
        }

        [Fact]
        public async Task Each_workspace_only_sees_its_own_chunks()
        {
            using var a = new TempWorkspace();
            using var b = new TempWorkspace();
            a.Write("Calc.cs", "class Calc { }\n");
            b.Write("Stock.cs", "class Stock { }\n");
            var (indexer, backend) = Indexer();
            await indexer.IndexAsync(a.Root);
            await indexer.IndexAsync(b.Root);

            var chroma = new ChromaDbService(new HttpClient(backend) { BaseAddress = new Uri("http://chroma.test") });
            var hits = await chroma.SearchAsync(new float[] { 1, 0, 0 }, 10, CodeVectorIndexer.WhereWorkspace(b.Root));

            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.Equal("Stock.cs", h.FilePath));
        }

        /// <summary>Just enough of Ollama (/api/embed) and Chroma (get/upsert/delete/query with where) in memory.</summary>
        private sealed class FakeVectorBackend : HttpMessageHandler
        {
            private readonly Dictionary<string, (string Doc, JsonObject Meta)> _rows = new();
            public int EmbeddedTexts { get; private set; }
            public IEnumerable<string> Documents => _rows.Values.Select(r => r.Doc);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();

                if (path.EndsWith("/api/embed"))
                {
                    var inputs = body!["input"]!.AsArray();
                    EmbeddedTexts += inputs.Count;
                    return Json(new { embeddings = inputs.Select(_ => new[] { 1f, 0f, 0f }) });
                }
                if (path.EndsWith("/upsert"))
                {
                    var ids = body!["ids"]!.AsArray();
                    for (var i = 0; i < ids.Count; i++)
                        _rows[(string)ids[i]!] = ((string)body["documents"]![i]!, body["metadatas"]![i]!.DeepClone().AsObject());
                    return Json(new { });
                }
                if (path.EndsWith("/get"))
                    return Json(new { metadatas = Match(body!["where"]).Select(r => r.Meta) });
                if (path.EndsWith("/delete"))
                {
                    foreach (var id in _rows.Where(r => Matches(r.Value.Meta, body!["where"])).Select(r => r.Key).ToList())
                        _rows.Remove(id);
                    return Json(new { });
                }
                if (path.EndsWith("/query"))
                {
                    var rows = Match(body!["where"]).Take((int)body["n_results"]!).ToList();
                    return Json(new
                    {
                        documents = new[] { rows.Select(r => r.Doc) },
                        metadatas = new[] { rows.Select(r => r.Meta) },
                        distances = new[] { rows.Select(_ => 0.0) }
                    });
                }
                return new HttpResponseMessage(HttpStatusCode.OK);   // collection exists
            }

            private IEnumerable<(string Doc, JsonObject Meta)> Match(JsonNode? where) =>
                _rows.Values.Where(r => Matches(r.Meta, where));

            private static bool Matches(JsonObject meta, JsonNode? where)
            {
                if (where is not JsonObject w) return true;
                if (w["$and"] is JsonArray all) return all.All(c => Matches(meta, c));
                return w.All(kv => meta[kv.Key]?.ToJsonString() == kv.Value?.ToJsonString());
            }

            private static HttpResponseMessage Json(object value) =>
                new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        }
    }
}
