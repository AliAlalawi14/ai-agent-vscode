/**
 * MCP server configuration in the extension: the `aiChat.mcpServers` setting (user level only), secrets kept in
 * SecretStorage and referenced as `${secret:NAME}`, and import from the files other tools use.
 * No `vscode` import: this module is unit-tested with plain Node.
 */

export interface McpServerEntry {
  command?: string;
  args?: string[];
  env?: Record<string, string>;
  url?: string;
  headers?: Record<string, string>;
  /** Cursor / Claude Desktop form: the server is configured but not started */
  disabled?: boolean;
  /** true = every tool runs without asking; a list = only these tools */
  alwaysAllow?: boolean | string[];
  /** Tools not offered to the model (keeps the prompt small) */
  disabledTools?: string[];
}

export type McpServers = Record<string, McpServerEntry>;

export const SECRET_PREFIX = "aiChat.mcp.secret.";
const SECRET_REF = /\$\{secret:([A-Za-z0-9_.-]+)\}/g;
const INPUT_REF = /\$\{input:([A-Za-z0-9_.-]+)\}/g;

/** Same rules as the backend: letters, digits, '-' and single '_' (tool names are mcp__server__tool). */
export function sanitizeServerName(name: string): string {
  let cleaned = name.trim().replace(/[^A-Za-z0-9_-]/g, "_").replace(/_{2,}/g, "_").replace(/^[_-]+|[_-]+$/g, "");
  if (cleaned.length > 32) { cleaned = cleaned.slice(0, 32).replace(/[_-]+$/, ""); }
  return cleaned;
}

/** Why a server entry can't work, or null. */
export function validateServer(entry: McpServerEntry): string | null {
  const command = entry.command?.trim();
  const url = entry.url?.trim();
  if (!command && !url) { return "Enter a command (local server) or a URL (remote server)."; }
  if (url && !/^https?:\/\/\S+$/i.test(url)) { return "The URL must start with http:// or https://"; }
  return null;
}

/** JSON with comments and trailing commas (VS Code and Claude files allow them). */
export function parseJsonc(text: string): unknown {
  let out = "";
  let inString = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      out += c;
      if (c === "\\") { out += text[++i] ?? ""; }
      else if (c === '"') { inString = false; }
    } else if (c === '"') {
      inString = true;
      out += c;
    } else if (c === "/" && text[i + 1] === "/") {
      while (i < text.length && text[i] !== "\n") { i++; }
      out += "\n";
    } else if (c === "/" && text[i + 1] === "*") {
      i += 2;
      while (i < text.length && !(text[i] === "*" && text[i + 1] === "/")) { i++; }
      i++;
    } else {
      out += c;
    }
  }
  return JSON.parse(out.replace(/,(\s*[}\]])/g, "$1"));
}

function stringMap(value: unknown): Record<string, string> | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) { return undefined; }
  const map: Record<string, string> = {};
  for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
    map[k] = typeof v === "string" ? v : JSON.stringify(v);
  }
  return Object.keys(map).length > 0 ? map : undefined;
}

function stringList(value: unknown): string[] | undefined {
  return Array.isArray(value) ? value.map((v) => (typeof v === "string" ? v : JSON.stringify(v))) : undefined;
}

/**
 * Reads an MCP config file of any common shape: `{ "mcpServers": {...} }` (Cursor, Claude Desktop, Windsurf,
 * Claude Code), `{ "servers": {...} }` (VS Code), or a bare name → server object.
 */
