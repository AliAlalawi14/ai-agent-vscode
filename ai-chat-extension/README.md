# AI Agent for VS Code

A coding agent in your sidebar: it answers questions about your code, **plans features with you** before touching
anything, and makes changes **you approve**, with a revert for every change.

## Quickstart (2 minutes)

1. Install the extension. Nothing else: the agent's backend is bundled and starts by itself.
2. Connect a model, either way:
   - `Ctrl+Shift+P` → **AI Agent: Set API Key**: **DeepSeek** (cheapest) or **Anthropic / Claude** (strongest).
   - `Ctrl+Shift+P` → **AI Agent: Add Provider**: **Google Gemini, OpenAI, Mistral, xAI Grok, Groq, OpenRouter,
     Together, Fireworks, Azure OpenAI**, a **local Ollama / LM Studio** (no key; your code never leaves your machine),
     or **any OpenAI-compatible server**. The wizard reads the provider's model list for you. Add as many as you like.
3. Open a folder, open the **AI Assistant** view (`Ctrl+Shift+L`) and ask: *"explain this project"*.

Keys are kept in VS Code's secret storage, never in settings files.

> **Claude Pro/Max subscription?** Not possible, by Anthropic's rules: subscription logins may only be used in
> Claude.ai and Claude Code; other tools must use an **API key** (console.anthropic.com) or Claude through your
> company's cloud (AWS Bedrock, Google Vertex, Microsoft Foundry).

## Modes (`Shift+Tab` to switch)

| Mode | What it does |
|---|---|
| **Ask** | Answers questions. Can't change anything. |
| **Plan** | Asks what it needs to know, then writes a plan to `.ai/plans/<name>.plan.md`. Edit it like any file; **Build** follows your version and ticks steps as they're done. |
| **Agent** | Edits files and runs build/test commands; **every change waits for your Accept**, and each one can be reverted. |
| **Auto** | Like Agent, but edits apply without asking. Use it on code under version control. |

## Good to know

- **Your code goes to the provider you picked.** Use a local model if it must stay on your machine.
- The agent only touches the open workspace; commands run without a shell from an allow-list. See `SECURITY.md`.
- Commands: **AI Agent: Set API Key**, **Add Provider**, **Remove Provider**, **Restart Backend**, **Show Backend Log** (first stop when something's off).

## Settings

| Setting | Default | |
|---|---|---|
| `aiChat.defaultProvider` | first with a key | Provider used when no model is picked |
| `aiChat.providers` | empty | Providers added with **Add Provider** (edit or remove with **Remove Provider**) |
| `aiChat.backendUrl` | empty | Leave empty. Set it only to use a backend you run yourself (contributors) |
| `aiChat.diffViewMode` | side-by-side | How diffs open |

## Contributing

The backend lives in `../Ai-Agent` (.NET). Build the bundled binary with `scripts/publish-backend.ps1` (or `.sh`),
or run the backend yourself and point `aiChat.backendUrl` at it.
