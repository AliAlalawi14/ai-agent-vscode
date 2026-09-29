import * as vscode from "vscode";
import * as cp from "child_process";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import type { ExtensionMessage } from "../shared/protocol";
import { OLLAMA_HOST, hasModel, ollamaStatus, pullModel, recommendLocalModel } from "./local";
import { CompletionEngine, buildContext } from "./core";
import { LOCAL_PROVIDER_ID, resolveEndpoint } from "./inlineProvider";

export interface LocalAutocompleteState {
  ollama: "running" | "stopped" | "missing";
  version?: string;
  ramGb: number;
  recommended: { name: string; sizeGb: number; why: string };
  installed: boolean;
  /** Autocomplete already runs on this model locally */
  active: boolean;
}

/**
 * Settings → Autocomplete → "Run on this computer": checks Ollama, recommends a small fill-in-the-middle model for
 * this machine, downloads it with progress, then switches autocomplete to it and tries one completion. Once the
 * model is downloaded, suggestions never leave the computer and work offline.
 */
export class LocalAutocompleteService {
  private pull: AbortController | null = null;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly post: (message: ExtensionMessage) => void,
  ) {}

  async state(): Promise<LocalAutocompleteState> {
    const ramGb = Math.round(os.totalmem() / 1024 ** 3);
    const recommended = recommendLocalModel(ramGb, process.platform === "darwin" && process.arch === "arm64");
    const status = await ollamaStatus();
    const c = vscode.workspace.getConfiguration("aiChat.completion");
    const installed = status.running && hasModel(status.models, recommended.name);
    return {
      ollama: status.running ? "running" : findOllama() ? "stopped" : "missing",
      ...(status.version ? { version: status.version } : {}),
      ramGb,
      recommended,
      installed,
      active: c.get<boolean>("enabled", false) && c.get<string>("provider", "") === LOCAL_PROVIDER_ID,
    };
  }

  async postState(): Promise<void> {
    this.post({ type: "localAutocomplete", state: await this.state() });
  }

  /** Starts `ollama serve` in the background when Ollama is installed but not running. */
  async start(): Promise<void> {
    const exe = findOllama();
    if (!exe) { return this.postState(); }
    const child = cp.spawn(exe, ["serve"], { detached: true, stdio: "ignore", windowsHide: true });
    child.on("error", () => undefined);
    child.unref();
    for (let i = 0; i < 20; i++) {
      await new Promise((r) => setTimeout(r, 500));
      if ((await ollamaStatus()).running) { break; }
    }
    await this.postState();
  }

  /** Download (if needed) → switch autocomplete to the local model → try one completion. */
  async setup(model: string): Promise<void> {
    this.pull?.abort();
    const abort = new AbortController();
    this.pull = abort;
    try {
      const status = await ollamaStatus();
      if (!status.running) { throw new Error("Ollama isn't running. Start it, then try again."); }
      if (!hasModel(status.models, model)) {
        await pullModel(model, (p) => this.post({ type: "localPull", status: p.status, ...(p.percent !== undefined ? { percent: p.percent } : {}) }), abort.signal);
      }

      const c = vscode.workspace.getConfiguration("aiChat.completion");
      await c.update("provider", LOCAL_PROVIDER_ID, vscode.ConfigurationTarget.Global);
      await c.update("model", model, vscode.ConfigurationTarget.Global);
      await c.update("style", "auto", vscode.ConfigurationTarget.Global);
      await c.update("enabled", true, vscode.ConfigurationTarget.Global);

      // First request loads the model into memory: allow it time, then report the real speed
      const endpoint = await resolveEndpoint(this.context.secrets);
      const sample = "function isEven(n: number): boolean {\n  return \n}\n";
      const ctx = buildContext(sample, sample.indexOf("return ") + "return ".length, "typescript", "sample.ts");
      const engine = new CompletionEngine();
      await engine.complete(endpoint!, ctx).catch(() => undefined);   // warm-up (loads the model)
      const result = await new CompletionEngine().complete(endpoint!, ctx);   // fresh engine: a real, timed request
      this.post({ type: "localDone", ok: true, text: result.text, ms: result.ms });
    } catch (error) {
      const message = abort.signal.aborted ? "Download cancelled." : error instanceof Error ? error.message : String(error);
      this.post({ type: "localDone", ok: false, error: message });
    } finally {
      if (this.pull === abort) { this.pull = null; }
      await this.postState();
    }
  }

  cancel(): void {
    this.pull?.abort();
  }
}

/** The Ollama program, if installed (PATH or its usual install location). */
function findOllama(): string | null {
  const name = process.platform === "win32" ? "ollama.exe" : "ollama";
  const candidates = [
    ...(process.env.PATH ?? "").split(path.delimiter).map((d) => path.join(d.replace(/"/g, ""), name)),
    ...(process.platform === "win32" && process.env.LOCALAPPDATA ? [path.join(process.env.LOCALAPPDATA, "Programs", "Ollama", name)] : []),
    ...(process.platform === "darwin" ? ["/Applications/Ollama.app/Contents/Resources/ollama", "/usr/local/bin/ollama", "/opt/homebrew/bin/ollama"] : []),
    ...(process.platform === "linux" ? ["/usr/local/bin/ollama", "/usr/bin/ollama"] : []),
  ];
  return candidates.find((c) => { try { return fs.statSync(c).isFile(); } catch { return false; } }) ?? null;
}

export { OLLAMA_HOST };
