import * as vscode from "vscode";
import * as cp from "child_process";
import * as crypto from "crypto";
import * as fs from "fs";
import * as net from "net";
import * as path from "path";
import { providerSecretKey, type ProviderEntry } from "./providerPresets";
import { SECRET_PREFIX, resolveSecrets, toBackendJson, type McpServers } from "./mcpConfig";

/** Provider keys kept in VS Code SecretStorage and handed to the backend as environment variables. */
export const PROVIDER_KEYS = [
  { id: "deepseek", label: "DeepSeek", secret: "aiChat.key.deepseek", env: "DeepSeek__ApiKey" },
  { id: "anthropic", label: "Anthropic (Claude)", secret: "aiChat.key.anthropic", env: "Anthropic__ApiKey" },
] as const;   // every other provider (Gemini, OpenAI, Mistral, Ollama...) is added with "Stoat: Add Provider"

export const WEB_SEARCH_KEY_SECRET = "aiChat.web.searchKey";

/** Where the backend is and how to authenticate; either the managed process or an external URL from settings. */
export interface BackendConnection {
  url: string;
  token: string;
}

/**
 * Runs the agent backend for this window (zero-config install): a free localhost port, a random token that
 * never leaves memory, the open folders as its workspaces, keys from SecretStorage, and memory in a SQLite file
 * under the extension's global storage. Not used when `aiChat.backendUrl` points at an external backend.
 */
export class BackendProcess implements vscode.Disposable {
  private process: cp.ChildProcess | null = null;
  private connection: BackendConnection | null = null;
  private starting: Promise<BackendConnection | null> | null = null;
  private readonly output = vscode.window.createOutputChannel("Stoat Backend");
  private readonly started = new vscode.EventEmitter<void>();
  /** Fires when a (re)start succeeded, so open chat panels refresh their status and model list. */
  readonly onDidStart = this.started.event;
  /** Why the last start failed (shown in the chat panel); null after a successful start. */
  lastProblem: string | null = null;

  constructor(private readonly context: vscode.ExtensionContext) {}

  /** True when settings point at an external backend (then this class does nothing). */
  static isExternal(): boolean {
    return !!vscode.workspace.getConfiguration("aiChat").get<string>("backendUrl", "")?.trim();
  }

  /** The connection to use for requests: the external backend from settings, or this window's managed one. */
  current(): BackendConnection | null {
    if (BackendProcess.isExternal()) {
      const config = vscode.workspace.getConfiguration("aiChat");
      return { url: config.get<string>("backendUrl", "")!.trim(), token: config.get<string>("apiToken", "") };
    }
    return this.connection;
  }

  showLog(): void {
    this.output.show(true);
  }

  /** Starts the backend if needed (idempotent while starting). Null when it can't start; the reason is shown. */
  async ensureStarted(): Promise<BackendConnection | null> {
    if (BackendProcess.isExternal()) {return this.current();}
    if (this.connection && this.process && this.process.exitCode === null) {return this.connection;}
    this.starting ??= this.start().finally(() => (this.starting = null));
    return this.starting;
  }

  async restart(): Promise<BackendConnection | null> {
    this.stop();
    return this.ensureStarted();
  }

  /** Restarts only a backend that is running (a settings change must not start one nobody asked for). */
  async restartIfRunning(): Promise<void> {
    if (this.process && this.process.exitCode === null) {
      await this.restart();
    }
  }

  /**
   * True when no model provider is configured, so the backend can't start; the chat panel then shows its
   * setup form. Never true for an external backend (it has its own configuration).
   */
  async needsSetup(): Promise<boolean> {
    if (BackendProcess.isExternal()) {return false;}
    for (const provider of PROVIDER_KEYS) {
      if (await this.context.secrets.get(provider.secret)) {return false;}
    }
    const openai = vscode.workspace.getConfiguration("aiChat.openai");
    if (openai.get<string>("baseUrl", "")?.trim() && (openai.get<string[]>("models", []) ?? []).length > 0) {return false;}
    const providers = vscode.workspace.getConfiguration("aiChat").get<ProviderEntry[]>("providers", []) ?? [];
    for (const provider of providers) {
      if (provider.auth === "none" || (await this.context.secrets.get(providerSecretKey(provider.name)))) {return false;}
    }
    return true;
  }

