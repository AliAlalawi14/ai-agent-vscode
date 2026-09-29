import * as vscode from "vscode";
import * as cp from "child_process";
import * as crypto from "crypto";
import * as fs from "fs";
import * as net from "net";
import * as path from "path";
import { providerSecretKey, type ProviderEntry } from "./providerPresets";

/** Provider keys kept in VS Code SecretStorage and handed to the backend as environment variables. */
export const PROVIDER_KEYS = [
  { id: "deepseek", label: "DeepSeek", secret: "aiChat.key.deepseek", env: "DeepSeek__ApiKey" },
  { id: "anthropic", label: "Anthropic (Claude)", secret: "aiChat.key.anthropic", env: "Anthropic__ApiKey" },
] as const;   // every other provider (Gemini, OpenAI, Mistral, Ollama...) is added with "AI Agent: Add Provider"

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
  private readonly output = vscode.window.createOutputChannel("AI Agent Backend");

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

  private async start(): Promise<BackendConnection | null> {
    const exe = this.findExecutable();
    if (!exe) {
      const choice = await vscode.window.showErrorMessage(
        "AI Agent: the backend binary for this platform was not found. Reinstall the extension, " +
          "or point 'aiChat.backendUrl' at a backend you run yourself.",
        "Open Settings",
      );
      if (choice) {void vscode.commands.executeCommand("workbench.action.openSettings", "aiChat.backend");}
      return null;
    }

    const env = await this.environment();
    if (!env) {return null;}   // no API key yet: the user was asked to set one

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
      const choice = await vscode.window.showErrorMessage(
        "AI Agent: the backend didn't start. See the log for the reason.",
        "Show Log",
      );
      if (choice) {this.showLog();}
      return null;
    }
    this.connection = { url, token };
    this.output.appendLine("Backend ready.");
    return this.connection;
  }

  stop(): void {
    const child = this.process;
    this.process = null;
    this.connection = null;
    if (child && child.exitCode === null) {
      // The backend kills its own child processes (build/test commands) on shutdown
      child.kill();
    }
  }

  dispose(): void {
    this.stop();
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

  /** Environment for the backend; null (after asking for a key) when no provider is configured yet. */
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

    // Providers added with "AI Agent: Add Provider" (Gemini, Mistral, Azure, Ollama...): Providers__Custom__N__*
    const providers = vscode.workspace.getConfiguration("aiChat").get<ProviderEntry[]>("providers", []) ?? [];
    let index = 0;
    for (const provider of providers) {
      const key = provider.auth === "none" ? undefined : await this.context.secrets.get(providerSecretKey(provider.name));
      if (provider.auth !== "none" && !key) {
        this.output.appendLine(`Skipping provider '${provider.name}': its key is missing (run 'AI Agent: Add Provider' again).`);
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

    const defaultProvider = vscode.workspace.getConfiguration("aiChat").get<string>("defaultProvider", "")?.trim();
    if (defaultProvider) {env.LLM__DefaultProvider = defaultProvider;}

    if (!anyKey) {
      const choice = await vscode.window.showInformationMessage(
        "AI Agent needs a model provider: a DeepSeek or Claude key, or any other provider (Gemini, OpenAI, Mistral, a local Ollama...).",
        "Set API Key",
        "Add Provider",
      );
      if (choice === "Set API Key") {void vscode.commands.executeCommand("aiChat.setApiKey");}
      if (choice === "Add Provider") {void vscode.commands.executeCommand("aiChat.addProvider");}
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
