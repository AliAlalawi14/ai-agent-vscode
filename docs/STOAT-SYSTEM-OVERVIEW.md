# Stoat: System Overview

> A complete description of Stoat (what it does, how it's built, how it behaves, and what it measures), written so it
> can be handed to another engineer or AI assistant as context. The last sections describe how local models fit in
> today and the open questions for running Stoat well on ordinary laptops.

---

## 1. What Stoat is

**Stoat is an open-source (MIT) AI coding agent that runs inside VS Code** (and VS Code forks: Cursor, Windsurf,
VSCodium). A developer chats with it in a sidebar panel; it reads the project, answers questions, plans features,
edits files and runs build/test commands.

**Positioning: "Small agent. Takes on the big ones."** A stoat weighs ~200 g and hunts rabbits ten times its size.
Stoat competes with large, closed, subscription tools (Cursor, GitHub Copilot, Claude Code) by being:
- **model-agnostic**: any provider with the user's own API key (Claude, OpenAI, Gemini, DeepSeek, Mistral, Grok,
  Groq, OpenRouter, Together, Fireworks, Azure OpenAI, any OpenAI-compatible API) or a **local model** (Ollama, LM Studio);
- **free, with no account and no telemetry**: users pay their model provider directly, or nothing with a local model;
- **safe**: workspace-only file access, no shell, secret redaction, approvals for commands, and every edit can be undone;
- **zero setup**: the agent engine ships inside the extension and starts itself;
- **lean**: small prompts (≈3k tokens per step, ≈7k per task), which matters a lot for local models.

- Extension id: `AliAlalawi14.stoat`
- Repo: `github.com/AliAlalawi14/stoat`
- Website: `alialalawi14.github.io/stoat`

---

## 2. What the user sees

### Modes (switch with Shift+Tab)
| Mode | Behaviour | Tools offered |
|---|---|---|
| **Ask** | Answers questions about the code. | Read-only: read/search/list/find (+ semantic search if indexed) |
| **Plan** | Investigates, may ask clarifying questions (a card with options), then writes a step-by-step plan to `.ai/plans/<name>.plan.md`. The user can edit the file; **Build** executes it and ticks steps off. | Read-only + `ask_questions`, `submit_plan` |
| **Agent** | Edits files and runs commands. | Everything + `update_plan` |
| **Auto** | Like Agent, but allow-listed safe commands (build, test, `git status`…) run without asking. | Everything + `update_plan` |

### Reviewing edits (default: "review after", like Cursor)
- The agent **applies file edits immediately** and keeps working; it doesn't stop at every file.
- When the run ends, a **review bar** above the input lists every changed file (+/− lines, number of edits).
  Per file: open a side-by-side diff, **Undo**, **Keep**. Plus **Keep all** / **Undo all**.
- Each edit also has a card in the chat with its own Keep / Undo.
- Undo is exact: several edits to a file are undone newest-first; if the user has since changed the same lines,
  Undo refuses instead of overwriting their work.
- **Commands always wait for approval** (Run / Reject card), except allow-listed ones in Auto mode.
- Setting `aiChat.reviewEdits = false` switches to "approve before": each edit waits for Accept.

