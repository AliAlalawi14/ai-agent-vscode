import * as vscode from "vscode";
import { PROVIDER_KEYS } from "../services/BackendProcess";
import { providerSecretKey } from "../services/providerPresets";
import { readProviders } from "../services/providerSetup";
import {
  CompletionEngine,
  buildContext,
  detectStyle,
  shouldComplete,
  type CompletionContext,
  type CompletionEndpoint,
  type FimStyle,
} from "./core";

/** A provider autocomplete can use, as shown in Settings → Autocomplete. */
export interface CompletionProviderOption {
  id: string;
  label: string;
  models: string[];
  detectedStyle: FimStyle;
}

const DEEPSEEK = { id: "deepseek", baseUrl: "https://api.deepseek.com/beta", model: "deepseek-chat" };

function settings() {
  const c = vscode.workspace.getConfiguration("aiChat.completion");
  return {
    enabled: c.get<boolean>("enabled", false),
    provider: c.get<string>("provider", "") ?? "",
    model: c.get<string>("model", "") ?? "",
    style: (c.get<string>("style", "auto") ?? "auto") as FimStyle | "auto",
    debounceMs: Math.max(50, c.get<number>("debounceMs", 300)),
    maxLines: Math.max(1, c.get<number>("maxLines", 8)),
  };
}

/** Providers that can serve completions (Claude has no fill-in-the-middle or OpenAI-style API, so it isn't listed). */
export async function completionProviders(secrets: vscode.SecretStorage): Promise<CompletionProviderOption[]> {
  const options: CompletionProviderOption[] = [];
  const deepseek = PROVIDER_KEYS.find((p) => p.id === "deepseek");
  if (deepseek && (await secrets.get(deepseek.secret))) {
    options.push({ id: DEEPSEEK.id, label: "DeepSeek", models: [DEEPSEEK.model], detectedStyle: "completions" });
  }
  for (const p of readProviders()) {
    options.push({ id: p.name, label: `${p.name} (${p.baseUrl.replace(/^https?:\/\//, "")})`, models: p.models, detectedStyle: detectStyle(p.baseUrl, p.name) });
  }
  return options;
}

/** Where to send completions, with the key; null when autocomplete isn't set up. */
export async function resolveEndpoint(secrets: vscode.SecretStorage, override?: { provider: string; model: string; style: FimStyle | "auto" }): Promise<CompletionEndpoint | null> {
  const s = override ?? settings();
  if (!s.provider) { return null; }

  if (s.provider === DEEPSEEK.id) {
    const key = await secrets.get(PROVIDER_KEYS.find((p) => p.id === "deepseek")!.secret);
    if (!key) { return null; }
    return {
      style: s.style === "auto" ? "completions" : s.style,
      baseUrl: DEEPSEEK.baseUrl,
      model: s.model || DEEPSEEK.model,
      headers: { Authorization: `Bearer ${key}` },
    };
  }

  const provider = readProviders().find((p) => p.name === s.provider);
  if (!provider) { return null; }
  const key = provider.auth === "none" ? undefined : await secrets.get(providerSecretKey(provider.name));
  const headers: Record<string, string> = {};
  if (key && provider.auth === "bearer") { headers.Authorization = `Bearer ${key}`; }
  if (key && provider.auth === "api-key") { headers["api-key"] = key; }
  return {
    style: s.style === "auto" ? detectStyle(provider.baseUrl, provider.name) : s.style,
    baseUrl: provider.baseUrl,
    model: s.model || provider.models[0] || "",
    headers,
  };
}

/**
 * Ghost-text suggestions as you type. Off until the user picks a provider and model in Settings → Autocomplete.
 * Requests go straight from the extension to the provider (not through the agent backend) so they are fast;
 * every keystroke cancels the previous request, and repeated failures pause it for a minute.
 */
export class StoatInlineProvider implements vscode.InlineCompletionItemProvider, vscode.Disposable {
  private engine: CompletionEngine;
  private readonly status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  private snoozedUntil = 0;
  private failures = 0;
  private pausedUntil = 0;
  private lastError: string | null = null;
  private busy = false;
  private readonly disposables: vscode.Disposable[] = [];

  constructor(private readonly context: vscode.ExtensionContext) {
    this.engine = new CompletionEngine(fetch, { maxLines: settings().maxLines, maxTokens: 128, temperature: 0.1 });
    this.status.command = "aiChat.completion.menu";
    this.disposables.push(
      vscode.languages.registerInlineCompletionItemProvider({ pattern: "**" }, this),
      vscode.commands.registerCommand("aiChat.completion.menu", () => this.menu()),
      vscode.commands.registerCommand("aiChat.completion.toggle", () => this.setEnabled(!settings().enabled)),
      vscode.workspace.onDidChangeConfiguration((e) => {
        if (e.affectsConfiguration("aiChat.completion") || e.affectsConfiguration("aiChat.providers")) {
          this.engine = new CompletionEngine(fetch, { maxLines: settings().maxLines, maxTokens: 128, temperature: 0.1 });
          this.failures = 0;
          this.pausedUntil = 0;
          this.lastError = null;
          this.render();
        }
      }),
      this.status,
    );
    this.render();
  }

