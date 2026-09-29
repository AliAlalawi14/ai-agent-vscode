import * as vscode from "vscode";
import * as fs from "fs/promises";
import * as os from "os";
import type { ExtensionMessage, McpServerEntryView, McpServerView, McpState, WebviewMessage } from "../shared/protocol";
import { agentApiClient } from "./AgentApiClient";
import { getBackendProcess } from "./backendConnection";
import { mcpServersSetting } from "./BackendProcess";
import {
  MCP_PRESETS,
  SECRET_PREFIX,
  extractSecrets,
  importSources,
  mergeServers,
  parseMcpFile,
  sanitizeServerName,
  secretRefs,
  validateServer,
  type McpServerEntry,
  type McpServers,
} from "./mcpConfig";

type McpMessage = Extract<
  WebviewMessage,
  { type: "getMcp" | "saveMcpServer" | "removeMcpServer" | "setMcpServer" | "setMcpSecret" | "importMcp" | "addMcpPreset" }
>;

/**
 * Settings → MCP servers: reads the `aiChat.mcpServers` user setting and the backend's live status, and applies
 * the panel's changes. Every change is written to the setting; the extension restarts the backend when it
 * changes, and the new state is sent once the backend is back (MessageBroker listens for the restart).
 */
export class McpPanelService {
  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly post: (message: ExtensionMessage) => void,
  ) {}

  async handle(message: McpMessage): Promise<void> {
    try {
      switch (message.type) {
        case "getMcp":
          break;
        case "saveMcpServer":
          await this.save(message.name, message.entry, message.secretKeys, message.originalName);
          break;
        case "removeMcpServer":
          await this.remove(message.name);
          break;
        case "setMcpServer":
          await this.patch(message.name, message);
          break;
        case "setMcpSecret":
          await this.context.secrets.store(SECRET_PREFIX + message.name, message.value);
          this.post({ type: "mcpResult", ok: true, message: "Saved. Restarting the server…" });
          await getBackendProcess()?.restartIfRunning();
          break;
        case "importMcp":
          await this.import(message.sourceId);
          break;
        case "addMcpPreset":
          await this.addPreset(message.presetId, message.values);
          break;
      }
    } catch (error) {
      this.post({ type: "mcpResult", ok: false, message: error instanceof Error ? error.message : String(error) });
    }
    await this.postState();
  }

  async postState(): Promise<void> {
    this.post({ type: "mcpState", state: await this.state() });
  }

  private async state(): Promise<McpState> {
    const config = mcpServersSetting();
    const backend = getBackendProcess();
    const running = !!backend?.current();
    const status = running ? await agentApiClient.getMcpStatus().catch(() => null) : null;

    const servers: McpServerView[] = [];
    for (const [name, entry] of Object.entries(config)) {
      const live = status?.servers.find((s) => s.name.toLowerCase() === name.toLowerCase());
      const missingSecrets: string[] = [];
      for (const ref of secretRefs(entry)) {
        if ((await this.context.secrets.get(SECRET_PREFIX + ref)) === undefined) { missingSecrets.push(ref); }
      }
      servers.push({
        name,
        entry,
        transport: entry.url ? "http" : "stdio",
        target: entry.url ?? [entry.command, ...(entry.args ?? [])].join(" "),
        state: entry.disabled ? "disabled" : live?.state ?? (running ? "starting" : "stopped"),
        ...(live?.error ? { error: live.error } : {}),
        tools: live?.tools ?? [],
        promptTokens: live?.promptTokens ?? 0,
        missingSecrets,
      });
    }

    const home = os.homedir();
    const sources = importSources({
      platform: process.platform,
      home,
      ...(process.env.APPDATA ? { appData: process.env.APPDATA } : {}),
      ...(vscode.workspace.workspaceFolders?.[0] ? { workspace: vscode.workspace.workspaceFolders[0].uri.fsPath } : {}),
    });
    const importable = await Promise.all(
      sources.map(async (s) => ({ id: s.id, label: s.label, available: await fs.stat(s.path).then(() => true, () => false) })),
    );

    return {
      servers,
      configErrors: status?.configErrors ?? [],
      importSources: importable,
      presets: MCP_PRESETS.map((p) => ({
        id: p.id,
        label: p.label,
        detail: p.detail,
        added: !!config[p.name],
        ...(p.needs ? { needs: p.needs.map((n) => ({ key: n.key, label: n.label, ...(n.url ? { url: n.url } : {}) })) } : {}),
      })),
      backendRunning: running,
    };
  }

  private async write(servers: McpServers): Promise<void> {
    await vscode.workspace.getConfiguration("aiChat").update("mcpServers", servers, vscode.ConfigurationTarget.Global);
  }

  /** Stores credential values in SecretStorage and keeps only `${secret:…}` references in the setting. */
  private async storeSecrets(name: string, entry: McpServerEntry, secretKeys: string[]): Promise<McpServerEntry> {
    const forced = new Set(secretKeys);
    const moved: McpServerEntry = { ...entry };
    const secrets: Record<string, string> = {};
    const force = (map: Record<string, string> | undefined, kind: "env" | "header") => {
      if (!map) { return map; }
      const out: Record<string, string> = {};
      for (const [key, value] of Object.entries(map)) {
        if (forced.has(`${kind}:${key}`) && value && !/\$\{secret:/.test(value)) {
          const secretName = `${name}_${key}`.replace(/[^A-Za-z0-9_.-]/g, "_");
          secrets[secretName] = value;
          out[key] = `\${secret:${secretName}}`;
        } else {
          out[key] = value;
        }
      }
      return out;
    };
    if (moved.env) { moved.env = force(moved.env, "env"); }
    if (moved.headers) { moved.headers = force(moved.headers, "header"); }
    const auto = extractSecrets(name, moved);   // anything else that looks like a credential
    for (const [secretName, value] of Object.entries({ ...secrets, ...auto.secrets })) {
      await this.context.secrets.store(SECRET_PREFIX + secretName, value);
    }
    return auto.entry;
  }

  private async save(rawName: string, view: McpServerEntryView, secretKeys: string[], originalName?: string): Promise<void> {
    const name = sanitizeServerName(rawName);
    if (!name) { throw new Error("Give the server a name (letters, digits, '-' or '_')."); }
    const entry: McpServerEntry = clean(view);
    const problem = validateServer(entry);
    if (problem) { throw new Error(problem); }

    const servers = { ...mcpServersSetting() };
    if (originalName && originalName !== name) { delete servers[originalName]; }
    else if (!originalName && servers[name]) { throw new Error(`A server named '${name}' already exists.`); }
    servers[name] = await this.storeSecrets(name, entry, secretKeys);
    await this.write(servers);
    this.post({ type: "mcpResult", ok: true, message: `Saved '${name}'. Starting it…` });
  }

  private async remove(name: string): Promise<void> {
    const servers = { ...mcpServersSetting() };
    const entry = servers[name];
    if (!entry) { return; }
    for (const ref of secretRefs(entry)) { await this.context.secrets.delete(SECRET_PREFIX + ref); }
    delete servers[name];
    await this.write(servers);
    this.post({ type: "mcpResult", ok: true, message: `Removed '${name}'.` });
  }

  private async patch(name: string, change: { disabled?: boolean; alwaysAllow?: boolean; disabledTools?: string[] }): Promise<void> {
    const servers = { ...mcpServersSetting() };
    const entry = servers[name];
    if (!entry) { throw new Error(`No server named '${name}'.`); }
    const next: McpServerEntry = { ...entry };
    if (change.disabled !== undefined) { if (change.disabled) { next.disabled = true; } else { delete next.disabled; } }
    if (change.alwaysAllow !== undefined) { if (change.alwaysAllow) { next.alwaysAllow = true; } else { delete next.alwaysAllow; } }
    if (change.disabledTools !== undefined) {
      if (change.disabledTools.length > 0) { next.disabledTools = change.disabledTools; } else { delete next.disabledTools; }
    }
    servers[name] = next;
    await this.write(servers);
  }

  private async import(sourceId: string): Promise<void> {
    const source = importSources({
      platform: process.platform,
      home: os.homedir(),
      ...(process.env.APPDATA ? { appData: process.env.APPDATA } : {}),
      ...(vscode.workspace.workspaceFolders?.[0] ? { workspace: vscode.workspace.workspaceFolders[0].uri.fsPath } : {}),
    }).find((s) => s.id === sourceId);
    if (!source) { throw new Error("Unknown import source."); }

    const text = await fs.readFile(source.path, "utf8").catch(() => { throw new Error(`${source.label}: ${source.path} not found.`); });
    const parsed = parseMcpFile(text);
    const incoming: McpServers = {};
    for (const [name, entry] of Object.entries(parsed.servers)) {
      const { entry: safe, secrets } = extractSecrets(name, entry);
      for (const [secretName, value] of Object.entries(secrets)) { await this.context.secrets.store(SECRET_PREFIX + secretName, value); }
      incoming[name] = safe;
    }
    const { merged, added, skipped } = mergeServers(mcpServersSetting(), incoming);
    if (added.length > 0) { await this.write(merged); }

    const parts = [
      added.length > 0 ? `Imported ${added.join(", ")}` : "Nothing new to import",
      skipped.length > 0 ? `already here: ${skipped.join(", ")}` : "",
      parsed.errors.length > 0 ? `skipped: ${parsed.errors.join("; ")}` : "",
    ].filter(Boolean);
    this.post({ type: "mcpResult", ok: added.length > 0 || parsed.errors.length === 0, message: `${source.label}: ${parts.join(" · ")}.` });
  }

  private async addPreset(presetId: string, values: Record<string, string>): Promise<void> {
    const preset = MCP_PRESETS.find((p) => p.id === presetId);
    if (!preset) { throw new Error("Unknown preset."); }
    const entry: McpServerEntry = JSON.parse(JSON.stringify(preset.entry));
    const secretKeys: string[] = [];
    for (const need of preset.needs ?? []) {
      const value = values[need.key]?.trim();
      if (!value) { throw new Error(`${need.label} is required.`); }
      const map = need.where === "header" ? (entry.headers ??= {}) : (entry.env ??= {});
      map[need.key] = (need.prefix ?? "") + value;
      secretKeys.push(`${need.where}:${need.key}`);
    }
    await this.save(preset.name, entry, secretKeys);
  }
}

/** Drops empty fields from the panel's form. */
function clean(view: McpServerEntryView): McpServerEntry {
  const entry: McpServerEntry = {};
  if (view.url?.trim()) {
    entry.url = view.url.trim();
    const headers = nonEmpty(view.headers);
    if (headers) { entry.headers = headers; }
  } else if (view.command?.trim()) {
    entry.command = view.command.trim();
    const args = (view.args ?? []).filter((a) => a.length > 0);
    if (args.length > 0) { entry.args = args; }
    const env = nonEmpty(view.env);
    if (env) { entry.env = env; }
  }
  if (view.disabled) { entry.disabled = true; }
  if (view.alwaysAllow) { entry.alwaysAllow = view.alwaysAllow; }
  if (view.disabledTools?.length) { entry.disabledTools = view.disabledTools; }
  return entry;
}

function nonEmpty(map?: Record<string, string>): Record<string, string> | undefined {
  if (!map) { return undefined; }
  const out = Object.fromEntries(Object.entries(map).filter(([k]) => k.trim().length > 0).map(([k, v]) => [k.trim(), v]));
  return Object.keys(out).length > 0 ? out : undefined;
}
