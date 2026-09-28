using Ai_Agent.Agent.Services;
using Ai_Agent.Config;
using Ai_Agent.Data;
using Ai_Agent.LLM;
using Ai_Agent.Tools;
using Ai_Agent.Tools.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();

builder.Services.Configure<LLMOptions>(
    builder.Configuration.GetSection(LLMOptions.SectionName));
builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<AnthropicOptions>(
    builder.Configuration.GetSection(AnthropicOptions.SectionName));
builder.Services.Configure<OpenAICompatibleOptions>(
    builder.Configuration.GetSection(OpenAICompatibleOptions.SectionName));

// ── LLM PROVIDERS: each one only when configured; the agent talks to the router ──
var deepSeekConfigured = !string.IsNullOrWhiteSpace(builder.Configuration[$"{LLMOptions.SectionName}:ApiKey"]);
var openAIConfigured = builder.Configuration.GetSection(OpenAICompatibleOptions.SectionName).Get<OpenAICompatibleOptions>()?.IsConfigured == true;
var claudeConfigured = (builder.Configuration.GetSection(AnthropicOptions.SectionName).Get<AnthropicOptions>() ?? new()).IsConfigured;

builder.Services.AddHttpClient<DeepSeekClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<LLMOptions>>();
    client.BaseAddress = new Uri(options.Value.BaseUrl);
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.Value.ApiKey}");
    client.Timeout = TimeSpan.FromSeconds(120);
})
.AddHttpMessageHandler<TransientRetryHandler>();   // 429/5xx/network errors: retry with backoff

builder.Services.AddHttpClient<OpenAICompatibleClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<OpenAICompatibleOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    if (!string.IsNullOrWhiteSpace(options.ApiKey))   // a local Ollama needs none
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiKey}");
    client.Timeout = TimeSpan.FromMinutes(5);          // local models can be slow
})
.AddHttpMessageHandler<TransientRetryHandler>();

builder.Services.AddSingleton<ClaudeClient>();

builder.Services.AddSingleton(sp =>
{
    var registry = new LLMProviderRegistry(sp.GetRequiredService<ILogger<LLMProviderRegistry>>());
    if (deepSeekConfigured) registry.RegisterProvider(sp.GetRequiredService<DeepSeekClient>());
    if (claudeConfigured) registry.RegisterProvider(sp.GetRequiredService<ClaudeClient>());
    if (openAIConfigured) registry.RegisterProvider(sp.GetRequiredService<OpenAICompatibleClient>());
    return registry;
});
// LLM:DefaultProvider = deepseek | anthropic | <OpenAI:ProviderName>; the first configured one otherwise
builder.Services.AddSingleton<ILLMClient>(sp =>
    new RoutingLLMClient(sp.GetRequiredService<LLMProviderRegistry>(), builder.Configuration["LLM:DefaultProvider"]));

// ── ChromaDB Client (via IHttpClientFactory + retry) ──────────────────────
builder.Services.AddHttpClient<ChromaDbService>(client =>
{
    client.BaseAddress = new Uri("http://localhost:8000");
    client.Timeout = TimeSpan.FromSeconds(60);
})
.AddHttpMessageHandler<TransientRetryHandler>();

// ── INFRASTRUCTURE SERVICES ─────────────────────────────────────────────
builder.Services.AddTransient<TransientRetryHandler>();
builder.Services.AddSingleton(_ => new OllamaEmbeddingService());
builder.Services.AddSingleton<UnifiedDiffService>();
builder.Services.AddSingleton<ChangeTracker>();

// ── TOOL FACTORY ───────────────────────────────────────────────────────
builder.Services.AddSingleton<ToolFactory>();

// ── DATABASE ────────────────────────────────────────────────────────────
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSingleton<ConversationMemoryService>();

// ── AGENT SERVICES ─────────────────────────────────────────────────────
builder.Services.AddSingleton<ProjectContextService>();
builder.Services.AddSingleton<TokenCounter>();
builder.Services.AddSingleton<ContextPruner>();
builder.Services.AddSingleton<ProjectIndexer>();
builder.Services.AddSingleton<CodeVectorIndexer>();

builder.Services.AddSingleton<PromptBuilder>(sp =>
{
    var config = sp.GetRequiredService<IOptions<AgentOptions>>();
    var projectContextService = sp.GetRequiredService<ProjectContextService>();
    var memoryService = sp.GetRequiredService<ConversationMemoryService>();
    return new PromptBuilder(config.Value.WorkspaceRoot, projectContextService, memoryService, config.Value.MemoryEnabled);
});

builder.Services.AddSingleton<ApprovalBroker>();
builder.Services.AddSingleton<PlanStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<AgentService>();

// ── SECURITY ────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ValidationService>();
builder.Services.AddSingleton<PathSecurityService>();

