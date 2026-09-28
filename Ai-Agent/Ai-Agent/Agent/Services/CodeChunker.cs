namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Splits a source file into chunks for embedding. Methods/functions become their own chunks; every other
    /// non-blank line (usings, class headers, fields, properties, enums, records, interfaces, attributes)
    /// lands in a "declarations" chunk, so every line of the file is searchable. Chunks longer than
    /// <see cref="MaxChunkLines"/> are split with a small overlap.
    /// </summary>
    public static class CodeChunker
    {
        public const int MaxChunkLines = 80;
        private const int Overlap = 5;
        private const int SimpleChunkLines = 30;

        public static List<CodeChunk> Chunk(string filePath, string[] lines)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var methods = ext switch
            {
                ".cs" or ".js" or ".jsx" or ".ts" or ".tsx" => BraceBlocks(lines),
                ".py" => PythonBlocks(lines),
                _ => new List<(int Start, int End)>()
            };

            var chunks = new List<CodeChunk>();
            if (methods.Count == 0 && ext is not (".cs" or ".js" or ".jsx" or ".ts" or ".tsx" or ".py"))
            {
                // Unknown structure: fixed windows
                for (var i = 0; i < lines.Length; i += SimpleChunkLines)
                    AddChunk(chunks, filePath, lines, i, Math.Min(i + SimpleChunkLines, lines.Length) - 1, "code");
                return chunks;
            }

            var covered = new bool[lines.Length];
            foreach (var (start, end) in methods)
            {
                AddChunk(chunks, filePath, lines, start, end, "method");
                for (var i = start; i <= end; i++) covered[i] = true;
            }

            // Everything outside a method: contiguous runs of uncovered lines become declaration chunks
            var runStart = -1;
            for (var i = 0; i <= lines.Length; i++)
            {
                var open = i < lines.Length && !covered[i];
                if (open && runStart < 0) runStart = i;
                if (!open && runStart >= 0)
                {
                    if (Enumerable.Range(runStart, i - runStart).Any(j => !string.IsNullOrWhiteSpace(lines[j])))
                        AddChunk(chunks, filePath, lines, runStart, i - 1, "declarations");
                    runStart = -1;
                }
            }

            return chunks.OrderBy(c => c.StartLine).ToList();
        }

        /// <summary>Adds lines[start..end] (0-based, inclusive), split into overlapping pieces if too long.</summary>
        private static void AddChunk(List<CodeChunk> chunks, string filePath, string[] lines, int start, int end, string kind)
        {
            for (var from = start; from <= end; from += MaxChunkLines - Overlap)
            {
                var to = Math.Min(from + MaxChunkLines - 1, end);
                chunks.Add(new CodeChunk
                {
                    FilePath = filePath,
                    StartLine = from + 1,
                    EndLine = to + 1,
                    Kind = kind,
                    Content = string.Join("\n", lines[from..(to + 1)])
                });
                if (to == end) break;
            }
        }

        private static readonly string[] MethodSignals =
            { "void ", "int ", "string ", "bool ", "public ", "private ", "protected ", "internal ", "function ", "async " };

        /// <summary>Method/function bodies in C-style code: a signature line followed by a balanced { } block.</summary>
        private static List<(int Start, int End)> BraceBlocks(string[] lines)
        {
            var blocks = new List<(int, int)>();
            var i = 0;
            while (i < lines.Length)
            {
                var trimmed = lines[i].Trim();
                var looksLikeSignature = trimmed.Contains('(') && trimmed.Contains(')') &&
                                         MethodSignals.Any(trimmed.Contains) &&
                                         !trimmed.StartsWith("//") && !trimmed.Contains(" class ") && !trimmed.StartsWith("class ");
                if (!looksLikeSignature) { i++; continue; }

                // Find the body: the first '{' within the next few lines, then its matching '}'
                var depth = 0;
                var opened = false;
                var end = -1;
                for (var j = i; j < lines.Length; j++)
                {
                    foreach (var c in lines[j])
                    {
                        if (c == '{') { depth++; opened = true; }
                        else if (c == '}') depth--;
                    }
                    if (!opened && (lines[j].TrimEnd().EndsWith(';') || j - i > 3)) break;   // expression-bodied / abstract: no block
                    if (opened && depth <= 0) { end = j; break; }
                }

                if (end < 0)
                {
                    // One-line member (e.g. "int A() => 1;"): leave it to the declarations chunk
                    i++;
                    continue;
                }

                blocks.Add((i, end));
                i = end + 1;
            }
            return blocks;
        }

        /// <summary>def/class blocks in Python: the header plus every following line indented deeper.</summary>
        private static List<(int Start, int End)> PythonBlocks(string[] lines)
        {
            var blocks = new List<(int, int)>();
            var i = 0;
            while (i < lines.Length)
            {
                var trimmed = lines[i].TrimStart();
                if (!(trimmed.StartsWith("def ") || trimmed.StartsWith("async def ")) || !trimmed.TrimEnd().EndsWith(':'))
                {
                    i++;
                    continue;
                }

                var indent = lines[i].Length - trimmed.Length;
                var end = i;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (string.IsNullOrWhiteSpace(lines[j])) continue;
                    if (lines[j].Length - lines[j].TrimStart().Length <= indent) break;
                    end = j;
                }
                blocks.Add((i, end));
                i = end + 1;
            }
            return blocks;
        }
    }
}
