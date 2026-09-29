# Security

This agent **reads and edits files and runs commands on your machine** on behalf of an AI model.
This page says what protects you, what doesn't, and how to report a problem.

## What the agent can and cannot do

| Area | Guarantee |
|---|---|
| Files | Only inside the opened workspace. Paths are normalized and checked; `..`, absolute paths and symlinks/junctions that point outside are refused (`WorkspacePath.Resolve`). |
| Secrets | Files that hold keys or certificates (`*.pfx`, `*.pem`, `.env`...) are never read into the conversation; keys/tokens/passwords in any tool output are redacted before the model sees them (`SecretRedactor`). |
| Changes | Every edit, delete and move is recorded and shown to you. By default (`aiChat.reviewEdits`) edits apply at once and you **Keep** or **Undo** them afterwards, per change, per file or all; turn it off to make each edit wait for **Accept** before it is written. Undo is refused when you changed the same lines since, so your work is never overwritten. Use Agent/Auto on code under version control. |
| Approvals | Commands always wait for your approval in **Agent** mode; **Auto** mode runs only the safe ones on `Agent:AutoApproveCommands` (build, test, `git status`…) by itself. |
| Commands | No shell: one allow-listed program per call (`dotnet build`, `git status`, detected test/build commands...), arguments passed as-is, so `&&`, pipes, redirects and `$(...)` can't chain commands. Options that write files or run other programs are blocked. |
| Network | The backend listens on localhost only and requires a random token on every request. |
| Plans | Plan mode can only read files; it writes nothing but its own plan file in `.ai/plans/`. |
| MCP servers | Configured in **user settings only** (`aiChat.mcpServers`), so an opened project can't add programs to run. Every MCP tool call asks first (also in Auto mode) unless you mark a server as trusted. Servers start in the project folder and get your normal environment **without** the backend's configuration: no model API keys, no agent token. Their tokens are kept in secret storage. A server runs with your permissions: only add servers you trust. |
| Web | `web_fetch` only reaches public addresses: loopback, private, link-local and cloud-metadata ranges (IPv4 and IPv6, including mapped/NAT64 forms) are refused when the connection is made, on the address actually used, and on every redirect; no proxy is used. It asks before each fetch by default, because a hijacked prompt could try to send data out in a URL. `web_search` sends only the query to the provider you chose. |
| Checks (verify loop) | After the agent changed files, Stoat runs the project's build and tests: its detected commands (`dotnet`, `npm` scripts, `cargo`, `go`, Maven, Gradle, `pytest`) or the ones you configured. They are **your project's own scripts**, so they run only in workspaces VS Code trusts, without a shell, with time limits, and Stop kills them. Turn it off with `aiChat.verify.enabled`. |
| Autocomplete | Off by default. When on, the code around the cursor (a few thousand characters) goes directly from VS Code to the provider you picked for it. |

## What it does NOT protect against

- **Prompt injection from your own files**: a file you open could contain text that tries to steer the model.
  The model is told to treat file contents as data, and the approval step is your final check. Review changes before accepting.
- **Commands you add to `Agent:AllowedCommands`**: an allowed test script runs with your permissions.
- **What you send to the model provider**: code you ask about is sent to the provider you configured (DeepSeek, Anthropic, OpenAI...).
  Use a local model (Ollama) if code must not leave your machine.

## API keys

Keys are stored in VS Code SecretStorage (extension) or .NET user-secrets (backend), never in config files in the repo.

## Reporting a vulnerability

Please **do not open a public issue**. Email **alialalawi14@gmail.com** with steps to reproduce.
You'll get an answer within 7 days.