### Other features
- **In-panel setup**: first run shows "Connect a model": pick a provider, paste a key (a "Get a key" link per
  provider), Load models (read live from the provider's `/models`), Save & connect. Keys live in VS Code
  SecretStorage.
- **Model menu** in the header: switch models per message across several providers.
- **@-mentions** of files/symbols; the active file and the editor selection are added as context automatically.
- **Stop**: ends the run immediately (UI and backend: model call, pending approval, running command).
- **History** of conversations; **metrics** per reply (tokens, cost, latency).

---

## 3. Architecture

```
┌──────────────────────── user's machine ───────────────────────┐
│  VS Code                                                       │
│  ┌──────────────────────┐   postMessage   ┌─────────────────┐  │
│  │ Chat panel (webview) │ ◄─────────────► │ Extension host  │  │
│  │ React + Zustand +    │                 │ (TypeScript)    │  │
│  │ Tailwind             │                 └───────┬─────────┘  │
│  └──────────────────────┘      starts & calls     │ HTTP + SSE │
│                                (127.0.0.1:random,  │ + token    │
│                                 random token)      ▼            │
│                                   ┌──────────────────────────┐ │
│                                   │ Backend (.NET 10,        │ │
│                                   │ ASP.NET Core, self-      │ │
│                                   │ contained, ~55 MB)       │ │
│                                   │ agent loop · tools ·     │ │
│                                   │ change log · plan files  │ │
│                                   └──┬──────────┬──────────┬─┘ │
│              reads/edits files       │          │ SQLite   │   │
│              (open folder only)      ▼          ▼ memory   │   │
│                                  project     agent.db      │   │
└────────────────────────────────────────────────────────────┼───┘
                                                             ▼
                               Model provider (HTTPS, user's key)
                               or Ollama / LM Studio on localhost
```

**Three parts:**
1. **Chat panel** (`ai-chat-extension/webview-ui`): React UI. It never talks to the network; everything goes through
   the extension via `postMessage`.
2. **Extension** (`ai-chat-extension/src`): VS Code integration. It:
   - starts and supervises the backend (`BackendProcess.ts`);
   - stores keys (SecretStorage) and provider settings;
   - relays chat requests and streams events to the panel (`MessageBroker.ts`);
   - opens diffs and applies Undo requests.
3. **Backend** (`Ai-Agent/Ai-Agent`): ASP.NET Core app, published **self-contained** per platform (no .NET needed)
   and bundled in the VSIX under `server/<rid>/`. It runs the agent loop, tools, providers, change tracking,
   plans and memory.

**Process model:**
- One backend per VS Code window, started on first use on a **free localhost port** with a **random token**
  (`X-Agent-Token` header required on every request).
- Its workspaces are the window's open folders; it restarts when the folders or the provider configuration change.
- Idle footprint measured on Windows: **~80–110 MB working set (~30 MB private)**.

**Platforms:** Windows x64, Linux x64/arm64, macOS arm64/x64. A GitHub Actions release workflow builds one VSIX per
platform (macOS builds run on a Mac so the executable is signed) and publishes to the VS Code Marketplace, Open VSX
and GitHub Releases.

---

## 4. Lifecycle of one request

1. The user sends a message in the panel (mode, selected model, @-mentions, recent history).
2. The extension adds editor context (active file, selection), resolves the workspace (the folder of the active file
   in multi-root windows), and POSTs to `backend /v1/chat/completions` with `reviewEdits` from settings. The response
   is a **Server-Sent Events** stream.
3. The backend builds the **system prompt**:
   - instructions per mode, tool descriptions, a cached **code map** of the project, the plan (if any);
   - ordered so the **stable part comes first** (tools, system, history) to maximize provider prompt caching;
   - history messages are capped at 6,000 chars each.
4. **Agent loop** (up to 8 / 15 / 40 / 40 steps in Ask / Plan / Agent / Auto), repeated each step:
   - call the model (streaming, with tool definitions);
   - for each tool call: run the loop guards, then the approval gate (see §5), then snapshot the files, execute the
     tool, and diff the result;
   - every changed file is recorded in the **change log** (unified patch + before/after), emitting a `change` event;
   - tool results go back to the model; the loop ends when the model answers without tool calls.
5. **Events streamed to the panel:**
   - `content` (text tokens), `tool` (start/result), `approval` (waiting / decision), `change`, `plan`,
     `questions`, `limit` (step budget hit → Continue button), `metrics` (tokens, cost, steps), `done`/`error`.
6. The panel renders text, tool cards, change cards and, after the run, the **review bar**. **Undo** calls
   `POST /api/Agent/revert` per change ID, newest first. **Stop** aborts the HTTP request, and the backend's
   `RequestAborted` token cancels the model call, approvals and running commands.

---

## 5. Agent internals

### Tools
| Tool | What it does | Side effects |
|---|---|---|
| `read_file`, `read_files` | Read one / several files (line ranges) | none |
| `list_directory`, `find_files` | Browse / glob | none |
| `search_code` | Text/regex search | none |
| `semantic_search` | Vector search over the project (only if the workspace is indexed) | none |
| `edit_file` | Exact string replace (keeps line endings/BOM) | file write |
| `replace_lines` | Replace a line range | file write |
| `write_file` | Create/overwrite a file | file write |
| `delete_file`, `move_file` | Delete / move (recorded, undoable) | file write |
| `run_terminal` | Run one allow-listed command (no shell) | process |
| `ask_questions`, `submit_plan` | Plan mode: clarifying-questions card, final plan file | plan file |
| `update_plan` | Agent mode: tick plan steps | plan file |

### Approval and review rules
- **File-writing tools**: applied immediately when `reviewEdits` is on (Agent/Auto) and reviewed afterwards;
  otherwise an approval card shows the proposed diff and the agent waits (10-minute timeout).
- **`run_terminal`**: always an approval card, except commands on `AutoApproveCommands` in Auto mode.
- A rejected action is reported to the model ("the user REJECTED …, don't retry").

### Loop guards
- An identical call that already failed in this request is refused.
- After 3 failed commands in a row, commands are paused and the model is told to explain.
- An output-limit truncation of a tool call is detected and not executed.

### Context management
- `Agent:MaxContextTokens` defaults to 48,000. When the conversation passes 60% of it, `ContextPruner` compacts
  whole tool rounds down to ~45% (it never splits a call from its result).

### Safety
- File access is limited to the workspace (`WorkspacePath.Resolve`: no `..`, absolute paths or links outside).
- Key/certificate files are never read; secrets in any tool output are redacted before the model sees them.
- Commands run without a shell, from an allow-list (`dotnet build/test/restore/clean`, `git status/diff/log/branch`,
  plus detected project build/test commands), so chaining with `&&`/pipes is impossible.
- Every tool call and approval is written to an append-only audit log.

### Concurrency
- The number of concurrent model calls is capped (a semaphore); waiting for approvals doesn't hold a slot.

---

## 6. Providers and routing
- **Native clients**: Anthropic Claude (official Anthropic SDK, with prompt caching) and DeepSeek (OpenAI format,
  with cache-hit pricing).
- **Any OpenAI-compatible API** as `Providers:Custom:N` (name, base URL including `/v1`, auth `bearer | api-key | none`,
  model list). The panel's presets cover Gemini, Mistral, xAI, Groq, OpenRouter, Together, Fireworks, OpenAI,
  Azure OpenAI, **Ollama (`http://localhost:11434/v1`, no key)**, **LM Studio (`http://localhost:1234/v1`, no key)**
  and "Custom server".
- **`RoutingLLMClient`** sends each request to the provider that owns the selected model (or the default provider).
- The extension passes keys to the backend as **environment variables** at start (from SecretStorage); nothing is
  written to config files.
- **Requirement:** Agent/Plan modes need models with **tool/function calling**. Ask mode works with any chat model.
- Cost tracking uses per-model prices including cache-hit pricing (shown per reply).

---

## 7. Storage
| What | Where | Notes |
|---|---|---|
| Conversation memory | SQLite file in the extension's global storage (`agent.db`) | Postgres optional; the backend runs without a DB (memory off) |
| Change log | `.ai_changes.ndjson` per workspace | Patches + before/after for exact undo; last 50 sessions |
| Plans | `.ai/plans/*.plan.md` in the project | Editable Markdown; Build follows the file |
| Audit log | `logs/audit-<date>.ndjson` next to the backend executable | Tool calls, approvals, run summaries (redacted) |
| Semantic index (optional) | Chroma (`localhost:8000`) + Ollama embeddings | Only when both answer (probed for 3 s); incremental re-indexing |
| Panel chat history | VS Code workspace state | Per workspace |

---

## 8. Quality measurement
An **eval gate** (`Ai-Agent/Ai-Agent.Evals`, tasks in `Ai-Agent/evals/tasks.json`):
- 26 real tasks on fixture projects, each run 3 times against a real model;
- scored on success, tool-call validity and approval counts;
- `--compare` flags regressions (a task that always passed now fails, or tokens/cost/latency worse by more than 10%).

Current baseline (**deepseek-chat**):

| KPI | Value |
|---|---|
| Tasks passing every run | **26 / 26** |
| Valid tool calls / tool errors | 100% / 0% |
| Tool calls per task (median / p90) | 2 / 7 |
| **Tokens per task (median)** | **7,142** (min 1,660, max 70,545) |
| Prompt cache hit rate | 66% |
| Cost per task (median) | $0.0015 |
| First token (median) | 0.73 s |
| System prompt (Ask mode, 5 tools) | ~3,600 characters (~900 tokens) + tool schemas |

Backend unit/integration tests: 123 (xUnit), including a scripted fake model for agent-loop behaviour.

---

## 9. Local models today, and the open questions

### How local models plug in now
- The panel has **Ollama (local)** and **LM Studio (local)** presets. Both are OpenAI-compatible, need no key, and
  their installed models are read automatically. Any other local server (llama.cpp `llama-server`, vLLM…) works
  through **Custom server**.
- The backend treats a local model like any other provider: same agent loop, same tools, same prompts.
- What a local model must support: **tool calling** (for Agent/Plan), **~16k+ context** for comfortable use (the
  backend budget is 48k tokens by default and configurable), and the OpenAI chat-completions format with streaming.

### The problem to solve
Running the agent **fully offline on an ordinary laptop**. Reference machine (the author's):
- **16 GB RAM, AMD Ryzen 7 4700U (8 cores), integrated Radeon graphics, no discrete GPU**, ~15 GB free disk;
- Ollama is not installed yet.

Known constraints:
- **Stoat itself is light** (~100 MB RAM, small SQLite file). **The model is the whole cost.**
- A 4-bit quantized model needs roughly 0.6 GB per billion parameters: 4B ≈ 2.5 GB, 8B ≈ 5 GB, 14B ≈ 9 GB.
  On 16 GB, ≤ 8B is comfortable and 14B is tight.
- **Generation speed** is bound by memory bandwidth: roughly 5–8 tokens/s for 8B on this class of laptop, 10–15
  for 4B (estimates, to be measured).
- **Prompt processing (prefill) is the real bottleneck on CPUs**: maybe 50–150 tokens/s. A 3k-token step takes
  tens of seconds the first time; later steps are fast only if the server **reuses the KV cache for the unchanged
  prompt prefix**. Stoat keeps its prefix stable for this reason.
- **Ollama does not use integrated AMD GPUs; llama.cpp's Vulkan build does.**
- Small models are weaker at multi-step tool use (malformed tool calls, wrong edits, giving up).

### Questions for the research
1. **Models**: which current open models (≤ 8B dense, or Mixture-of-Experts with a small active part) are best at
   *tool calling + code editing* on a 16 GB machine? Which quantization (Q4_K_M, Q5, …) keeps tool calling reliable?
2. **Runtime**: Ollama vs llama.cpp `llama-server` (Vulkan on integrated GPUs) vs LM Studio vs others (MLX on Apple
   Silicon, ONNX Runtime/NPU on Windows Copilot+ PCs). Which gives the best prefill speed and prefix-cache reuse on
   this hardware?
3. **Speed tricks**: KV-cache reuse across steps, speculative decoding with a small draft model, context size vs
   speed, flash attention on CPU/iGPU.
4. **Agent-side changes for small models** ("small-model mode"): fewer tools, shorter instructions, a simpler edit
   format, grammar-constrained / validated tool calls with one retry, smaller context budget. Which of these matter
   most?
5. **Hybrid**: local model by default, with an explicit "escalate to cloud" for hard tasks. Where should the line be?
6. **Using hardware the user already has**: running the model on another machine on the local network (desktop GPU,
   Mac mini) and connecting from the laptop. Stoat already supports this through a Custom server URL.
7. **Bundling**: should Stoat ship a one-click local engine (detect hardware → recommend a model → download →
   run), or guide users to Ollama/LM Studio?
8. **How to measure**: Stoat's eval gate can run against any provider (`--model`), so each candidate
   model/runtime can be scored on the same 26 tasks for pass rate, tokens/s, time to first token, and RAM.

---

## 10. Roadmap (next)
1. **MCP client**: connect external tools (GitHub, databases, docs, browsers) via the Model Context Protocol, with
   approvals and per-server trust.
2. **Web tools**: `web_fetch` (with SSRF protection) and `web_search` (Brave / Tavily / SearXNG); browser
   automation through the Playwright MCP server.
3. **Inline tab autocomplete**: ghost-text completions from fill-in-the-middle models (local or cloud), off by default.
4. Local-model strategy, based on the research above.

---

## 11. Repository map
| Path | Contents |
|---|---|
| `ai-chat-extension/src/` | Extension: `extension.ts`, `services/BackendProcess.ts`, `MessageBroker.ts`, `AgentApiClient.ts`, `providerSetup.ts`, `providerPresets.ts`, `shared/protocol.ts` |
| `ai-chat-extension/webview-ui/src/` | Panel: `components/chat`, `components/changes` (review bar, change cards), `components/setup`, `components/settings`, Zustand stores |
| `Ai-Agent/Ai-Agent/` | Backend: `Agent/Services` (AgentService, PromptBuilder, ContextPruner, ChangeTracker, ApprovalBroker…), `Tools/Services`, `LLM` (Claude, OpenAI-compatible, routing), `Controllers`, `Config` |
| `Ai-Agent/Ai-Agent.Tests/` | xUnit tests |
| `Ai-Agent/Ai-Agent.Evals/`, `Ai-Agent/evals/` | Eval runner, tasks, fixtures, results |
| `scripts/` | `publish-backend.ps1/.sh`: bundle the backend for one platform |
| `site/` | Website (GitHub Pages) |
| `.github/workflows/` | `release.yml` (multi-platform VSIX + publishing), `pages.yml` (website) |
