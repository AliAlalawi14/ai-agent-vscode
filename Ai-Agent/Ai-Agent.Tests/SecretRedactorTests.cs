using Ai_Agent.Agent.Services;
using Ai_Agent.Tools.Services;

namespace Ai_Agent.Tests
{
    public class SecretRedactorTests
    {
        [Theory]
        // Fake values with the real formats (never put real credentials in tests)
        [InlineData("\"ApiKey\": \"sk-0123456789abcdef0123456789abcdef\"", "sk-01234567")]
        [InlineData("\"DefaultConnection\": \"Host=localhost;Database=ai;Username=postgres;Password=NotARealPw9\"", "NotARealPw9")]
        [InlineData("OPENAI_API_KEY=abc123def456", "abc123def456")]
        [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123", "ghp_abcdefghij")]
        [InlineData("\"client_secret\": \"s3cr3t-value\"", "s3cr3t-value")]
        public void Secrets_are_redacted(string input, string secret)
        {
            var output = SecretRedactor.Redact(input);
            Assert.DoesNotContain(secret, output);
            Assert.Contains(SecretRedactor.Marker, output);
        }

        [Theory]
        [InlineData("var token = GetToken();")]
        [InlineData("    Password = model.Password;")]
        [InlineData("public string ApiKey { get; set; } = string.Empty;")]
        [InlineData("if (secret == null) throw new ArgumentException(\"secret\");")]
        public void Normal_code_is_left_untouched(string code) =>
            Assert.Equal(code, SecretRedactor.Redact(code));

        [Theory]
        [InlineData("certs/server.pfx", true)]
        [InlineData(".ssh/id_rsa", true)]
        [InlineData("keys/private.pem", true)]
        [InlineData("appsettings.json", false)]
        [InlineData("Program.cs", false)]
        public void Key_and_certificate_files_are_recognized(string path, bool secret) =>
            Assert.Equal(secret, SecretRedactor.IsSecretFile(path));

        [Fact]
        public async Task Writing_the_redaction_placeholder_is_refused()
        {
            using var ws = new TempWorkspace();
            ws.Write("appsettings.json", "{ \"ApiKey\": \"real-value\" }");

            var write = await new FileWriterTool(ws.Root).ExecuteAsync(new()
            {
                ["path"] = "appsettings.json",
                ["content"] = $"{{ \"ApiKey\": \"{SecretRedactor.Marker}\", \"Other\": 1 }}"
            });
            var edit = await new EditFileTool(ws.Root).ExecuteAsync(new()
            {
                ["path"] = "appsettings.json",
                ["old_string"] = "\"ApiKey\"",
                ["new_string"] = $"\"ApiKey2\": \"{SecretRedactor.Marker}\", \"ApiKey\""
            });

            Assert.StartsWith("ERROR", write);
            Assert.StartsWith("ERROR", edit);
            Assert.Equal("{ \"ApiKey\": \"real-value\" }", ws.Read("appsettings.json"));
        }
    }
}
