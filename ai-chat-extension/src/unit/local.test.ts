import { test } from "node:test";
import assert from "node:assert/strict";
import * as http from "http";
import type { AddressInfo } from "net";
import { hasModel, ollamaStatus, pullModel, recommendLocalModel, type PullProgress } from "../completion/local";

test("the model fits the machine: 1.5B on a 16 GB laptop, bigger with more memory", () => {
  assert.equal(recommendLocalModel(16).name, "qwen2.5-coder:1.5b-base");
  assert.equal(recommendLocalModel(8).name, "qwen2.5-coder:1.5b-base");
  assert.equal(recommendLocalModel(4).name, "qwen2.5-coder:0.5b-base");
  assert.equal(recommendLocalModel(32).name, "qwen2.5-coder:3b-base");
  assert.equal(recommendLocalModel(16, true).name, "qwen2.5-coder:3b-base");
  assert.equal(recommendLocalModel(64).name, "qwen2.5-coder:7b-base");
});

test("installed models are matched with or without a tag", () => {
  assert.equal(hasModel(["qwen2.5-coder:1.5b-base"], "qwen2.5-coder:1.5b-base"), true);
  assert.equal(hasModel(["llama3:latest"], "llama3"), true);
  assert.equal(hasModel(["qwen2.5-coder:3b-base"], "qwen2.5-coder:1.5b-base"), false);
});

async function fakeOllama(pullLines: string[], opts: { failPull?: boolean } = {}) {
  const server = http.createServer((req, res) => {
    if (req.url === "/api/version") { res.end(JSON.stringify({ version: "0.12.0" })); return; }
    if (req.url === "/api/tags") { res.end(JSON.stringify({ models: [{ name: "llama3:latest" }] })); return; }
    if (req.url === "/api/pull") {
      if (opts.failPull) { res.writeHead(500); res.end("{}"); return; }
      res.writeHead(200, { "Content-Type": "application/x-ndjson" });
      let i = 0;
      const next = () => {
        if (i >= pullLines.length) { res.end(); return; }
        res.write(pullLines[i++] + "\n");
        setTimeout(next, 5);
      };
      next();
      return;
    }
    res.writeHead(404); res.end();
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  const host = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  return { host, close: () => new Promise<void>((r) => server.close(() => r())) };
}

test("status: running with its models, or not running", async () => {
  const srv = await fakeOllama([]);
  try {
    const s = await ollamaStatus(srv.host);
    assert.equal(s.running, true);
    assert.equal(s.version, "0.12.0");
    assert.deepEqual(s.models, ["llama3:latest"]);
  } finally { await srv.close(); }
  assert.equal((await ollamaStatus("http://127.0.0.1:1")).running, false);
});

test("pull reports progress and resolves on success", async () => {
  const srv = await fakeOllama([
    JSON.stringify({ status: "pulling manifest" }),
    JSON.stringify({ status: "pulling abc", total: 1000, completed: 250 }),
    JSON.stringify({ status: "pulling abc", total: 1000, completed: 1000 }),
    JSON.stringify({ status: "verifying sha256 digest" }),
    JSON.stringify({ status: "success" }),
  ]);
  try {
    const seen: PullProgress[] = [];
    await pullModel("qwen2.5-coder:1.5b-base", (p) => seen.push(p), undefined, srv.host);
    assert.deepEqual(seen.filter((p) => p.percent !== undefined).map((p) => p.percent), [25, 100]);
    assert.equal(seen.at(-1)?.status, "success");
  } finally { await srv.close(); }
});

test("pull errors are reported, and a download that stops early is an error", async () => {
  const bad = await fakeOllama([JSON.stringify({ error: "pull model manifest: file does not exist" })]);
  try {
    await assert.rejects(pullModel("nope:1b", () => {}, undefined, bad.host), /file does not exist/);
  } finally { await bad.close(); }

  const cut = await fakeOllama([JSON.stringify({ status: "pulling abc", total: 10, completed: 5 })]);
  try {
    await assert.rejects(pullModel("x", () => {}, undefined, cut.host), /ended before/);
  } finally { await cut.close(); }

  const down = await fakeOllama([], { failPull: true });
  try {
    await assert.rejects(pullModel("x", () => {}, undefined, down.host), /HTTP 500/);
  } finally { await down.close(); }
});
