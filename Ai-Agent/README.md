# AI Agent - Intelligent Code Assistant

An ASP.NET Core Web API that provides an AI-powered coding assistant with capabilities for code analysis, file manipulation, semantic search, and autonomous task execution.

## Overview

This project implements a sophisticated AI agent that leverages Large Language Models (LLMs) to assist with software development tasks. It features a modular tool system, vector-based semantic search, conversation memory, and secure file operations within a configurable workspace.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         API Layer                                │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────────────────┐  │
│  │AgentController│ │HealthController│                           │
│  └─────────────┘ └─────────────┘ └─────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────────┐
│                       Agent Services                             │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────────────────┐  │
│  │AgentService │ │PromptBuilder│ │ConversationMemorySvc  │  │
│  └─────────────┘ └─────────────┘ └─────────────────────────┘  │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────────────────┐  │
│  │ProjectIndexer│ │CodeVectorIndexer│ │ProjectContextService│  │
│  └─────────────┘ └─────────────┘ └─────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────────┐
│                         LLM Layer                                │
│  ┌─────────────┐ ┌─────────────────────┐ ┌─────────────────┐  │
│  │ILLMClient   │ │DeepSeekClient       │ │LLMProviderReg.  │  │
│  │(interface)  │ │(implementation)     │ │(extensibility)   │  │
│  └─────────────┘ └─────────────────────┘ └─────────────────┘  │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │OllamaEmbeddingService (vector embeddings)               │  │
│  └─────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────────┐
│                      Tool System                                   │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────────┐  │
│  │read_file │ │write_file│ │replace   │ │directory_browser│  │
│  └──────────┘ └──────────┘ └──────────┘ └──────────────────┘  │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────────┐  │
│  │code_search│ │multi_read│ │terminal  │ │semantic_search  │  │
│  └──────────┘ └──────────┘ └──────────┘ └──────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────────┐
│                    Infrastructure                                  │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────────────────┐  │
│  │PostgreSQL   │ │ChromaDB     │ │File System (Workspace)  │  │
│  │(memory)     │ │(vectors)    │ │                         │  │
│  └─────────────┘ └─────────────┘ └─────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

## Key Components

### Controllers

| Controller | Description | Key Endpoints |
|------------|-------------|---------------|
| `AgentController` | Main agent execution API | `POST /v1/chat/completions` (streaming), approve, revert |
| `HealthController` | System monitoring and metrics | `GET /api/health`, `GET /api/health/detailed` |

### Agent Services

| Service | Purpose |
|---------|---------|
| `AgentService` | Core orchestration of the AI agent loop (think → act → observe) |
| `PromptBuilder` | Constructs system prompts with tool definitions and project context |
| `ProjectIndexer` | Indexes project structure, classes, methods, and properties |
| `CodeVectorIndexer` | Creates vector embeddings for semantic code search |
| `ConversationMemoryService` | Persists conversation history to PostgreSQL |
| `TokenCounter` | Estimates token usage for context management |
| `ContextPruner` | Intelligently reduces context when approaching token limits |
| `PathSecurityService` | Validates file paths to prevent directory traversal attacks |
| `ValidationService` | Validates and sanitizes user requests |
| `AgentMetrics` | Tracks performance metrics and operational statistics |
| `CostTracker` | Monitors API usage costs |

### LLM Integration

| Component | Description |
|-----------|-------------|
| `ILLMClient` | Abstraction interface for LLM providers |
| `DeepSeekClient` | DeepSeek API implementation |
| `LLMProviderRegistry` | Registry for multi-provider support |
| `OllamaEmbeddingService` | Local embedding generation via Ollama |

### Tool System

All tools implement the `ITool` interface:

| Tool | Description |
|------|-------------|
| `FileReaderTool` | Reads files with line numbers for precise referencing |
| `MultiFileReaderTool` | Reads multiple files in a single operation |
| `FileWriterTool` | Writes files with diff preview and backup creation |
| `ReplaceLinesTool` | Replaces specific line ranges with validation |
| `DirectoryBrowserTool` | Lists directory contents with filtering |
| `CodeSearchTool` | Pattern-based code search using grep-like functionality |
| `SemanticSearchTool` | Vector-based semantic code search using ChromaDB |
| `TerminalTool` | Runs one allow-listed command without a shell (`Agent:AllowedCommands`; `.cmd` shims like npm are resolved on Windows) |
| `EditFileTool` | Exact search-and-replace edit (preferred way to change files) |
| `DeleteFileTool` | Deletes one file (approval required; revert recreates it) |
| `MoveFileTool` | Moves/renames one file, never overwrites (recorded as two revertable changes) |
| `FindFilesTool` | Finds files by name/glob |

