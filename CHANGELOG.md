# Changelog

## Unreleased

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
