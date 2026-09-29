import { test } from "node:test";
import assert from "node:assert/strict";
import {
  extractSecrets,
  importSources,
  looksSecret,
  mergeServers,
  parseJsonc,
  parseMcpFile,
  resolveSecrets,
  sanitizeServerName,
  secretRefs,
  splitCommandLine,
  toBackendJson,
  validateServer,
} from "../services/mcpConfig";

test("parses Cursor / Claude Desktop files (mcpServers)", () => {
  const { servers, errors } = parseMcpFile(`{
    "mcpServers": {
      "playwright": { "command": "npx", "args": ["@playwright/mcp@latest"] },
      "github": { "url": "https://api.githubcopilot.com/mcp/", "headers": { "Authorization": "Bearer x" } },
      "off": { "command": "node", "disabled": true }
    }
  }`);
  assert.deepEqual(errors, []);
  assert.deepEqual(servers.playwright, { command: "npx", args: ["@playwright/mcp@latest"] });
  assert.equal(servers.github.url, "https://api.githubcopilot.com/mcp/");
  assert.equal(servers.off.disabled, true);
});

test("parses VS Code mcp.json with comments, trailing commas and ${input:} placeholders", () => {
  const { servers, errors } = parseMcpFile(`{
    // VS Code allows comments
    "inputs": [{ "id": "token", "type": "promptString", "password": true }],
    "servers": {
      "gh": { "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer \${input:token}" }, },
      "local": { "type": "stdio", "command": "uvx", "args": ["mcp-server-git"], /* inline */ },
    },
  }`);
  assert.deepEqual(errors, []);
  assert.equal(servers.local.command, "uvx");
  assert.equal(servers.gh.headers?.Authorization, "Bearer ${input:token}");
});

test("a file without servers yields nothing (not every key as a server)", () => {
  const { servers } = parseMcpFile(`{ "numStartups": 5, "theme": "dark", "projects": {} }`);
  assert.deepEqual(servers, {});
});

test("bad entries are reported and skipped", () => {
  const { servers, errors } = parseMcpFile(`{ "mcpServers": { "a": {}, "b": { "url": "ftp://x" }, "c": 5, "ok": { "command": "node" } } }`);
  assert.deepEqual(Object.keys(servers), ["ok"]);
  assert.equal(errors.length, 3);
  assert.equal(parseMcpFile("{ nope").errors.length, 1);
});

test("jsonc keeps // and /* inside strings", () => {
  assert.deepEqual(parseJsonc(`{ "u": "https://x.com/a", "c": "/* not a comment */" }`), { u: "https://x.com/a", c: "/* not a comment */" });
});

test("server names are made safe for mcp__server__tool", () => {
  assert.equal(sanitizeServerName("my server"), "my_server");
  assert.equal(sanitizeServerName("a__b"), "a_b");
  assert.equal(sanitizeServerName("__x__"), "x");
  assert.equal(sanitizeServerName("!!!"), "");
});

test("validation needs a command or an http(s) url", () => {
  assert.notEqual(validateServer({}), null);
  assert.notEqual(validateServer({ url: "ws://x" }), null);
  assert.equal(validateServer({ command: "npx" }), null);
  assert.equal(validateServer({ url: "https://x/mcp" }), null);
});

test("credential names are recognized, ordinary ones are not", () => {
  for (const k of ["GITHUB_TOKEN", "API_KEY", "OPENAI_API_KEY", "Authorization", "DB_PASSWORD", "GH_PAT", "X-Auth", "CLIENT_SECRET"]) {
    assert.equal(looksSecret(k), true, k);
  }
  for (const k of ["PATH", "DEBUG", "NODE_ENV", "HOME", "MONKEY", "PATHEXT", "LOG_LEVEL"]) {
    assert.equal(looksSecret(k), false, k);
  }
});

test("credentials move to secret storage and are replaced by references", () => {
  const { entry, secrets } = extractSecrets("gh", {
    command: "npx",
    env: { GITHUB_TOKEN: "ghp_abc", DEBUG: "1" },
    headers: { Authorization: "Bearer xyz", "X-Trace": "on" },
    args: ["--token", "${input:tok}"],
  });
  assert.equal(entry.env?.GITHUB_TOKEN, "${secret:gh_GITHUB_TOKEN}");
  assert.equal(entry.env?.DEBUG, "1");
  assert.equal(entry.headers?.Authorization, "${secret:gh_Authorization}");
  assert.equal(entry.headers?.["X-Trace"], "on");
  assert.deepEqual(entry.args, ["--token", "${secret:gh_tok}"]);
  assert.deepEqual(secrets, { gh_GITHUB_TOKEN: "ghp_abc", gh_Authorization: "Bearer xyz" });
  assert.deepEqual(secretRefs(entry).sort(), ["gh_Authorization", "gh_GITHUB_TOKEN", "gh_tok"]);
});

test("existing references are not moved twice", () => {
  const { secrets } = extractSecrets("s", { command: "x", env: { TOKEN: "${secret:s_TOKEN}" } });
  assert.deepEqual(secrets, {});
});

test("secrets are resolved at start; missing ones are reported", async () => {
  const store: Record<string, string> = { gh_TOKEN: "real" };
  const { servers, missing } = await resolveSecrets(
    {
      gh: { command: "x", env: { TOKEN: "${secret:gh_TOKEN}" }, args: ["--k=${secret:gh_TOKEN}"] },
      other: { url: "https://x", headers: { Authorization: "Bearer ${secret:other_A}" } },
    },
    async (n) => store[n],
  );
  assert.equal(servers.gh.env?.TOKEN, "real");
  assert.deepEqual(servers.gh.args, ["--k=real"]);
  assert.equal(servers.other.headers?.Authorization, "Bearer ");
  assert.deepEqual(missing, { other: ["other_A"] });
});

test("backend JSON uses the mcpServers shape", () => {
  assert.deepEqual(JSON.parse(toBackendJson({ a: { command: "x" } })), { mcpServers: { a: { command: "x" } } });
});

test("command lines split like a shell, keeping quoted parts", () => {
  assert.deepEqual(splitCommandLine(`npx -y @scope/pkg --flag "a b" 'c d'`), ["npx", "-y", "@scope/pkg", "--flag", "a b", "c d"]);
});

test("import merges without overwriting existing servers", () => {
  const { merged, added, skipped } = mergeServers({ a: { command: "old" } }, { a: { command: "new" }, b: { command: "b" } });
  assert.equal(merged.a.command, "old");
  assert.deepEqual(added, ["b"]);
  assert.deepEqual(skipped, ["a"]);
});

test("import sources per platform", () => {
  const win = importSources({ platform: "win32", home: "C:\\Users\\u", appData: "C:\\Users\\u\\AppData\\Roaming", workspace: "C:\\p" });
  assert.equal(win.find((s) => s.id === "cursor")?.path, "C:\\Users\\u\\.cursor\\mcp.json");
  assert.equal(win.find((s) => s.id === "claude-desktop")?.path, "C:\\Users\\u\\AppData\\Roaming\\Claude\\claude_desktop_config.json");
  assert.equal(win.find((s) => s.id === "workspace")?.path, "C:\\p\\.vscode\\mcp.json");
  const mac = importSources({ platform: "darwin", home: "/Users/u" });
  assert.equal(mac.find((s) => s.id === "vscode")?.path, "/Users/u/Library/Application Support/Code/User/mcp.json");
});
