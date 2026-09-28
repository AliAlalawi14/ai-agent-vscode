# AI Agent for VS Code

A coding agent that works inside VS Code: it answers questions about your code, plans features with you
(clarifying questions, an editable plan file, Build), and makes changes you approve, with a revert for every change.

| Folder | What |
|---|---|
| [`Ai-Agent/`](Ai-Agent/README.md) | Backend (.NET): agent loop, tools, providers, plan files, eval harness |
| [`ai-chat-extension/`](ai-chat-extension/README.md) | VS Code extension (TypeScript + React webview) |

## Measured, not claimed

Every change runs through an eval gate: real tasks against a real model, each run 3 times
(`Ai-Agent/evals`). Current baseline: **26/26 tasks pass every run**.

## Status

Early and moving fast. See [CHANGELOG.md](CHANGELOG.md) and [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
