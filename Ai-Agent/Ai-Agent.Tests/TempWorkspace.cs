using System.Text;

namespace Ai_Agent.Tests
{
    /// <summary>A throwaway workspace folder, deleted after the test.</summary>
    public sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ai-agent-tests", Guid.NewGuid().ToString("N"));

        public TempWorkspace()
        {
            Directory.CreateDirectory(Root);
        }

        public string Write(string relativePath, string content, bool bom = false)
        {
            var full = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(bom));
            return full;
        }

        public string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

        public byte[] ReadBytes(string relativePath) => File.ReadAllBytes(Path.Combine(Root, relativePath));

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
