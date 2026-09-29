import { test } from "node:test";
import assert from "node:assert/strict";
import * as http from "http";
import type { AddressInfo } from "net";
import {
  CompletionCache,
  CompletionEngine,
  CompletionHttpError,
  buildContext,
  buildRequest,
  cleanCompletion,
  detectStyle,
  parseResponse,
  shouldComplete,
  type CompletionContext,
  type CompletionEndpoint,
  type FimStyle,
} from "../completion/core";

const ctx = (prefix: string, suffix = ""): CompletionContext => ({ prefix, suffix, languageId: "typescript", path: "src/a.ts" });

test("context is the text around the cursor, cut at line boundaries", () => {
  const text = "line1\nline2\nfunction add(a, b) {\n  return \n}\nafter";
  const offset = text.indexOf("return ") + "return ".length;
  const c = buildContext(text, offset, "typescript", "a.ts");
  assert.equal(c.prefix, text.slice(0, offset));
  assert.equal(c.suffix, text.slice(offset));

  const long = "x".repeat(10) + "\n" + "y".repeat(5000) + "\nlast line\n";
  const c2 = buildContext(long, long.length - 1, "ts", "a.ts");
  assert.ok(c2.prefix.length <= 3000);
  assert.ok(!c2.prefix.startsWith("y".repeat(10)) || c2.prefix.indexOf("\n") >= 0);
});

test("no suggestion in the middle of a word or in an empty file", () => {
  assert.equal(shouldComplete(ctx("const va", "lue = 1")), false);
  assert.equal(shouldComplete(ctx("", "")), false);
  assert.equal(shouldComplete(ctx("const value = ", ";")), true);
  assert.equal(shouldComplete(ctx("function f() {\n  ", "\n}")), true);
});

const endpoint = (style: FimStyle, baseUrl = "https://api.example.com/v1"): CompletionEndpoint => ({
  style, baseUrl, model: "m", headers: { Authorization: "Bearer k" },
});

test("each style builds the right request", () => {
  const c = ctx("def add(a, b):\n    return ", "\n");
  const completions = buildRequest(endpoint("completions"), c);
  assert.equal(completions.url, "https://api.example.com/v1/completions");
  assert.deepEqual((completions.body as Record<string, unknown>).suffix, "\n");
  assert.equal(completions.headers.Authorization, "Bearer k");

  assert.equal(buildRequest(endpoint("mistral", "https://api.mistral.ai/v1"), c).url, "https://api.mistral.ai/v1/fim/completions");

  const ollama = buildRequest(endpoint("ollama", "http://localhost:11434/v1"), c);
  assert.equal(ollama.url, "http://localhost:11434/api/generate");
  assert.equal((ollama.body as { suffix: string }).suffix, "\n");

  const chat = buildRequest(endpoint("chat"), c);
  assert.equal(chat.url, "https://api.example.com/v1/chat/completions");
  const messages = (chat.body as { messages: Array<{ content: string }> }).messages;
  assert.ok(messages[1].content.includes("<BEFORE_CURSOR>"));
});

test("mid-line completions stop at the end of the line", () => {
  const body = buildRequest(endpoint("completions"), ctx("foo(", ")")).body as { stop: string[] };
  assert.deepEqual(body.stop, ["\n"]);
});

test("responses are parsed per style", () => {
  assert.equal(parseResponse("completions", { choices: [{ text: "a + b" }] }), "a + b");
  assert.equal(parseResponse("mistral", { choices: [{ message: { content: "a + b" } }] }), "a + b");
  assert.equal(parseResponse("chat", { choices: [{ message: { content: "a + b" } }] }), "a + b");
  assert.equal(parseResponse("ollama", { response: "a + b" }), "a + b");
  assert.equal(parseResponse("completions", {}), "");
});

test("cleaning: fences, repeated line, suffix overlap, closers, length", () => {
  assert.equal(cleanCompletion("```python\na + b\n```", ctx("return ")), "a + b");
  assert.equal(cleanCompletion("    return a + b", ctx("def f(a, b):\n    return ")), "a + b");
  assert.equal(cleanCompletion("a + b\n}\nconsole.log(1)", ctx("return ", "\n}\nconsole.log(1)")), "a + b");
  assert.equal(cleanCompletion("x)", ctx("foo(", ")")), "x");
  assert.equal(cleanCompletion("a\nb\nc\nd", ctx("x = ", "\n"), { maxLines: 2, maxTokens: 50, temperature: 0 }), "a\nb");
  assert.equal(cleanCompletion("   \n  ", ctx("x = ")), "");
  assert.equal(cleanCompletion("first\nsecond", ctx("call(", ", y)")), "first");
});

