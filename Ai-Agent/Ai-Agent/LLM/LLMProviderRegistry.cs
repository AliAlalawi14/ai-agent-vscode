namespace Ai_Agent.LLM
{
    /// <summary>
    /// Registry for managing multiple LLM providers.
    /// Enables dynamic provider selection and failover.
    /// </summary>
    public class LLMProviderRegistry
    {
        private readonly Dictionary<string, ILLMClient> _providers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<LLMProviderRegistry> _logger;

        public LLMProviderRegistry(ILogger<LLMProviderRegistry> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Register a provider with the registry.
        /// </summary>
        public void RegisterProvider(ILLMClient provider)
        {
            _providers[provider.ProviderName] = provider;
            _logger.LogInformation("Registered LLM provider: {Provider}", provider.ProviderName);
        }

        /// <summary>
        /// Get a provider by name.
        /// </summary>
        public ILLMClient? GetProvider(string name)
        {
            _providers.TryGetValue(name, out var provider);
            return provider;
        }

        /// <summary>
        /// Get the default provider (first registered).
        /// </summary>
        public ILLMClient? GetDefaultProvider()
        {
            return _providers.Values.FirstOrDefault();
        }

        /// <summary>
        /// Get all registered providers.
        /// </summary>
        public IEnumerable<ILLMClient> GetAllProviders()
        {
            return _providers.Values;
        }

        /// <summary>
        /// Check if a provider is registered.
        /// </summary>
        public bool HasProvider(string name)
        {
            return _providers.ContainsKey(name);
        }

        /// <summary>
        /// Get available models from all providers.
        /// </summary>
        public async Task<List<ModelInfo>> GetAllModelsAsync(CancellationToken cancellationToken = default)
        {
            var allModels = new List<ModelInfo>();

            foreach (var provider in _providers.Values)
            {
                try
                {
                    var models = await provider.GetAvailableModelsAsync(cancellationToken);
                    allModels.AddRange(models);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get models from {Provider}", provider.ProviderName);
                }
            }

            return allModels;
        }

        /// <summary>
        /// Get provider names.
        /// </summary>
        public IEnumerable<string> GetProviderNames()
        {
            return _providers.Keys;
        }
    }
}
