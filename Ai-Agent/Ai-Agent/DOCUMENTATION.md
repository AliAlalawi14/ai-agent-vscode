# Ai-Agent — Complete System Documentation

> A self-hosted, code-aware AI agent system: a .NET 10 backend that indexes a codebase, understands it semantically, and executes an agentic loop with tool use — exposed over HTTP and consumed by a VS Code extension with a React-based chat UI, streaming SSE, inline diff review, and one-click apply/revert.

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Backend Architecture](#2-backend-architecture)
   - 2.1 [Project Structure](#21-project-structure)
   - 2.2 [The Agent Loop](#22-the-agent-loop)
   - 2.3 [Function Calling Protocol](#23-function-calling-protocol)
   - 2.4 [Tool System](#24-tool-system)
   - 2.5 [Semantic Search Pipeline](#25-semantic-search-pipeline)
   - 2.6 [Diff & Change Tracking System](#26-diff--change-tracking-system)
   - 2.7 [Prompt Engineering](#27-prompt-engineering)
   - 2.8 [Context Management](#28-context-management)
   - 2.9 [Persistence Layer](#29-persistence-layer)
3. [Frontend Architecture](#3-frontend-architecture)
   - 3.1 [Extension Core](#31-extension-core)
   - 3.2 [Webview UI](#32-webview-ui)
   - 3.3 [State Management](#33-state-management)
   - 3.4 [Streaming Protocol Integration](#34-streaming-protocol-integration)
4. [Client-Server Protocol](#4-client-server-protocol)
   - 4.1 [SSE Message Types](#41-sse-message-types)
   - 4.2 [Extension↔Webview Messaging](#42-extensionwebview-messaging)
5. [API Reference](#5-api-reference)
6. [Configuration](#6-configuration)
7. [Running the System](#7-running-the-system)
8. [Technology Stack](#8-technology-stack)
9. [Known Limitations & Roadmap](#9-known-limitations--roadmap)

---

## 1. System Overview

Ai-Agent is a two-part system that bridges an LLM provider (DeepSeek) with a developer's IDE:

```
┌──────────────────────────────────────────────────────────────────┐
│                         VS CODE EXTENSION                        │
│  ┌──────────────────────┐    ┌──────────────────────────────┐   │
│  │   Extension Core      │    │   Webview UI (React)          │   │
│  │   (TypeScript/Node)   │◄──►│   Chat • Diffs • Tools        │   │
│  │   MessageBroker       │    │   Mentions • History           │   │
│  │   AgentApiClient      │    └──────────────────────────────┘   │
│  └──────────┬───────────┘                                        │
└─────────────┼────────────────────────────────────────────────────┘
              │ SSE (Server-Sent Events) + REST
              │ http://localhost:5036
┌─────────────▼────────────────────────────────────────────────────┐
│                     .NET BACKEND (ASP.NET Core)                    │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │  AgentService (Agent Loop)                                 │    │
│  │  ┌─────────┐  ┌──────────┐  ┌────────────────────────┐   │    │
│  │  │PromptBld│  │ToolSystem│  │ChangeTracker+UnifiedDiff│   │    │
│  │  └─────────┘  │(8 tools) │  │(patch-based revert)    │   │    │
│  │               └──────────┘  └────────────────────────┘   │    │
│  └──────────────────────────────────────────────────────────┘    │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────────────┐     │
│  │ DeepSeekClient│  │ChromaDbService│ │PostgreSQL(EF Core) │     │
│  │ (LLM API)     │  │ (Vector Store) │ │(ConversationMemory)│     │
│  └──────────────┘  └──────────────┘  └────────────────────┘     │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │ OllamaEmbeddingService (nomic-embed-text → float[] vectors)│    │
│  └──────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────┘
```

### Data Flow (One Agent Run)

```
User types "Add validation to CreateUser endpoint"
  │
  ▼
Webview → Extension → POST /v1/chat/completions (SSE stream)
  │
  ▼
AgentService.RunStreamAsync():
  │
  ├─→ Iteration 1: THINK (DeepSeek)
  │     "I need to read the controller first"
  │     → tool_call: read_file(path="Controllers/UserController.cs")
  │     Stream yields: [TOOL_EVENT] tool_start → [TOOL_EVENT] tool_result
  │
  ├─→ Iteration 2: THINK
  │     "I see the CreateUser method. I'll add validation."
  │     → tool_call: write_file(path=..., content=...)
  │     Stream yields: [CHANGE_EVENT] with unified diff patch → [TOOL_EVENT]
  │
  ├─→ Iteration 3: THINK
  │     "Let me verify it builds."
  │     → tool_call: run_terminal(command="dotnet build")
  │     Stream yields: [TOOL_EVENT] tool_start → [TOOL_EVENT] tool_result
  │
  └─→ Iteration 4: THINK
        "Done. The build succeeded."
        Stream yields: content tokens → {"done":true}
  │
  ▼
Webview renders: chat bubbles, tool activity indicators, diff card (Accept/Revert)
```

---

## 2. Backend Architecture

### 2.1 Project Structure

```
Ai-Agent/
├── Agent/Services/
│   ├── AgentService.cs              # Core agent loop (RunAsync, RunStreamAsync)
│   ├── PromptBuilder.cs             # System prompt assembly (dynamic per session)
│   ├── ChromaDbService.cs           # ChromaDB v2 REST client (upsert, query)
│   ├── UnifiedDiffService.cs        # LCS-based unified diff engine
│   ├── ProjectIndexer.cs            # File walking + chunking (C#, JS, TS, Python)
│   ├── CodeVectorIndexer.cs         # Embedding batching + ChromaDB storage
│   ├── ProjectContextService.cs     # Reads .csproj, folder structure, code map
│   ├── ConversationMemoryService.cs # EF Core persistence for conversation history
│   ├── ContextPruner.cs             # Trims message history at token threshold
│   ├── TokenCounter.cs              # Character-based token estimation
│   ├── AgentMetrics.cs              # Concurrent metrics tracking
│   ├── CostTracker.cs               # Per-model pricing + cost estimation
│   ├── PathSecurityService.cs       # Directory traversal prevention
│   ├── ValidationService.cs         # Input sanitization + injection detection
│   └── CorrelationIdMiddleware.cs   # Distributed tracing via X-Correlation-ID
│
├── Controllers/
│   ├── AgentController.cs           # /api/AgentController endpoints
│   ├── ChatController.cs            # Simple LLM passthrough
│   └── HealthController.cs          # Health, metrics, system info
│
├── Tools/Services/
│   ├── ITool.cs                     # Tool interface
│   ├── ToolRegistry.cs              # Per-workspace tool catalog + JSON schema gen
│   ├── ToolFactory.cs               # Creates workspace-scoped registries
│   ├── FileReaderTool.cs            # read_file — line-numbered output
│   ├── MultiFileReaderTool.cs       # read_files — batch reads
│   ├── FileWriterTool.cs            # write_file — full file writes + .backup
│   ├── ReplaceLinesTool.cs          # replace_lines — targeted edits with validation
│   ├── CodeSearchTool.cs            # search_code — grep-style text search
│   ├── SemanticSearchTool.cs        # semantic_search — ChromaDB vector search
│   ├── DirectoryBrowserTool.cs      # list_directory — workspace file listing
│   ├── TerminalTool.cs              # run_terminal — allowlisted command execution
│   ├── DiffReviewService.cs         # Legacy change log (being replaced by ChangeTracker)
│   └── ChangeTracker.cs             # Production change ledger (NDJSON, session-aware)
│
├── LLM/
│   ├── ILLMClient.cs                # LLM provider interface
│   ├── DeepSeekClient.cs            # Full implementation (streaming + function calling)
│   ├── OllamaEmbeddingService.cs    # Local embedding via Ollama (nomic-embed-text)
│   └── LLMProviderRegistry.cs       # Multi-provider registry (future use)
│
├── Config/
│   ├── AgentOptions.cs              # MaxIterations, MaxContextTokens, WorkspaceRoot
│   └── LLMOptions.cs                # DeepSeek API key, base URL, model
│
├── Models/                          # DTOs, request/response types
├── Data/AppDbContext.cs             # EF Core DbContext (ConversationMemory)
├── Migrations/                      # EF Core migration files
└── Program.cs                       # DI registration, startup
```

### 2.2 The Agent Loop

The agent follows a **Think → Decide → Act → Observe** cycle, implemented in `AgentService`:

#### Non-Streaming: `RunAsync(string userRequest, string? workspace)`

```csharp
// Returns AgentResult { Success, Response, Iterations, FileChanges }
while (iteration < maxIterations)
{
    // 1. TOKEN MANAGEMENT: prune if >70% of MaxContextTokens
    if (usagePercent > 70)
        messages = _contextPruner.Prune(messages);
        messages[0] = trimmedSystemPrompt;

    // 2. THINK: Call LLM with function calling
    response = await _llmClient.SendMessageAsync(messages, toolDefinitions);

    // 3. DECIDE: Check for tool calls
    if (response.ToolCalls != null && response.ToolCalls.Count > 0)
    {
        // 4. ACT: Execute each tool, capture before/after, record via ChangeTracker
        foreach (var toolCall in response.ToolCalls)
        {
            toolResult = await toolRegistry.ExecuteToolAsync(name, params);
            // If write_file/replace_lines: capture diff, add to fileChanges
        }

        // 5. OBSERVE: Append tool results + continuation prompt
        messages.Add(assistantMessage);
        messages.Add(toolResultMessages);
        messages.Add(continuationPrompt);
    }
    else
    {
        // No tool calls → final answer
        return new AgentResult { Success = true, Response = content, FileChanges };
    }
}
```

#### Streaming: `RunStreamAsync(string userRequest, string? workspace)` → `IAsyncEnumerable<string>`

Same loop but yields incremental output:

| Yield | Content |
|-------|---------|
| Raw text chunks | LLM reasoning tokens, batched every 50ms |
| `[TOOL_EVENT]{json}[/TOOL_EVENT]` | Tool start (`type: tool_start`) and result (`type: tool_result`) |
| `[CHANGE_EVENT]{json}[/CHANGE_EVENT]` | File change with unified diff patch |
| Session start event | `{ type: session_start, sessionId }` — emitted once at stream start |

### 2.3 Function Calling Protocol

The system uses **OpenAI-compatible JSON function calling** (not the old text-based `TOOL_REQUEST` protocol). Tool definitions are built dynamically by `ToolRegistry.GetToolDefinitions()`:

```json
{
  "type": "function",
  "function": {
    "name": "read_file",
    "description": "Reads the content of a file...",
    "parameters": {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Relative path to the file..." }
      },
      "required": ["path"]
    }
  }
}
```

DeepSeek returns tool calls in standard format, which the agent processes and feeds back into the conversation as `role: "tool"` messages.

### 2.4 Tool System

| Tool | Description | Key Features |
|------|-------------|-------------|
| `read_file` | Reads a single file | Line-numbered output (`"     1: using System;"`) |
| `read_files` | Batch read multiple files | Comma-separated paths, one call |
| `write_file` | Full file write/overwrite | Auto-creates `.backup`, validates content not empty |
| `replace_lines` | Targeted line replacement | Validates `expectedContent` before writing, single-line constraint enforced by prompt |
| `search_code` | Text search (grep) | Case-insensitive, returns file:line matches |
| `semantic_search` | Vector similarity search | Converts query to embedding, queries ChromaDB, returns top-5 matches with scores |
| `list_directory` | Directory listing | Lists files + subdirectories at path |
| `run_terminal` | Shell command execution | **Allowlisted commands only** (`dotnet build`, `git status`, etc.), blocks shell operators (`&&`, `|`, `` ` ``), 30s timeout, 5KB output limit |

**Adding a tool:** Implement `ITool`, register in `ToolFactory.CreateRegistry()`. The system prompt auto-discovers it.

### 2.5 Semantic Search Pipeline

```
User query: "authentication middleware"
        │
        ▼
OllamaEmbeddingService.GetEmbeddingAsync(query)
        │  POST http://localhost:11434/api/embeddings
        │  Model: nomic-embed-text
        ▼
float[] queryEmbedding
        │
        ▼
ChromaDbService.SearchAsync(queryEmbedding, nResults: 5)
        │  POST /api/v2/.../collections/{id}/query
        ▼
List<CodeSearchResult> { Content, FilePath, StartLine, EndLine, Score }
```

**Indexing pipeline** (runs at startup + on-demand via `POST /api/AgentController/index`):

```
CodeVectorIndexer.IndexAsync(maxFiles?: number)
  │
  ├─► Walks workspace for .cs, .js, .ts, .py files
  ├─► Splits into method/function-level chunks
  │     • C-style → brace-aware chunking
  │     • Python → indentation-aware chunking
  │     • Fallback → fixed-size (30 lines)
  ├─► Generates deterministic chunk IDs: "path/to/file.cs_L42-L67"
  ├─► Sends embeddings to Ollama in batches
  └─► Upserts into ChromaDB (idempotent, safe for re-indexing)
```

### 2.6 Diff & Change Tracking System

This is a custom-built, production-grade diff pipeline that replaces the old full-file-content approach.

#### UnifiedDiffService

A complete LCS-based unified diff engine producing standard Git-compatible patches:

```
Input:  before = "line1\nline2\nline3\n"
        after  = "line1\nline2_modified\nline3\nline4\n"

Output: --- a/file.cs
        +++ b/file.cs
        @@ -1,3 +1,4 @@
         line1
        -line2
        +line2_modified
         line3
        +line4
```

Supports:
- `ComputeDiff(filePath, before, after)` — produces unified diff
- `ApplyPatch(original, patch, reverse)` — applies or reverts a patch

#### ChangeTracker

Persistent change ledger with session grouping:

| Feature | Detail |
|---------|--------|
| **Storage** | NDJSON (newline-delimited JSON) — append-only, crash-safe |
| **Session grouping** | Each agent run gets a `sessionId`; changes are numbered sequentially |
| **Change IDs** | Every change has a unique `ChangeId` for individual revert |
| **Patch-based revert** | Revert applies the reverse patch — handles concurrent file modifications gracefully |
| **Batch revert** | `RevertSessionAsync(sessionId)` reverts all changes from a session in reverse order |
| **Thread safety** | `SemaphoreSlim`-guarded in-memory cache with double-check locking |
| **Querying** | By session, by file, recent N |

#### Change Event Format (Streaming)

```json
{
  "changeId": "0199a2b7-e8c3-...",
  "sessionId": "0199a2b6-d1f2-...",
  "filePath": "Controllers/AgentController.cs",
  "toolUsed": "write_file",
  "patch": "--- a/Controllers/AgentController.cs\n+++ b/...\n@@ -42,3 +42,5 @@\n...",
  "isNewFile": false,
  "summary": "Modified AgentController.cs (15 line diff)",
  "patchSize": 342
}
```

**Why it's better than the old approach:** Instead of sending the full 500-line file content in before/after fields, the system now sends a compact 15-line unified diff. This reduces payload size by **80-95%**, uses a standard format any diff viewer can render, and supports proper patch-based revert.

### 2.7 Prompt Engineering

`PromptBuilder.BuildSystemPromptAsync(toolRegistry, workspaceRoot)` assembles a dynamic system prompt each session:

| Section | Purpose |
|---------|---------|
| **Recent Memory** | Last 5 conversation summaries from `ConversationMemoryService` |
| **Path Rules** | Workspace root, relative vs absolute path policy |
| **@-Mentions** | How `@file.cs` and `@ClassName.Method` are resolved |
| **FILE CHANGE REPORTING** | Explains automatic `CHANGE_EVENT` emission — agent should not duplicate |
| **Project Context** | Project type, framework, folder structure, NuGet packages, code map (up to 20 classes with methods/properties), class dependency graph |
| **Tool Selection Guide** | When to use semantic_search vs search_code, single vs multi-file reads |
| **HOW TO USE TOOLS** | Function calling format explanation |
| **replace_lines guidance** | Line number usage, single-line constraint, expectedContent validation |
| **write_file IMPORTANT** | Must provide complete file content — no abbreviations |
| **AVAILABLE TOOLS** | Auto-generated from `ToolRegistry.GetAllTools()` |
| **RULES** | Before/during/after change rules + error recovery |

A trimmed version (`BuildTrimmedSystemPrompt`) is used when the context window approaches capacity — it preserves the essential rules but omits full tool descriptions and project context.

### 2.8 Context Management

**TokenCounter:** Character-based estimation (1 token ≈ 3.5 characters for mixed code/text).

**ContextPruner:** When token usage exceeds 70% of `MaxContextTokens`:
- Keeps: system prompt + original user task
- Keeps: last `KeepLastInteractions * 2` messages (detected by `role == "tool"` messages)
- Inserts: summary message for omitted interactions

### 2.9 Persistence Layer

**PostgreSQL (EF Core):**
- `ConversationMemory` table: stores conversation summaries, titles, key terms, files modified, build success
- Indexed by `SessionId` and `CreatedAt`
- `ConversationMemoryService` provides: save, get recent (top 5 used in prompt), search by keyword

**ChromaDB (Vector Store):**
- Stores code chunk embeddings with deterministic IDs
- Collection auto-created on first use via `InitializeAsync()`
- Polly retry policy (3 retries, exponential backoff)

---

## 3. Frontend Architecture

### 3.1 Extension Core

The VS Code extension runs in the **Extension Host** (Node.js process) and consists of:

| File | Responsibility |
|------|---------------|
| `src/extension.ts` | Entry point. Activates on VS Code start. Registers: sidebar webview provider, 3 commands, 2 keybindings, 1 context menu entry |
| `src/ChatViewProvider.ts` | Implements `WebviewViewProvider`. Generates HTML for the webview (supports dev mode with Vite HMR at `localhost:5173` and production builds from `webview-ui/build/`). Initializes `MessageBroker` |
| `src/services/MessageBroker.ts` | **Central hub** for all extension↔webview communication. Handles: SSE streaming, tool execution display, file change application, diff view opening, revert, symbol/file search, auto-file tracking, conversation persistence |
| `src/services/AgentApiClient.ts` | HTTP client for the backend. `runStream()` is an async generator that parses SSE line-by-line, yielding typed events (`content`, `toolStart`, `toolResult`, `change`, `done`, `error`) |
| `src/shared/protocol.ts` | TypeScript type definitions shared between extension core and webview — message types, session state, file changes, tool events, mention contexts |

**Key commands registered:**
- `aiChat.openSidebar` (`Ctrl+Shift+L`) — opens the AI chat sidebar
- `aiChat.newChat` — clears the current conversation
- `aiChat.askAboutSelection` (`Ctrl+Shift+A`) — sends selected code to the AI with file/language context; appears in right-click context menu

**Auto-context injection:** The extension tracks open editors and recently accessed files. When no explicit @-mentions are provided, it automatically injects the active file + up to 3 recent files as context for the LLM.

### 3.2 Webview UI

The webview is a **React 18** application built with **Vite** and **Tailwind CSS 4**.

#### Component Tree

```
App
└── ChatContainer
    ├── ChatHeader
    │   ├── History toggle button
    │   ├── Health status indicator
    │   └── Settings button
    ├── ConversationHistory (slide-out panel)
    ├── MessageList (virtualized with @tanstack/react-virtual)
    │   └── MessageBubble (per message)
    │       ├── MarkdownRenderer (react-markdown + remark-gfm)
    │       │   ├── CodeBlock (react-syntax-highlighter)
    │       │   ├── InlineToolIndicator
    │       │   └── Inline change indicators
    │       ├── ToolExecutionCard (per tool)
    │       │   ├── Tool name + status spinner
    │       │   └── Result (truncated, expandable)
    │       ├── FileChangeCard (per file change)
    │       │   ├── Summary line
    │       │   ├── FileDiffViewer (react-diff-viewer-continued)
    │       │   └── Action buttons: Accept | Revert | Open Diff
    │       ├── TokenCostBadge
    │       └── MessageActions (copy, retry)
    ├── ActiveToolsPanel (floating panel)
    ├── MessageInput
    │   └── MentionDropdown (@file, @symbol)
    └── SettingsPanel
```

#### Key UI Features

| Feature | Implementation |
|---------|---------------|
| **Streaming text** | Tokens append in real-time to the current assistant message bubble |
| **Inline tool indicators** | When a tool runs, an inline badge appears in the message timeline between text segments |
| **Diff cards** | File changes render as cards with `react-diff-viewer-continued` (side-by-side or inline mode) |
| **Accept/Revert** | Buttons on diff cards: Accept applies via `vscode.workspace.applyEdit`, Revert calls `POST /api/AgentController/revert` |
| **Open Diff** | Opens VS Code's native diff view (`vscode.diff` command) comparing the real file with proposed changes |
| **@-mentions** | Type `@` in the input to search files and symbols (queries backend endpoints) |
| **Conversation history** | Sidebar panel with save/load/rename/delete; auto-saves after each exchange |
| **Dark/Light theme** | Auto-detects VS Code theme and applies to the webview |
| **Health status** | Connection indicator in header (green/yellow/red) |
| **Token suppression** | Filters leaked tool-call text (`TOOL_REQUEST`, `END_TOOL_REQUEST`) from the LLM output stream |
| **Post-processing cleanup** | On stream end, cleans all text segments of leaked protocol markers and collapses excessive whitespace |

### 3.3 State Management

Uses **Zustand 5** with 5 stores:

| Store | State |
|-------|-------|
| `chatStore` | Messages (with segment timeline), active tools, tool history, streaming/error flags |
| `conversationStore` | Conversation list, active conversation ID, CRUD operations, auto-persistence |
| `changeStore` | File changes with accept/revert status |
| `mentionStore` | Open files, recent files, search results for @-mention dropdown |
| `settingsStore` | Backend URL, selected model, diff view mode, health status |

**Persistence flow:**
1. Webview auto-saves every 300ms (debounced) via `saveCurrentSession` message
2. Extension stores current session + conversations in `workspaceState`
3. On reload, extension sends `restoreState` with saved conversations and session
4. Webview hydrates all stores from the restored state

### 3.4 Streaming Protocol Integration

The webview establishes a message listener in `useAgentStream` that handles events from the extension:

```typescript
// AgentApiClient parses SSE lines → yields typed events
for await (const event of agentApiClient.runStream({ task, workspace })) {
  switch (event.type) {
    case 'content':  // LLM token → append to message bubble
    case 'toolStart':  // Show tool indicator, suppress text leaks
    case 'toolResult': // Update tool status, resume text display
    case 'change':    // Render diff card with Accept/Revert buttons
    case 'done':      // Finalize message, run post-processing cleanup
    case 'error':     // Show error, reset streaming state
  }
}
```

**Token suppression logic:** When a tool execution starts, the hook enters "suppressing" mode — any text tokens containing leaked protocol markers (`TOOL_REQUEST`, `PARAM:`, etc.) are silently dropped. On stream completion, all text segments are post-processed to remove any remaining leaked text and collapsed whitespace.

---

## 4. Client-Server Protocol

### 4.1 SSE Message Types

The streaming endpoint `POST /v1/chat/completions` returns `text/event-stream` with the following message types:

| SSE Line | Type | Description |
|----------|------|-------------|
| `data: {"content":"text..."}` | LLM token | Batched text chunks from the LLM (50ms batching) |
| `data: {"tool":{"type":"tool_start","tool":"read_file","args":{...}}}` | Tool start | Tool execution began |
| `data: {"tool":{"type":"tool_result","tool":"read_file","result":"...","status":"completed","summary":"Read X"}}` | Tool result | Tool completed (result truncated to 1000 chars) |
| `data: {"change":{"changeId":"...","sessionId":"...","filePath":"...","patch":"--- a/...\n+++ b/...","isNewFile":false,"summary":"...","patchSize":342}}` | File change | File was modified (unified diff patch included) |
| `data: {"done":true}` | Stream end | Agent completed the task |
| `data: {"error":"..."}` | Error | Streaming error |

The streaming endpoint also emits a **session start** event at the beginning:
```json
data: {"tool":{"type":"session_start","sessionId":"0199a2b6-...","task":"Add validation..."}}
```

### 4.2 Extension↔Webview Messaging

The extension core and webview communicate via VS Code's `postMessage` API using a typed protocol defined in `shared/protocol.ts`:

**Webview → Extension:**
- `runTask` — Start a new agent task (with optional context files)
- `cancelTask` — Abort the current streaming task
- `applyEdit` — Apply a file change to the workspace
- `revertFile` — Revert a file change via backend
- `openDiff` — Open VS Code native diff view
- `searchSymbols` / `searchFiles` — @-mention search
- `healthCheck` — Ping backend
- `saveConversations` / `loadConversations` — Persistence
- `saveCurrentSession` — Auto-save current chat state

**Extension → Webview:**
- `init` — Theme information
- `restoreState` — Saved conversations + session
- `token` — LLM text token
- `toolStart` / `toolResult` — Tool execution status
- `changeEvent` — File change with diff
- `streamDone` — Streaming completed
- `symbolResults` / `fileResults` — Search results
- `healthStatus` — Backend connectivity
- `error` — Error messages
- `clearChat` / `sendSelection` — UI commands

---

## 5. API Reference

### AgentController (`/api/AgentController`)

| Method | Path | Request | Response | Description |
|--------|------|---------|----------|-------------|
| `POST` | `/run` | `{"task":"...", "workspace?":"..."}` | `{"success":true, "response":"...", "iterations":4, "fileChanges":[...]}` | Non-streaming agent run |
| `POST` | `/v1/chat/completions` | Same as `/run` | SSE stream (see Section 4.1) | Streaming agent run |
| `POST` | `/index` | `{"workspace?":"...", "maxFiles?":500}` | `{"success":true, "message":"Indexing complete"}` | Re-index workspace (embeddings) |
| `GET` | `/changes?sessionId=X` | Query params | `{"count":N, "changes":[...]}` | Query changes by session |
| `GET` | `/changes?filePath=X` | Query params | `{"count":N, "changes":[...]}` | Query changes by file |
| `GET` | `/changes?count=20` | Query params | `{"count":N, "changes":[...]}` | Recent changes |
| `POST` | `/revert` | `{"changeId":"..."}` | `{"success":true, "changeId":"..."}` | Revert single change by ID |
| `POST` | `/revert` | `{"sessionId":"..."}` | `{"success":true, "revertedCount":N, "files":[...]}` | Revert all changes in a session |
| `POST` | `/revert` | `{"filePath":"..."}` | `{"success":true, "message":"Reverted X"}` | Legacy backup-based revert |
| `GET` | `/symbols?query=X` | Query params | `{"symbols":[{type,name,className,filePath}]}` | Symbol search (for @-mentions) |
| `GET` | `/files?query=X` | Query params | `{"files":["path1","path2"]}` | File search (for @-mentions) |

### ChatController (`/api/ChatController`)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/` | Simple chat passthrough to LLM (no tools, no streaming, no memory) |

### HealthController (`/api/HealthController`)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/` | Basic health check |
| `GET` | `/detailed` | Dependency status (DB, LLM, vector DB) |
| `GET` | `/metrics` | Current metrics snapshot |
| `GET` | `/info` | System info (runtime, memory, uptime) |
| `POST` | `/metrics/reset` | Reset metrics counters |

---

## 6. Configuration

### Backend (`appsettings.json` / `appsettings.Development.json`)

```json
{
  "Agent": {
    "MaxIterations": 15,
    "MaxContextTokens": 12000,
    "MaxConversationTurns": 6,
    "WorkspaceRoot": "/path/to/target/project"
  },
  "DeepSeek": {
    "ApiKey": "sk-...",
    "BaseUrl": "https://api.deepseek.com",
    "Model": "deepseek-chat"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=ai_agent;Username=postgres;Password=..."
  }
}
```

**AgentOptions:**

| Option | Default | Description |
|--------|---------|-------------|
| `MaxIterations` | 15 | Hard cap on agent loop iterations per request |
| `MaxContextTokens` | 12000 | Token budget; pruning triggers at 70% |
| `WorkspaceRoot` | — | Absolute path to the codebase the agent works in |

**LLMOptions (section name: `DeepSeek`):**

| Option | Description |
|--------|-------------|
| `ApiKey` | DeepSeek API key |
| `BaseUrl` | API base URL (`https://api.deepseek.com`) |
| `Model` | Model name (`deepseek-chat`, `deepseek-reasoner`) |

### VS Code Extension (`aiChat.*` settings)

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `aiChat.backendUrl` | `string` | `http://localhost:5036` | Backend server URL |
| `aiChat.streamTimeout` | `number` | `120000` | SSE stream timeout (ms) |
| `aiChat.diffViewMode` | `"side-by-side"` \| `"inline"` | `"side-by-side"` | How to render file diffs |

---

## 7. Running the System

### Prerequisites

| Component | Purpose | Required? |
|-----------|---------|-----------|
| .NET 10 SDK | Run the backend | Yes |
| PostgreSQL | Conversation memory persistence | Yes |
| ChromaDB | Vector store for semantic search | Yes |
| Ollama + `nomic-embed-text` | Embedding generation | Yes |
| DeepSeek API key | LLM provider | Yes |
| Node.js 22+ | Build/run the VS Code extension | For extension only |

### Step-by-Step

```bash
# ── 1. Start PostgreSQL ──────────────────────────────────
# Option A: Docker
docker run -d --name postgres \
  -e POSTGRES_PASSWORD=<choose-a-password> \
  -e POSTGRES_DB=ai_agent \
  -p 5432:5432 postgres:16

# Option B: Local installation
pg_isready  # verify it's running

# ── 2. Start ChromaDB (port 8000) ───────────────────────
pip install chromadb
chroma run --host localhost --port 8000

# ── 3. Start Ollama (port 11434) ────────────────────────
ollama pull nomic-embed-text   # one-time
ollama serve

# ── 4. Apply database migrations ─────────────────────────
cd Ai-Agent/
dotnet ef database update

# ── 5. Run the backend (port 5036) ──────────────────────
dotnet run
# App starts on http://localhost:5036
# Automatically indexes the workspace on startup

# ── 6. Build & run the VS Code extension ─────────────────
cd ai-chat-extension/
npm install
npm run build:all    # builds webview UI → extension bundle

# In VS Code: Press F5 to launch Extension Development Host
# Or install the .vsix via: code --install-extension ai-chat-extension-0.0.1.vsix
```

### Verification

```bash
# Health check
curl http://localhost:5036/api/HealthController

# Test agent (non-streaming)
curl -X POST http://localhost:5036/api/AgentController/run \
  -H "Content-Type: application/json" \
  -d '{"task": "List all controllers in the project"}'

# Re-index workspace
curl -X POST http://localhost:5036/api/AgentController/index \
  -H "Content-Type: application/json" \
  -d '{"maxFiles": 200}'

# Query recent changes
curl http://localhost:5036/api/AgentController/changes?count=5
```

---

## 8. Technology Stack

### Backend

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 10, C# 14 |
| Web Framework | ASP.NET Core Web API |
| ORM | Entity Framework Core 10 |
| Database | PostgreSQL (via Npgsql) |
| Vector Store | ChromaDB v1.4+ (REST API) |
| LLM Provider | DeepSeek (function calling + streaming) |
| Embeddings | Ollama (`nomic-embed-text`) |
| HTTP Resilience | Polly (exponential backoff retry) |
| DI | Built-in `Microsoft.Extensions.DependencyInjection` |

### Frontend (VS Code Extension)

| Layer | Technology |
|-------|-----------|
| Extension Runtime | Node.js 22, TypeScript 5.9 |
| Extension Bundling | Webpack 5 |
| Webview Framework | React 18 |
| Webview Bundling | Vite 8 |
| Styling | Tailwind CSS 4 |
| State Management | Zustand 5 |
| Animations | Framer Motion 12 |
| Virtual Scrolling | `@tanstack/react-virtual` |
| Markdown | `react-markdown` + `remark-gfm` |
| Syntax Highlighting | `react-syntax-highlighter` |
| Diff Viewer | `react-diff-viewer-continued` |
| Icons | Lucide React |

---

## 9. Known Limitations & Roadmap

### Current Limitations

| Issue | Impact | Status |
|-------|--------|--------|
| **Only DeepSeek LLM implemented** | No Ollama LLM client — `LLMProviderRegistry` exists but is unused | TODO |
| **ChatController is basic** | No streaming, tools, or memory — just passthrough | Low priority |
| **`OllamaEmbeddingService` uses raw `new HttpClient()`** | No `IHttpClientFactory`, no Polly retry | Should fix |
| **RunAsync non-streaming does not emit session_start event** | Frontend needs to handle both paths | Minor |
| **Diff computation is O(n*m) LCS** | Can be slow for very large files (>5000 lines) | Acceptable for now; Myers algorithm would be faster |
| **No incremental indexing** | Full re-index on every run (mitigated by upsert + 500-file cap) | Roadmap |
| **`MaxConversationTurns` in AgentOptions is unused** | Dead config — only `MaxIterations` is enforced | Clean up |

### Suggested Roadmap

| Priority | Item |
|----------|------|
| 🔴 High | Implement Ollama LLM client (`OllamaClient : ILLMClient`) for local LLM support |
| 🔴 High | Wire `OllamaEmbeddingService` through `IHttpClientFactory` with Polly retry |
| 🟡 Medium | Incremental indexing: only re-embed changed files (use file modification timestamps) |
| 🟡 Medium | Add Roslyn-based C# chunking for higher-quality embeddings (class/method boundaries) |
| 🟡 Medium | Parallel embedding requests in `CodeVectorIndexer` |
| 🟡 Medium | ChatController: add streaming, conversation history, and tool access |
| 🟢 Low | Hybrid search (BM25 + vector) in `SemanticSearchTool` for higher recall |
| 🟢 Low | Command allowlist and process sandboxing for `TerminalTool` |
| 🟢 Low | Remove unused `DiffReviewService` (replaced by `ChangeTracker`) |
| 🟢 Low | Remove `MaxConversationTurns` dead config or wire it into the agent loop |