test("the cache reuses a suggestion while the user types it", () => {
  const cache = new CompletionCache();
  cache.set(ctx("const total = ", ";"), "items.length");
  assert.equal(cache.get(ctx("const total = ", ";")), "items.length");
  assert.equal(cache.get(ctx("const total = ite", ";")), "ms.length");
  assert.equal(cache.get(ctx("const total = xyz", ";")), undefined);
  assert.equal(cache.get(ctx("const total = items.length", ";")), undefined);   // fully typed
});

test("style detection from the provider", () => {
  assert.equal(detectStyle("https://api.deepseek.com", "deepseek"), "completions");
  assert.equal(detectStyle("https://api.mistral.ai/v1", "mistral"), "mistral");
  assert.equal(detectStyle("http://localhost:11434/v1", "ollama"), "ollama");
  assert.equal(detectStyle("http://localhost:8080/v1", "custom"), "completions");
  assert.equal(detectStyle("https://api.openai.com/v1", "openai"), "chat");
});

/** A fake provider answering every style; records requests. */
async function fakeServer(handler: (url: string, body: Record<string, unknown>) => { status?: number; json: unknown; delayMs?: number }) {
  const requests: Array<{ url: string; body: Record<string, unknown> }> = [];
  const server = http.createServer((req, res) => {
    let data = "";
    req.on("data", (d) => (data += d)).on("end", () => {
      const body = data ? JSON.parse(data) : {};
      requests.push({ url: req.url ?? "", body });
      const reply = handler(req.url ?? "", body);
      setTimeout(() => {
        res.writeHead(reply.status ?? 200, { "Content-Type": "application/json" });
        res.end(JSON.stringify(reply.json));
      }, reply.delayMs ?? 0);
    });
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  const port = (server.address() as AddressInfo).port;
  return { base: `http://127.0.0.1:${port}`, requests, close: () => new Promise<void>((r) => server.close(() => r())) };
}

test("engine: real HTTP round trip for every style, then served from cache", async () => {
  const srv = await fakeServer((url) =>
    url.endsWith("/api/generate") ? { json: { response: "a + b" } }
      : url.endsWith("/completions") && !url.includes("chat") && !url.includes("fim") ? { json: { choices: [{ text: "a + b" }] } }
        : { json: { choices: [{ message: { content: "```\na + b\n```" } }] } });
  try {
    for (const style of ["completions", "mistral", "ollama", "chat"] as FimStyle[]) {
      const engine = new CompletionEngine();
      const c = ctx(`// ${style}\nfunction add(a, b) {\n  return `, "\n}");
      const first = await engine.complete({ style, baseUrl: `${srv.base}/v1`, model: "m", headers: {} }, c);
      assert.equal(first.text, "a + b", style);
      assert.equal(first.cached, false);
      const again = await engine.complete({ style, baseUrl: `${srv.base}/v1`, model: "m", headers: {} }, c);
      assert.equal(again.cached, true);
    }
    assert.equal(srv.requests.length, 4);
    assert.deepEqual(srv.requests.map((r) => r.url), ["/v1/completions", "/v1/fim/completions", "/api/generate", "/v1/chat/completions"]);
  } finally {
    await srv.close();
  }
});

test("engine: errors are explained, and typing cancels the request", async () => {
  const srv = await fakeServer((url) => (url.includes("slow") ? { json: { choices: [{ text: "late" }] }, delayMs: 2000 } : { status: 401, json: {} }));
  try {
    const engine = new CompletionEngine();
    await assert.rejects(
      engine.complete({ style: "completions", baseUrl: `${srv.base}/v1`, model: "m", headers: {} }, ctx("x = ")),
      (e: unknown) => e instanceof CompletionHttpError && /rejected the key/.test(e.message),
    );

    const controller = new AbortController();
    const pending = engine.complete({ style: "completions", baseUrl: `${srv.base}/slow`, model: "m", headers: {} }, ctx("y = "), controller.signal);
    setTimeout(() => controller.abort(), 50);
    const started = Date.now();
    await assert.rejects(pending);
    assert.ok(Date.now() - started < 1500);
  } finally {
    await srv.close();
  }
});