### Security Features

- **Path Security**: All file operations are restricted to the configured workspace
- **Validation Service**: Sanitizes user input and validates requests
- **Correlation ID Tracking**: Request tracing via `CorrelationIdMiddleware`
- **Backup Creation**: Automatic backups before file modifications

## Configuration

Configuration is managed through `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=ai_agent;Username=postgres;Password=..."
  },
  "DeepSeek": {
    "ApiKey": "your-api-key",
    "BaseUrl": "https://api.deepseek.com",
    "Model": "deepseek-chat"
  },
  "Agent": {
    "MaxIterations": 15,
    "MaxContextTokens": 12000,
    "WorkspaceRoot": "C:\\Path\\To\\Workspace"
  }
}
```

### Agent options added by the enhancement phases

| Setting | Default | Meaning |
|---|---|---|
| `Agent:MaxConcurrentLlmCalls` | 2 | LLM calls in flight across all chats (held only while the model streams, never while waiting for approval) |
| `Agent:AllowedCommands` | dotnet build/test/restore/clean, git status/diff/log/branch | Commands `run_terminal` may run, `"program subcommand"`; config entries are **added** to the defaults |
| `Agent:ChangeLogMaxSessions` | 50 | Sessions kept in each workspace's `.ai_changes.ndjson` (older ones can't be reverted) |
| `Agent:MaxIndexedFiles` | 2000 | Code files per workspace in the semantic index |
| `DeepSeek:Pricing:<model>:Input\|CachedInput\|Output` | built-in list prices | USD per 1K tokens, used for the cost shown per answer |

Add `.ai_changes.ndjson` to your projects' `.gitignore`: it is the agent's per-workspace change log.
Plans live in `.ai/plans/*.plan.md` (commit them or ignore them, your choice).

### LLM providers (configure any; each needs only its own settings)

| Provider | Settings (user-secrets / env) | Models in the picker |
|---|---|---|
| DeepSeek | `DeepSeek:ApiKey`, `DeepSeek:BaseUrl` | deepseek-chat |
| Claude | `Anthropic:ApiKey` (or `ANTHROPIC_API_KEY`); optional `Anthropic:Effort` (default high), `Anthropic:RefusalFallbacks` | claude-opus-5 (default), claude-sonnet-5, claude-haiku-4-5 |
| OpenAI-compatible (OpenAI, OpenRouter, Groq, Ollama) | `OpenAI:BaseUrl` (without /v1), `OpenAI:ApiKey`, `OpenAI:ProviderName`, `OpenAI:Models:0:Id`; OpenAI's newer models: `OpenAI:UseMaxCompletionTokens=true` | whatever you list |

`LLM:DefaultProvider` (deepseek / anthropic / your OpenAI:ProviderName) picks the model used when the chat doesn't choose one.
Claude requests use prompt caching; `claude-opus-5` requests enable server-side refusal fallbacks (to claude-opus-4-8) unless
`Anthropic:RefusalFallbacks=false`. Compare providers on the same tasks with the eval runner's `--model <id>`.

```bash
dotnet user-secrets set "Anthropic:ApiKey" <key>          # Claude
dotnet user-secrets set "OpenAI:BaseUrl" http://localhost:11434   # a local Ollama
dotnet user-secrets set "OpenAI:ProviderName" ollama
dotnet user-secrets set "OpenAI:Models:0:Id" qwen3:14b
```

## Measuring changes (eval gate)

```bash
dotnet test Ai-Agent.Tests
dotnet run --project Ai-Agent.Evals -- --repeat 3 --compare evals/results/baseline.json
```

The runner starts its own backend on a temp copy of `evals/fixture` (or `fixture2`), runs every task N times and
exits with 1 on a regression: a task that always passed in the baseline no longer does, the success rate dropped,
or tokens/cost/median turn time got >10% worse (p90 >25%). Confirm a flagged task with `--only <id> --repeat 10`
on the old and the new build before calling it a regression.

### Configuration Classes