  // No awaited notifications in here: every chat request and health check waits on this start, so a
  // notification the user never clicks would leave the chat "Thinking..." forever. The panel shows lastProblem.
  private async start(): Promise<BackendConnection | null> {
    const exe = this.findExecutable();
    if (!exe) {
      this.lastProblem =
        "The backend binary for this platform was not found. Reinstall the extension, " +
        "or point 'aiChat.backendUrl' at a backend you run yourself.";
      this.output.appendLine(this.lastProblem);
      return null;
    }

    // macOS/Linux: a VSIX packed on Windows (or unpacked without modes) leaves the binary non-executable
    if (process.platform !== "win32") {
      try { fs.chmodSync(exe, 0o755); } catch { /* read-only install: spawn reports the real error */ }
    }

    const env = await this.environment();
    if (!env) {
      this.lastProblem = "No model provider is set up yet. Add one in the chat panel.";
      return null;
    }

    const port = await freePort();
    const token = crypto.randomBytes(24).toString("hex");
    const url = `http://127.0.0.1:${port}`;
    Object.assign(env, { ASPNETCORE_URLS: url, Agent__ApiToken: token });

    this.output.appendLine(`Starting ${exe} on ${url}`);
    const child = cp.spawn(exe, [], { cwd: path.dirname(exe), env: { ...process.env, ...env }, windowsHide: true });
    child.stdout?.on("data", (d) => this.output.append(d.toString()));
    child.stderr?.on("data", (d) => this.output.append(d.toString()));
    child.on("exit", (code) => {
      this.output.appendLine(`Backend exited (code ${code}).`);
      if (this.process === child) {
        this.process = null;
        this.connection = null;
      }
    });
    this.process = child;

    if (!(await waitForHealth(url, token, child))) {
      this.stop();
      this.lastProblem = "The backend didn't start. Open the backend log for the reason.";
      return null;
    }
    this.connection = { url, token };
    this.lastProblem = null;
    this.output.appendLine("Backend ready.");
    this.started.fire();
    return this.connection;
  }

  stop(): void {
    const child = this.process;
    this.process = null;
    this.connection = null;
    if (child && child.exitCode === null) {
      if (process.platform === "win32" && child.pid) {
        // A kill on Windows skips the backend's shutdown code: end the whole tree (MCP servers, commands) with it
        cp.spawn("taskkill", ["/pid", String(child.pid), "/T", "/F"], { windowsHide: true }).on("error", () => child.kill());
      } else {
        // SIGTERM: the backend shuts down gracefully and stops its MCP servers and commands
        child.kill();
      }
    }
  }

  dispose(): void {
    this.stop();
    this.started.dispose();
    this.output.dispose();
  }

  /** `aiChat.backendPath`, else the binary bundled for this platform in <extension>/server/<rid>/. */
  private findExecutable(): string | null {
    const configured = vscode.workspace.getConfiguration("aiChat").get<string>("backendPath", "")?.trim();
    const name = process.platform === "win32" ? "Ai-Agent.exe" : "Ai-Agent";
    const candidates = configured
      ? [configured]
      : [path.join(this.context.extensionPath, "server", runtimeId(), name)];
    return candidates.find((c) => fs.existsSync(c)) ?? null;
  }