export function parseMcpFile(text: string): { servers: McpServers; errors: string[] } {
  const errors: string[] = [];
  const servers: McpServers = {};
  let root: unknown;
  try {
    root = parseJsonc(text);
  } catch (e) {
    return { servers, errors: [`Not valid JSON: ${e instanceof Error ? e.message : String(e)}`] };
  }
  if (!root || typeof root !== "object" || Array.isArray(root)) {
    return { servers, errors: ["The file must contain a JSON object."] };
  }
  const obj = root as Record<string, unknown>;
  // A bare name → server map only if every value looks like a server (so e.g. ~/.claude.json without servers is empty)
  const isServerMap = Object.values(obj).every((v) => !!v && typeof v === "object" && ("command" in (v as object) || "url" in (v as object)));
  const source = (obj.mcpServers ?? obj.servers ?? (isServerMap ? obj : {})) as Record<string, unknown>;
  if (!source || typeof source !== "object" || Array.isArray(source)) { return { servers, errors: ["No servers found."] }; }

  for (const [rawName, rawEntry] of Object.entries(source)) {
    const name = sanitizeServerName(rawName);
    if (!name) { errors.push(`'${rawName}': the name needs letters or digits.`); continue; }
    if (!rawEntry || typeof rawEntry !== "object" || Array.isArray(rawEntry)) { errors.push(`'${rawName}': not a server object.`); continue; }
    const e = rawEntry as Record<string, unknown>;
    const entry: McpServerEntry = {
      ...(typeof e.command === "string" ? { command: e.command } : {}),
      ...(stringList(e.args) ? { args: stringList(e.args) } : {}),
      ...(stringMap(e.env) ? { env: stringMap(e.env) } : {}),
      ...(typeof e.url === "string" ? { url: e.url } : {}),
      ...(stringMap(e.headers) ? { headers: stringMap(e.headers) } : {}),
      ...(e.disabled === true || e.enabled === false ? { disabled: true } : {}),
      ...(e.alwaysAllow === true || Array.isArray(e.alwaysAllow) ? { alwaysAllow: e.alwaysAllow as boolean | string[] } : {}),
      ...(stringList(e.disabledTools) ? { disabledTools: stringList(e.disabledTools) } : {}),
    };
    const problem = validateServer(entry);
    if (problem) { errors.push(`'${rawName}': ${problem}`); continue; }
    servers[name] = entry;
  }
  return { servers, errors };
}

/** Env/header names whose values are credentials. */
export function looksSecret(key: string): boolean {
  return /(token|secret|password|passwd|api[_-]?key|access[_-]?key|private[_-]?key|credential|cookie|authorization)/i.test(key) ||
    /(^|[_-])(pat|auth|key)([_-]|$)/i.test(key);   // whole words only: "PATH" or "MONKEY" are not secrets
}

function secretName(server: string, key: string): string {
  return `${server}_${key}`.replace(/[^A-Za-z0-9_.-]/g, "_");
}

/**
 * Moves credential values out of the entry: each becomes `${secret:NAME}` and is returned for SecretStorage.
 * VS Code's `${input:x}` placeholders become secret references too (their value must be entered once).
 */
export function extractSecrets(server: string, entry: McpServerEntry): { entry: McpServerEntry; secrets: Record<string, string> } {
  const secrets: Record<string, string> = {};
  const move = (map: Record<string, string> | undefined, forceHeader = false): Record<string, string> | undefined => {
    if (!map) { return map; }
    const out: Record<string, string> = {};
    for (const [key, value] of Object.entries(map)) {
      let v = value.replace(INPUT_REF, (_m, id: string) => `\${secret:${secretName(server, id)}}`);
      const alreadyRef = /\$\{secret:[^}]+\}/.test(v);
      if (!alreadyRef && v.length > 0 && (looksSecret(key) || (forceHeader && /^authorization$/i.test(key)))) {
        const name = secretName(server, key);
        secrets[name] = v;
        v = `\${secret:${name}}`;
      }
      out[key] = v;
    }
    return out;
  };
  return {
    entry: {
      ...entry,
      ...(entry.env ? { env: move(entry.env) } : {}),
      ...(entry.headers ? { headers: move(entry.headers, true) } : {}),
      ...(entry.args ? { args: entry.args.map((a) => a.replace(INPUT_REF, (_m, id: string) => `\${secret:${secretName(server, id)}}`)) } : {}),
    },
    secrets,
  };
}

/** Secret names an entry refers to. */
export function secretRefs(entry: McpServerEntry): string[] {
  const values = [...(entry.args ?? []), ...Object.values(entry.env ?? {}), ...Object.values(entry.headers ?? {})];
  const names = new Set<string>();
  for (const v of values) { for (const m of v.matchAll(SECRET_REF)) { names.add(m[1]); } }
  return [...names];
}

