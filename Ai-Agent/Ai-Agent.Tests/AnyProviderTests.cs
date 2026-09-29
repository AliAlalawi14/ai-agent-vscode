using Ai_Agent.Config;
using Ai_Agent.LLM;
using Ai_Agent.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace Ai_Agent.Tests
{
    /// <summary>Phase 1b: any number of OpenAI-compatible providers (Gemini, Azure, Ollama...), each with its own URL and auth.</summary>
    public class AnyProviderTests
    {
        /// <summary>Records the request (URL, headers) and answers with a one-token stream.</summary>
        private sealed class RecordingHandler(string reply) : HttpMessageHandler
        {
            public HttpRequestMessage? Last { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Last = request;
                var sse = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{reply}\"}}}}]}}\n\ndata: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
            }
        }

        private static (OpenAICompatibleClient Client, RecordingHandler Handler) Build(CustomProviderOptions provider, string reply = "ok")
        {
            var handler = new RecordingHandler(reply);
            var client = new OpenAICompatibleClient(new HttpClient(handler) { BaseAddress = provider.BaseAddress },
                provider.ToSettings(), NullLogger.Instance);
            return (client, handler);
        }

        private static async Task<string> Ask(ILLMClient client, string? model = null)
        {
            var text = new StringBuilder();
            await foreach (var c in client.StreamMessageAsync(new() { new ChatMessage { Role = "user", Content = "hi" } }, model: model))
                text.Append(c.Content);
            return text.ToString();
        }

        [Theory]
        // Providers whose API isn't at /v1: the request must go to <full base>/chat/completions
        [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
        [InlineData("https://my-res.openai.azure.com/openai/v1/", "https://my-res.openai.azure.com/openai/v1/chat/completions")]
        [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
        public async Task Requests_go_to_the_full_base_url_of_each_provider(string baseUrl, string expected)
        {
            var (client, handler) = Build(new CustomProviderOptions
            {
                Name = "p", BaseUrl = baseUrl, ApiKey = "k", Models = new() { new OpenAIModelOptions { Id = "m" } }
            });

            await Ask(client);

            Assert.Equal(expected, handler.Last!.RequestUri!.ToString());
        }

        [Fact]
        public async Task Each_auth_style_sends_the_right_header()
        {
            var bearer = Build(new CustomProviderOptions { Name = "gemini", BaseUrl = "https://a.test/v1", ApiKey = "key-1", Auth = "bearer", Models = new() { new OpenAIModelOptions { Id = "m" } } });
            var azure = Build(new CustomProviderOptions { Name = "azure", BaseUrl = "https://b.test/openai/v1", ApiKey = "key-2", Auth = "api-key", Models = new() { new OpenAIModelOptions { Id = "m" } } });
            var local = Build(new CustomProviderOptions { Name = "ollama", BaseUrl = "http://localhost:11434/v1", Auth = "none", Models = new() { new OpenAIModelOptions { Id = "m" } } });

            await Ask(bearer.Client);
            await Ask(azure.Client);
            await Ask(local.Client);

            Assert.Equal("Bearer key-1", bearer.Handler.Last!.Headers.Authorization!.ToString());
            Assert.Equal("key-2", azure.Handler.Last!.Headers.GetValues("api-key").Single());
            Assert.Null(azure.Handler.Last!.Headers.Authorization);
            Assert.Null(local.Handler.Last!.Headers.Authorization);
            Assert.False(local.Handler.Last!.Headers.Contains("api-key"));
        }

        [Fact]
        public async Task Several_providers_route_by_model()
        {
            var registry = new LLMProviderRegistry(NullLogger<LLMProviderRegistry>.Instance);
            registry.RegisterProvider(Build(new CustomProviderOptions { Name = "gemini", BaseUrl = "https://g.test/v1beta/openai", ApiKey = "k", Models = new() { new OpenAIModelOptions { Id = "gemini-x" } } }, "from-gemini").Client);
            registry.RegisterProvider(Build(new CustomProviderOptions { Name = "mistral", BaseUrl = "https://m.test/v1", ApiKey = "k", Models = new() { new OpenAIModelOptions { Id = "mistral-y" } } }, "from-mistral").Client);
            registry.RegisterProvider(Build(new CustomProviderOptions { Name = "ollama", BaseUrl = "http://localhost:11434/v1", Auth = "none", Models = new() { new OpenAIModelOptions { Id = "qwen3:14b" } } }, "from-ollama").Client);
            var router = new RoutingLLMClient(registry, "ollama");

            Assert.Equal("from-mistral", await Ask(router, "mistral-y"));
            Assert.Equal("from-gemini", await Ask(router, "gemini-x"));
            Assert.Equal("from-ollama", await Ask(router));                    // default provider
            Assert.Equal(3, (await router.GetAvailableModelsAsync()).Select(m => m.Provider).Distinct().Count());
        }

        [Fact]
        public void A_provider_needs_a_name_a_valid_url_and_a_model()
        {
            var model = new List<OpenAIModelOptions> { new() { Id = "m" } };
            Assert.True(new CustomProviderOptions { Name = "x", BaseUrl = "https://x.test/v1", Models = model }.IsConfigured);
            Assert.False(new CustomProviderOptions { Name = "", BaseUrl = "https://x.test/v1", Models = model }.IsConfigured);
            Assert.False(new CustomProviderOptions { Name = "x", BaseUrl = "not a url", Models = model }.IsConfigured);
            Assert.False(new CustomProviderOptions { Name = "x", BaseUrl = "https://x.test/v1" }.IsConfigured);
        }
    }
}