// ── OBSERVABILITY ──────────────────────────────────────────────────────
builder.Services.AddSingleton<AgentMetrics>();
builder.Services.AddSingleton<CostTracker>();

// ── CONTROLLERS ────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
// No CORS policy on purpose: the VS Code extension calls from the extension host (Node),
// which is not subject to CORS, and browsers must not be able to reach this API.

// ── RATE LIMITS: 120 API calls/minute overall ──
// (Concurrent LLM calls are capped in AgentService, so a run waiting for approval holds no slot.)
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Global limiter: applies to every request in addition to any endpoint policy
    limiter.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("all", _ =>
            new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

// ── CONFIG VALIDATION: fail fast on what can't work, warn on what is risky ──
{
    var llm = app.Services.GetRequiredService<IOptions<LLMOptions>>().Value;
    var agentConfig = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
    if (!deepSeekConfigured && !claudeConfigured && !openAIConfigured)
        throw new InvalidOperationException(
            "No LLM provider is configured. Set one of: dotnet user-secrets set \"DeepSeek:ApiKey\" <key> | " +
            "\"Anthropic:ApiKey\" <key> (or ANTHROPIC_API_KEY) | OpenAI:BaseUrl + OpenAI:Models (OpenAI, OpenRouter, Groq, Ollama).");
    if (deepSeekConfigured && !Uri.TryCreate(llm.BaseUrl, UriKind.Absolute, out _))
        throw new InvalidOperationException($"DeepSeek:BaseUrl '{llm.BaseUrl}' is not a valid URL.");
    var router = app.Services.GetRequiredService<ILLMClient>();
    startupLog.LogInformation("LLM providers: {Providers}; default model {Model}",
        string.Join(", ", app.Services.GetRequiredService<LLMProviderRegistry>().GetProviderNames()), router.DefaultModel);
    if (!Directory.Exists(agentConfig.WorkspaceRoot))
        startupLog.LogWarning("Agent:WorkspaceRoot '{Root}' does not exist", agentConfig.WorkspaceRoot);
    foreach (var allowed in agentConfig.AllowedWorkspaces.Where(w => !Directory.Exists(w)))
        startupLog.LogWarning("Agent:AllowedWorkspaces entry '{Workspace}' does not exist", allowed);

    // Secrets belong in user-secrets / environment variables, not in files that get copied and committed
    foreach (var provider in ((IConfigurationRoot)builder.Configuration).Providers
                 .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider>())
    {
        foreach (var secret in new[] { "Anthropic:ApiKey", "OpenAI:ApiKey" })
            if (provider.TryGet(secret, out var value) && !string.IsNullOrWhiteSpace(value))
                startupLog.LogWarning("SECURITY: {Key} is stored in {File}; move it to user-secrets.", secret, provider.Source.Path);
        if (provider.TryGet("DeepSeek:ApiKey", out var key) && !string.IsNullOrWhiteSpace(key))
            startupLog.LogWarning("SECURITY: DeepSeek:ApiKey is stored in {File}. Rotate the key and move it to user-secrets " +
                                  "(dotnet user-secrets set \"DeepSeek:ApiKey\" <new key>), then delete it from the file.", provider.Source.Path);
        if (provider.TryGet("ConnectionStrings:DefaultConnection", out var cs) &&
            cs?.Contains("Password=", StringComparison.OrdinalIgnoreCase) == true)
            startupLog.LogWarning("SECURITY: the database password is stored in {File}; move the connection string to user-secrets.", provider.Source.Path);
    }
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRateLimiter();

// ── AUTH: every request must carry the shared token ─────────────────────
var apiToken = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value.ApiToken;
if (string.IsNullOrWhiteSpace(apiToken))
    throw new InvalidOperationException(
        "Agent:ApiToken is not configured. Set it with: dotnet user-secrets set \"Agent:ApiToken\" <token>");
var apiTokenBytes = System.Text.Encoding.UTF8.GetBytes(apiToken);

app.Use(async (ctx, next) =>
{
    var provided = System.Text.Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Agent-Token"].ToString());
    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(provided, apiTokenBytes))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

// Migrations run at startup in development; set Database:MigrateOnStartup=false where they run as a deploy step
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // MigrateAsync (not EnsureCreated): EnsureCreated skips everything when the database
    // already exists, so tables from Migrations/ were never created.
    await context.Database.MigrateAsync();
}

app.MapControllers();

// Index code for semantic_search in the background: the API is usable immediately, and without
// Chroma/Ollama the tool is simply not offered (see CodeVectorIndexer.IsAvailable)
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    var root = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value.WorkspaceRoot;
    try { await app.Services.GetRequiredService<CodeVectorIndexer>().IndexAsync(root); }
    catch (Exception ex) { startupLog.LogWarning("Background indexing failed: {Message}", ex.Message); }
}));

await app.RunAsync();