  /** Environment for the backend; null when no provider is configured yet (the chat panel asks for one). */
  private async environment(): Promise<Record<string, string> | null> {
    const env: Record<string, string> = {
      ASPNETCORE_ENVIRONMENT: "Production",
      Database__Provider: "sqlite",
      Database__SqlitePath: path.join(this.context.globalStorageUri.fsPath, "agent.db"),
    };
    fs.mkdirSync(this.context.globalStorageUri.fsPath, { recursive: true });

    const folders = vscode.workspace.workspaceFolders ?? [];
    if (folders.length > 0) {env.Agent__WorkspaceRoot = folders[0].uri.fsPath;}
    folders.forEach((f, i) => (env[`Agent__AllowedWorkspaces__${i}`] = f.uri.fsPath));

    let anyKey = false;
    for (const provider of PROVIDER_KEYS) {
      const key = await this.context.secrets.get(provider.secret);
      if (key) {
        env[provider.env] = key;
        anyKey = true;
      }
    }

    // OpenAI-compatible endpoint (incl. a local Ollama, which needs no key)
    const openai = vscode.workspace.getConfiguration("aiChat.openai");
    const baseUrl = openai.get<string>("baseUrl", "")?.trim();
    const models = openai.get<string[]>("models", []) ?? [];
    if (baseUrl && models.length > 0) {
      env.OpenAI__BaseUrl = baseUrl;
      env.OpenAI__ProviderName = openai.get<string>("providerName", "openai") || "openai";
      models.forEach((m, i) => (env[`OpenAI__Models__${i}__Id`] = m));
      if (openai.get<boolean>("useMaxCompletionTokens", false)) {env.OpenAI__UseMaxCompletionTokens = "true";}
      anyKey = true;
    }

    // Providers added with "Stoat: Add Provider" (Gemini, Mistral, Azure, Ollama...): Providers__Custom__N__*
    const providers = vscode.workspace.getConfiguration("aiChat").get<ProviderEntry[]>("providers", []) ?? [];
    let index = 0;
    for (const provider of providers) {
      const key = provider.auth === "none" ? undefined : await this.context.secrets.get(providerSecretKey(provider.name));
      if (provider.auth !== "none" && !key) {
        this.output.appendLine(`Skipping provider '${provider.name}': its key is missing (run 'Stoat: Add Provider' again).`);
        continue;
      }
      const prefix = `Providers__Custom__${index++}__`;
      env[`${prefix}Name`] = provider.name;
      env[`${prefix}BaseUrl`] = provider.baseUrl;
      env[`${prefix}Auth`] = provider.auth;
      if (key) {env[`${prefix}ApiKey`] = key;}
      if (provider.useMaxCompletionTokens) {env[`${prefix}UseMaxCompletionTokens`] = "true";}
      provider.models.forEach((m, i) => (env[`${prefix}Models__${i}__Id`] = m));
      anyKey = true;
    }

    // Verify loop: custom check commands (empty = the backend detects them from the project)
    const checks = vscode.workspace.getConfiguration("aiChat.verify").get<string[]>("commands", []) ?? [];
    checks.map((c) => c.trim()).filter(Boolean).forEach((c, i) => (env[`Agent__VerifyCommands__${i}`] = c));

    // Web tools: fetch mode and the search provider (its key from secret storage)
    const web = vscode.workspace.getConfiguration("aiChat.web");
    env.Web__Fetch = web.get<string>("fetch", "ask") || "ask";
    const searchProvider = web.get<string>("searchProvider", "") ?? "";
    if (searchProvider) {
      env.Web__SearchProvider = searchProvider;
      const searchKey = await this.context.secrets.get(WEB_SEARCH_KEY_SECRET);
      if (searchKey) { env.Web__SearchApiKey = searchKey; }
      const searxng = web.get<string>("searxngUrl", "")?.trim();
      if (searxng) { env.Web__SearxngUrl = searxng; }
    }

    // MCP servers: user settings only (a cloned project can't add programs to run), secrets resolved here
    const mcp = mcpServersSetting();
    if (Object.keys(mcp).length > 0) {
      const { servers, missing } = await resolveSecrets(mcp, async (name) => this.context.secrets.get(SECRET_PREFIX + name));
      for (const [server, names] of Object.entries(missing)) {
        this.output.appendLine(`MCP server '${server}': missing value for ${names.join(", ")} (set it in Settings → MCP servers).`);
      }
      env.Mcp__ServersJson = toBackendJson(servers);
    }

    const defaultProvider = vscode.workspace.getConfiguration("aiChat").get<string>("defaultProvider", "")?.trim();
    if (defaultProvider) {env.LLM__DefaultProvider = defaultProvider;}

    if (!anyKey) {
      return null;
    }
    return env;
  }
}

/** .NET runtime identifier of this machine: the folder the bundled backend lives in. */
export function runtimeId(): string {
  const os = process.platform === "win32" ? "win" : process.platform === "darwin" ? "osx" : "linux";
  return `${os}-${process.arch === "arm64" ? "arm64" : "x64"}`;
}

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const port = (server.address() as net.AddressInfo).port;
      server.close(() => resolve(port));
    });
  });
}

/** Polls /api/Health until the backend answers (or exits, or 60 s pass). */
async function waitForHealth(url: string, token: string, child: cp.ChildProcess): Promise<boolean> {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) {
      return false;
    }
    try {
      const response = await fetch(`${url}/api/Health`, { headers: { "X-Agent-Token": token } });
      if (response.ok) {
        return true;
      }
    } catch {
      // not listening yet
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  return false;
}

/** The configured MCP servers, from user settings only (workspace values are ignored on purpose). */
export function mcpServersSetting(): McpServers {
  return vscode.workspace.getConfiguration("aiChat").inspect<McpServers>("mcpServers")?.globalValue ?? {};
}