  async provideInlineCompletionItems(
    document: vscode.TextDocument,
    position: vscode.Position,
    context: vscode.InlineCompletionContext,
    token: vscode.CancellationToken,
  ): Promise<vscode.InlineCompletionItem[] | undefined> {
    const s = settings();
    const now = Date.now();
    if (!s.enabled || now < this.snoozedUntil || now < this.pausedUntil) { return undefined; }
    if (!["file", "untitled", "vscode-notebook-cell"].includes(document.uri.scheme)) { return undefined; }
    if (context.selectedCompletionInfo) { return undefined; }            // the suggest widget is open
    if (document.getText().length > 1_000_000) { return undefined; }

    const ctx: CompletionContext = buildContext(
      document.getText(),
      document.offsetAt(position),
      document.languageId,
      vscode.workspace.asRelativePath(document.uri),
    );
    if (!shouldComplete(ctx)) { return undefined; }

    // Wait for a pause in typing; a new keystroke cancels this call
    await new Promise((r) => setTimeout(r, context.triggerKind === vscode.InlineCompletionTriggerKind.Invoke ? 0 : s.debounceMs));
    if (token.isCancellationRequested) { return undefined; }

    const endpoint = await resolveEndpoint(this.context.secrets);
    if (!endpoint || !endpoint.model) { return undefined; }

    const abort = new AbortController();
    const subscription = token.onCancellationRequested(() => abort.abort());
    this.busy = true;
    this.render();
    try {
      const result = await this.engine.complete(endpoint, ctx, abort.signal);
      this.failures = 0;
      this.lastError = null;
      if (!result.text || token.isCancellationRequested) { return undefined; }
      return [new vscode.InlineCompletionItem(result.text, new vscode.Range(position, position))];
    } catch (error) {
      if (abort.signal.aborted) { return undefined; }                   // typing continued: not an error
      this.lastError = error instanceof Error ? error.message : String(error);
      if (++this.failures >= 3) { this.pausedUntil = Date.now() + 60_000; }
      return undefined;
    } finally {
      subscription.dispose();
      this.busy = false;
      this.render();
    }
  }

  private render(): void {
    const s = settings();
    if (!s.provider) {
      this.status.hide();
      return;
    }
    const snoozed = Date.now() < this.snoozedUntil;
    const paused = Date.now() < this.pausedUntil;
    this.status.text = !s.enabled || snoozed
      ? "$(circle-slash) Stoat"
      : this.busy ? "$(loading~spin) Stoat" : this.lastError ? "$(warning) Stoat" : "$(sparkle) Stoat";
    this.status.tooltip = !s.enabled
      ? "Stoat autocomplete is off (click to turn on)"
      : snoozed ? "Stoat autocomplete is snoozed"
        : paused ? `Stoat autocomplete paused for a minute after errors: ${this.lastError}`
          : this.lastError ? `Last autocomplete error: ${this.lastError}` : `Stoat autocomplete: ${s.model || "model"} (${s.provider})`;
    this.status.show();
  }

  private async setEnabled(on: boolean): Promise<void> {
    await vscode.workspace.getConfiguration("aiChat.completion").update("enabled", on, vscode.ConfigurationTarget.Global);
    this.snoozedUntil = 0;
    this.render();
  }

  private async menu(): Promise<void> {
    const s = settings();
    const items: Array<vscode.QuickPickItem & { run: () => unknown }> = [
      s.enabled
        ? { label: "$(circle-slash) Turn autocomplete off", run: () => this.setEnabled(false) }
        : { label: "$(sparkle) Turn autocomplete on", run: () => this.setEnabled(true) },
      { label: "$(clock) Snooze for 1 hour", run: () => { this.snoozedUntil = Date.now() + 3_600_000; this.render(); } },
      { label: "$(gear) Autocomplete settings…", run: () => vscode.commands.executeCommand("aiChat.openSidebar") },
    ];
    if (this.lastError) { items.push({ label: "$(warning) Last error", detail: this.lastError, run: () => undefined }); }
    const pick = await vscode.window.showQuickPick(items, { placeHolder: `Stoat autocomplete (${s.provider || "not set up"})` });
    await pick?.run();
  }

  dispose(): void {
    this.disposables.forEach((d) => d.dispose());
  }
}
