# Changelog

## 0.3.0: checks its own work, predictable cost, offline autocomplete

### Added
- **Verify loop**: after the agent changed files, the project's build and tests run automatically, then:
  - the result appears in the review bar ("✓ Build passed · 12/12 tests passed") and as a card per attempt;
  - failures go back to the agent with the output, and it fixes them (up to 2 attempts).

  Commands are detected (dotnet, npm/pnpm/yarn/bun, cargo, go, Maven, Gradle, pytest) or configured; checks run in
  trusted workspaces only.
- **Budget per task**: stops before the next model call once a task reaches your cost cap, and offers Continue.
- **Offline autocomplete in one click**: Settings → Autocomplete → Run on this computer:
  - checks Ollama and downloads the small coder model that fits your machine (qwen2.5-coder 1.5B, about 1 GB, on a
    normal laptop);
  - turns autocomplete on and times a first suggestion.
- The agent may run the project's detected test/build commands (npm test, cargo test, go test…), still with approval.
- Website: [comparison with Cursor, Claude Code and Devin Desktop](https://alialalawi14.github.io/stoat/compare.html).

### Changed
- Backend protocol 7.

## 0.2.0: MCP, web and autocomplete

### Added
- **MCP servers** (Model Context Protocol, official C# SDK): local (stdio) or remote (HTTP/SSE) servers; tools
  offered as `mcp__server__tool`, with schemas made compatible with every provider. Settings → MCP servers:
  - presets: **Browser (Playwright)**, **Context7 docs**, **GitHub**;
  - import from Cursor, Claude Desktop, Claude Code, Windsurf and VS Code;
  - custom servers, status and errors, per-tool on/off, "always allow", and tokens in secret storage.

  MCP tools ask before they run (also in Auto mode); servers never get the model API keys.
- **Web tools**:
  - `web_fetch` reads public pages as Markdown, with SSRF protection at connect time; it asks per URL by default.
  - `web_search` works through Brave Search API, Tavily or SearXNG.
- **Tab autocomplete** (off by default): ghost text from fill-in-the-middle models:
  - supported: DeepSeek, Mistral Codestral, Ollama, llama.cpp/vLLM, plus a chat fallback;
  - **Try it** in Settings, and a status-bar toggle and snooze.
- `docs/STOAT-SYSTEM-OVERVIEW.md`: how Stoat works, for contributors and AI assistants.

### Changed
- Auto mode auto-approves only file edits and allow-listed commands; any other tool that needs approval asks.
- Local MCP servers start in the project folder; the backend's process tree is ended with it on Windows.
- Backend protocol 6.

## 0.1.0: first public release as **Stoat**

### Added
- **Set up models in the chat panel**: first run shows a "Connect a model" card (provider, key with a *Get a key*
  link, models read live, Save & connect); the same form in Settings, with the provider list and the backend's
  real address, Restart and Show log.
- **Review edits after they're applied** (Cursor-style, `aiChat.reviewEdits`, on by default): Agent and Auto
  edit without stopping; a review bar lists every changed file with Keep / Undo per file and Keep all / Undo all,
  and each change card has Keep / Undo. Commands still ask first. Backend protocol 5 (`reviewEdits`).
- Builds for Windows, macOS (Apple Silicon and Intel) and Linux (x64, arm64), published to the VS Code
  Marketplace and Open VSX by `.github/workflows/release.yml`.

### Fixed
- The chat no longer hangs on "Thinking..." when no provider is set up (backend start waited on a notification).
- **Stop** ends the run at once and actually stops the agent; a stopped run no longer leaks into the next reply
  or breaks Stop for it.
- The bundled backend is made executable before it starts on macOS/Linux.

### Changed
- Renamed to **Stoat** (extension id `AliAlalawi14.stoat`). Keys saved by the old "AI Agent" build must be entered once more.
- EF Core / ASP.NET Core OpenApi 10.0.12 (clears two high-severity advisories).

## Earlier (unreleased builds)

### Added: any model provider
- **AI Agent: Add Provider**: Gemini, OpenAI, Mistral, xAI, Groq, OpenRouter, Together, Fireworks, Azure OpenAI,
  Ollama, LM Studio or any OpenAI-compatible server; model list read live from the provider; as many as you like.
- Backend `Providers:Custom:N` (full base URL, auth `bearer|api-key|none`); the older `OpenAI:*` section still works.
- Claude subscriptions can't be used (Anthropic's terms allow API keys or cloud providers only); documented.

### Added: zero-config install
- The extension starts its own backend (bundled, self-contained: no .NET needed) on a free localhost port with a
  random token; the open folders are its workspaces; restarted when folders change.
- **AI Agent: Set API Key** (keys in VS Code secret storage), **Restart Backend**, **Show Backend Log**;
  OpenAI-compatible / Ollama settings in VS Code; a clear prompt when no key is set.
- Conversation memory on a **SQLite file by default**; Postgres optional (`Database:Provider=postgres`). The backend
  starts without a database (memory off) instead of crashing.
- Multi-root windows: the workspace is the folder of the active file.
- Chroma/Ollama URLs configurable; without them semantic search is off after a 3 s probe (no retries, no stack trace).
- `scripts/publish-backend.ps1|.sh` and a platform VSIX (win32-x64: 55 MB).

### Added
- Plan mode like Cursor: clarifying questions, the plan as an editable file (`.ai/plans/*.plan.md`) that builds follow,
  progress ticked in the file, Build / Build selected, Mermaid diagrams, plan revisions, Shift+Tab and a Plan-mode hint.
- Providers: Claude (official Anthropic SDK) and any OpenAI-compatible endpoint (OpenAI, OpenRouter, Groq, Ollama),
  routed per model; a model picker filled from the backend.
- `delete_file` / `move_file` tools (approved, revertable); allowed commands from config; symlink/junction guard.
- Eval gate: `--repeat`, `--compare` (noise-aware tolerances, common-task KPIs), `--recompare`, `--model`, second fixtures.

### Changed
- One workspace per request everywhere (project context, change log, semantic index), with a cached code map.
- Semantic index covers whole files, re-indexes only changed files and removes deleted ones.
- Prompt order for provider prompt caching (cache-hit rate 0% → ~75%); cost includes cache-hit pricing.

### Fixed
- A run waiting for approval no longer blocks other chats; provider error messages reach the chat;
  a failed write to a new path is no longer recorded as a change.
