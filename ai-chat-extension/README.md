# Stoat – AI Coding Agent

> **Small agent. Takes on the big ones.** A stoat weighs about 200 g and hunts rabbits ten times its size.

**An open-source coding agent for VS Code that works with the model you choose.**
It reads your project, plans features with you, edits the code, and lets you **Keep or Undo every change**.
Use Claude, GPT, Gemini, DeepSeek, Mistral, Grok, any OpenAI-compatible API, or a **local model** that never
sends your code anywhere.

<!-- Add a short demo GIF here (docs/demo.gif in the repo, referenced by its https://raw.githubusercontent.com URL):
     ask a question → Agent edits 2–3 files → review bar → Keep all. -->

- 🔑 **Your key, your model.** No subscription and no account. Pay the provider only for what you use, or pay nothing with Ollama / LM Studio.
- 🧪 **Checks its own work.** After editing, it runs your build and tests; if they fail, it fixes them before it says "done". The review shows "✓ Build passed · 12/12 tests passed".
- ✅ **Review, don't babysit.** The agent edits without stopping; when it's done you get every changed file with **Keep / Undo**, per file or all at once.
- 💵 **Predictable cost.** No subscription; cost on every reply and an optional budget per task.
- 🗺️ **Plan before building.** Plan mode asks what it needs to know, then writes an editable plan file that **Build** follows step by step.
- 🛡️ **Safe by default.** It only touches the open folder, commands wait for your approval, secrets are redacted, and every change can be undone.
- 🧩 **More tools when you want them.** MCP servers (a browser, GitHub, library docs, databases…), web page reading and search, and optional tab autocomplete.
- 📦 **Nothing to install.** The agent's engine ships inside the extension and starts by itself. No Python, no .NET, no Docker.

---

## Get started in 60 seconds

1. **Install** Stoat from the Extensions view (`Ctrl+Shift+X`, search *Stoat*).
2. **Open a project folder** and click the **Stoat** icon in the activity bar (or `Ctrl+Shift+L`).
3. **Connect a model** in the panel: pick a provider, paste your key, choose models, then **Save & connect**.
   Every provider has a *Get a key* link. For a free, private option pick **Ollama (local)**.
4. Ask something: *"Explain how this project is structured"* or *"Add input validation to the signup form"*.

Keys are stored in VS Code's secret storage, never in settings files.

---

## How you work with it

### Four modes (`Shift+Tab` to switch)

| Mode | What it does | Changes files? |
|---|---|---|
| **Ask** | Answers questions about your code. | Never |
| **Plan** | Investigates, asks clarifying questions, then writes a step-by-step plan to `.ai/plans/<name>.plan.md`. Edit the plan like any file; **Build** follows your version. | Only its plan file |
| **Agent** | Edits files and runs build/test commands. Edits apply right away and you review them afterwards; **commands wait for your approval**. | Yes, reviewed |
| **Auto** | Like Agent, but safe commands (build, test, `git status`…) also run without asking. Use on code under version control. | Yes, reviewed |

### Reviewing changes

When a run finishes, the review bar above the input lists every file the agent changed:

```
▾ 3 files changed  +42 −7                      [↶ Undo all] [✓ Keep all]
   Program.cs       src          2 edits  +30 −5    ↗  ↶  ✓
   UserService.cs   src/Services          +10 −2    ↗  ↶  ✓
   Helper.cs        (new)                 +2  −0    ↗  ↶  ✓
```

- **↗** opens a side-by-side diff (before the agent ↔ now) in the editor.
- **↶ Undo** restores the file. Several edits to one file are undone newest-first. If you've since changed the same lines yourself, Undo stops instead of overwriting your work.
- **✓ Keep** accepts it. You can also Keep or Undo each individual edit from its card in the chat.

Prefer to approve each edit *before* it's written? Turn off **Settings → Review edits after they're applied**.

### Checks: build and tests after every change

When the agent has changed files (Agent and Auto mode), Stoat runs the project's build and tests automatically, shows the result in the review bar and as a card in the chat, and hands failures back to the agent to fix (up to 2 attempts). Commands are detected from the project: `dotnet`, `npm`/`pnpm`/`yarn`/`bun` scripts (or `tsc` for TypeScript), `cargo`, `go`, Maven, Gradle and `pytest`. Or set your own in Settings → **Checks & budget** (e.g. `npm run lint`, `npm test`). Checks run only in workspaces you trust.

### Budget per task

Settings → **Checks & budget** → *Budget per task*: the agent stops before its next model call once a task has cost that much, and offers **Continue**. It applies to models with a known price (the cost shown under each reply).

### MCP servers: give the agent more tools

Settings (⚙️) → **MCP servers** connects [Model Context Protocol](https://modelcontextprotocol.io) servers:

- **Quick add**: **Browser (Playwright)** to open pages, click and fill forms; **Library docs (Context7)**; **GitHub** (issues, PRs, code search).
- **Import** the servers you already use in Cursor, Claude Desktop, Claude Code, Windsurf or VS Code.
- **Any server**: a local command (`npx …`, `uvx …`) or a remote URL, with environment variables or headers. Tokens go to secret storage.
- Each server shows its status, errors, tools and what it adds to every request, so you can switch off tools you don't need.
- **MCP tools ask before they run** (Run / Reject), also in Auto mode, unless you turn on *Run its tools without asking* for a server you trust.

Servers are configured in your user settings only, so a project you open can't add programs to run. Local servers start in your project folder (the browser saves page snapshots to `.playwright-mcp/`; add it to `.gitignore`).

### Web pages and search

- **web_fetch** reads public pages (docs, issues, changelogs) as Markdown. It **asks before each fetch** by default and always refuses your computer, your network and cloud metadata addresses.
- **web_search** uses the provider you choose in Settings → **Web**: Brave Search API, Tavily, or your own SearXNG.

### Tab autocomplete (optional), offline on any laptop

Settings → **Autocomplete** → **Run on this computer**: one click downloads a small coder model through [Ollama](https://ollama.com) (about 1 GB on a normal laptop, larger when you have more memory) and turns autocomplete on. Suggestions are then free, private and work without internet.

Or pick a cloud provider and a code model, click **Try it**, turn it on. Ghost text appears as you type; **Tab** accepts. Works with fill-in-the-middle models: a small local coder model on Ollama (e.g. `qwen2.5-coder:1.5b-base`), Mistral Codestral, DeepSeek, or llama.cpp/vLLM servers, with a slower chat fallback for other models. The status bar item turns it on/off or snoozes it.

### More

- **@-mentions**: type `@` to add files or symbols as context. The file you're looking at and your selection are included automatically.
- **Ask about selection**: select code, then `Ctrl+Shift+A` (or right-click).
- **Stop** at any time with the stop button. The agent stops immediately, including a running command; edits already made stay in the review so you can undo them.
- **Several providers at once**: switch models per message from the model menu in the header.
- **History**: past conversations are in the history panel (🕘).

---

## Supported models

| Provider | Setup | Notes |
|---|---|---|
| **Anthropic (Claude)** | API key | Strong at agentic coding |
| **DeepSeek** | API key | Very low cost |
| **OpenAI** | API key | |
| **Google Gemini** | API key (AI Studio) | |
| **Mistral**, **xAI (Grok)**, **Groq**, **Together**, **Fireworks** | API key | |
| **OpenRouter** | One key | Hundreds of models |
| **Azure OpenAI** | Resource name + key | |
| **Ollama**, **LM Studio** | No key | Runs on your machine; code never leaves it |
| **Any OpenAI-compatible server** | URL + key | vLLM, DeepInfra, a company gateway… |

Agent and Plan modes need models that support **tool calling**. Most current models do; small local models vary.

> **Claude Pro/Max subscription?** It can't be used here. Anthropic only allows subscription logins in Claude.ai and
> Claude Code; other tools must use an API key from console.anthropic.com, or Claude through AWS Bedrock,
> Google Vertex or Microsoft Foundry.

---

## Privacy & safety

- **Your code is sent only to the model provider you choose**, straight from your machine. Stoat has no server and collects no telemetry.
- The agent **can only read and write inside the open folder**. Paths that escape it (`..`, symlinks) are refused.
- **Commands** run without a shell, from an allow-list, and wait for your approval (Auto mode runs only safe ones by itself).
- **Secrets**: key and certificate files are never read, and keys or tokens in any output are redacted before the model sees them.
- **MCP servers** run with your permissions: add only ones you trust. They never see your model API keys.
- **Autocomplete**, when on, sends the code around your cursor to the provider you chose for it.

Details and how to report a vulnerability: [SECURITY.md](https://github.com/AliAlalawi14/stoat/blob/main/SECURITY.md).

---

## Commands & shortcuts

| Command | Shortcut |
|---|---|
| Stoat: Open Sidebar | `Ctrl+Shift+L` (`Cmd+Shift+L`) |
| Stoat: Ask About Selection | `Ctrl+Shift+A` (`Cmd+Shift+A`) |
| Switch mode | `Shift+Tab` in the input |
| Stoat: New Conversation | **+** in the panel header |
| Stoat: Add Provider / Remove Provider / Set API Key | also in the panel's Settings (⚙️) |
| Stoat: Restart Backend / Show Backend Log | also in the panel's Settings (⚙️) |

## Settings

| Setting | Default | |
|---|---|---|
| `aiChat.reviewEdits` | `true` | Edits apply at once and you review them afterwards. `false`: each edit waits for Accept. |
| `aiChat.mcpServers` | `{}` | MCP servers (`mcpServers` format; user settings only). Easier in Settings → MCP servers. |
| `aiChat.web.fetch` | `ask` | `ask` each URL, `allow`, or `off`. |
| `aiChat.web.searchProvider` | none | `brave`, `tavily` or `searxng` (key in secret storage). |
| `aiChat.completion.enabled` | `false` | Tab autocomplete; provider, model and style in Settings → Autocomplete. |
| `aiChat.verify.enabled` | `true` | Build and test after the agent's changes; failures go back to the agent. |
| `aiChat.verify.commands` | `[]` | Your own check commands (empty = detected). |
| `aiChat.budgetPerTask` | `0` | Stop a task at this cost in USD (0 = no limit). |
| `aiChat.providers` | `[]` | Providers added in the panel (keys live in secret storage, not here). |
| `aiChat.defaultProvider` | first configured | Provider used when no model is picked. |
| `aiChat.diffViewMode` | `side-by-side` | How diffs open. |
| `aiChat.backendUrl` | empty | Leave empty. Set only to use a backend you run yourself (contributors). |

---

## Troubleshooting

- **Red dot / "backend not running"**: open *Settings (⚙️) → Show log*. *Restart* fixes most stuck states.
- **"The provider rejected the key"**: the key is wrong or has no credit; create a new one from the *Get a key* link.
- **Ollama: "Nothing answered"**: start Ollama (`ollama serve`) and pull a model first (`ollama pull qwen3:14b`).
- **The agent answers but never edits**: you're in **Ask** or **Plan** mode, or the model doesn't support tool calling.
- **An MCP server shows "Failed"**: open it in Settings → MCP servers for the error (e.g. an npm 404 or a missing token). Local servers need Node.js (`npx`) or Python (`uvx`).
- **No autocomplete**: check the status bar item; **Try it** in Settings → Autocomplete shows the exact error.

Found a bug or have an idea? [Open an issue](https://github.com/AliAlalawi14/stoat/issues).

## License

[MIT](https://github.com/AliAlalawi14/stoat/blob/main/LICENSE). Free for personal and commercial use.