- `LLMOptions` (`Config/LLMOptions.cs`) - LLM provider settings
- `AgentOptions` (`Config/AgentOptions.cs`) - Agent behavior configuration

## Database Schema

### PostgreSQL Tables

**ConversationMemory**
- `Id` (PK)
- `SessionId` (indexed)
- `CreatedAt` (indexed)
- `Title` (max 200 chars)
- `UserRequest` (max 2000 chars)
- `Summary` (max 2000 chars)
- `KeyTerms` (max 500 chars)
- `BuildSucceeded` (boolean)
- `IterationsUsed` (integer)

### ChromaDB Collections

- Code embeddings for semantic search
- Tenant ID: `b8743815-887b-4076-9802-ff1f123307c8`

## API Endpoints

### Agent Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/v1/chat/completions` | Streaming agent execution (SSE) |
| POST | `/api/agent/revert` | Revert a file change from backup |
| GET | `/api/agent/symbols` | Search code symbols for @-mentions |
| GET | `/api/agent/files` | Search files for @-mentions |

### Health Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/health` | Basic health check |
| GET | `/api/health/detailed` | Detailed health with dependency status |
| GET | `/api/health/metrics` | Current metrics snapshot |
| GET | `/api/health/info` | System information |
| POST | `/api/health/metrics/reset` | Reset metrics |

## Agent Execution Flow

```
1. User Request
        ↓
2. Validation & Sanitization
        ↓
3. Build System Prompt (with tools, context, memory)
        ↓
4. Iteration Loop (max 15 iterations):
   ┌─────────────────────────────────────┐
   │  a. Check token usage               │
   │  b. Prune context if > 70%          │
   │  c. Call LLM                        │
   │  d. Parse tool calls                │
   │  e. Execute tools                   │
   │  f. Observe results                 │
   │  g. Add to conversation history     │
   └─────────────────────────────────────┘
        ↓
5. Save Conversation Memory
        ↓
6. Return Response
```

## Technology Stack

- **Framework**: ASP.NET Core 10.0 (.NET 10)
- **Database**: PostgreSQL with Entity Framework Core 10
- **Vector DB**: ChromaDB
- **LLM**: DeepSeek API (extensible to other providers)
- **Embeddings**: Ollama (local)
- **Documentation**: OpenAPI/Swagger

## Project Structure

```
Ai-Agent/
├── Agent/
│   └── Services/           # Core agent services
├── Config/                 # Configuration classes
├── Controllers/            # API controllers
├── Data/                   # DbContext
├── LLM/                    # LLM clients and interfaces
├── Models/                 # Entity models
├── Tools/
│   ├── Interfaces/         # ITool interface
│   └── Services/           # Tool implementations
├── Migration/              # Migration helpers
├── Migrations/             # EF Core migrations
├── Program.cs              # Application entry point
├── appsettings.json        # Configuration
└── Ai-Agent.csproj         # Project file
```

## Dependencies

```xml
<PackageReference Include="ChromaDB.Client" Version="1.0.0" />
<PackageReference Include="EntityFramework" Version="6.5.2" />
<PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.7" />
<PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.8" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.1" />
```

## Getting Started

1. **Prerequisites**
   - .NET 10 SDK
   - PostgreSQL database
   - ChromaDB running on port 8000
   - DeepSeek API key

2. **Database Setup**
   ```bash
   # Ensure PostgreSQL is running and database exists
   # Connection string configured in appsettings.Development.json
   ```

3. **Run the Application**
   ```bash
   dotnet run
   ```

4. **API Documentation**
   - OpenAPI spec available at `/openapi/v1.json`

## Security Considerations

- All file operations are sandboxed to the configured `WorkspaceRoot`
- Path traversal attacks are prevented via `PathSecurityService`
- User input is sanitized before processing
- API keys should be stored securely (user secrets in production)

## Extending the System

### Adding a New Tool

1. Create a class implementing `ITool` in `Tools/Services/`
2. Register in `Program.cs` via `ToolRegistry`
3. The tool is automatically included in system prompts

### Adding a New LLM Provider

1. Implement `ILLMClient` interface
2. Register in `LLMProviderRegistry`
3. Configure options class and add to `Program.cs`

## Monitoring

The system provides comprehensive metrics via `AgentMetrics`:

- Request latency
- Token usage
- Tool execution counts
- LLM API costs
- Success/failure rates

Access via: `GET /api/health/metrics`

---

*Built with .NET 10 and AI capabilities*
