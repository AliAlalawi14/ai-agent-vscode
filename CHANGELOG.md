# Changelog

## Unreleased

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