/** Replaces `${secret:NAME}` with stored values; reports the ones that are missing. */
export async function resolveSecrets(
  servers: McpServers,
  getSecret: (name: string) => Promise<string | undefined>,
): Promise<{ servers: McpServers; missing: Record<string, string[]> }> {
  const resolved: McpServers = {};
  const missing: Record<string, string[]> = {};
  for (const [name, entry] of Object.entries(servers)) {
    const values = new Map<string, string | undefined>();
    for (const ref of secretRefs(entry)) { values.set(ref, await getSecret(ref)); }
    const absent = [...values].filter(([, v]) => v === undefined).map(([k]) => k);
    if (absent.length > 0) { missing[name] = absent; }
    const sub = (s: string) => s.replace(SECRET_REF, (_m, ref: string) => values.get(ref) ?? "");
    const mapValues = (m?: Record<string, string>) => (m ? Object.fromEntries(Object.entries(m).map(([k, v]) => [k, sub(v)])) : m);
    resolved[name] = {
      ...entry,
      ...(entry.args ? { args: entry.args.map(sub) } : {}),
      ...(entry.env ? { env: mapValues(entry.env) } : {}),
      ...(entry.headers ? { headers: mapValues(entry.headers) } : {}),
    };
  }
  return { servers: resolved, missing };
}

/** What the backend receives (Mcp__ServersJson). Servers with missing secrets are still passed: their error explains it. */
export function toBackendJson(servers: McpServers): string {
  return JSON.stringify({ mcpServers: servers });
}

/** "npx -y @scope/pkg --flag \"a b\"" → ["npx", "-y", "@scope/pkg", "--flag", "a b"] */
export function splitCommandLine(line: string): string[] {
  const parts: string[] = [];
  const re = /"([^"]*)"|'([^']*)'|(\S+)/g;
  for (const m of line.matchAll(re)) { parts.push(m[1] ?? m[2] ?? m[3]); }
  return parts;
}

export interface McpPreset {
  id: string;
  label: string;
  detail: string;
  name: string;
  entry: McpServerEntry;
  /** Values the user must provide (stored as secrets) */
  needs?: Array<{ key: string; label: string; where: "env" | "header"; prefix?: string; url?: string }>;
}

export const MCP_PRESETS: McpPreset[] = [
  {
    id: "playwright",
    label: "Browser (Playwright)",
    detail: "Open pages, click, fill forms and read what's on screen. Needs Node.js and Chrome.",
    name: "playwright",
    entry: { command: "npx", args: ["-y", "@playwright/mcp@latest"] },
  },
  {
    id: "context7",
    label: "Library docs (Context7)",
    detail: "Up-to-date documentation and code examples for thousands of libraries. Needs Node.js.",
    name: "context7",
    entry: { command: "npx", args: ["-y", "@upstash/context7-mcp"] },
  },
  {
    id: "github",
    label: "GitHub",
    detail: "Issues, pull requests, code search and more, through GitHub's hosted MCP server.",
    name: "github",
    entry: { url: "https://api.githubcopilot.com/mcp/" },
    needs: [{ key: "Authorization", label: "GitHub personal access token", where: "header", prefix: "Bearer ", url: "https://github.com/settings/personal-access-tokens" }],
  },
];

export interface ImportSource {
  id: string;
  label: string;
  path: string;
}

/** Where other tools keep their MCP servers, for "Import". */
export function importSources(opts: { platform: string; home: string; appData?: string; workspace?: string }): ImportSource[] {
  const { platform, home } = opts;
  const join = (...p: string[]) => p.join(platform === "win32" ? "\\" : "/");
  const appData = opts.appData ?? (platform === "darwin" ? join(home, "Library", "Application Support") : join(home, ".config"));
  const sources: ImportSource[] = [
    { id: "cursor", label: "Cursor", path: join(home, ".cursor", "mcp.json") },
    { id: "claude-desktop", label: "Claude Desktop", path: join(appData, "Claude", "claude_desktop_config.json") },
    { id: "claude-code", label: "Claude Code", path: join(home, ".claude.json") },
    { id: "windsurf", label: "Windsurf", path: join(home, ".codeium", "windsurf", "mcp_config.json") },
    { id: "vscode", label: "VS Code (user)", path: join(appData, "Code", "User", "mcp.json") },
  ];
  if (opts.workspace) {
    sources.push({ id: "workspace", label: "This project (.vscode/mcp.json)", path: join(opts.workspace, ".vscode", "mcp.json") });
  }
  return sources;
}

/** Adds imported servers without overwriting existing names; returns what was added and skipped. */
export function mergeServers(existing: McpServers, incoming: McpServers): { merged: McpServers; added: string[]; skipped: string[] } {
  const merged: McpServers = { ...existing };
  const added: string[] = [];
  const skipped: string[] = [];
  for (const [name, entry] of Object.entries(incoming)) {
    if (merged[name]) { skipped.push(name); continue; }
    merged[name] = entry;
    added.push(name);
  }
  return { merged, added, skipped };
}
