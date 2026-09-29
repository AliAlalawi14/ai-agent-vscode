/**
 * Inline autocomplete core: what to send (the code around the cursor), how to ask each kind of model, how to
 * clean what comes back, and a small cache. No `vscode` import: unit-tested with plain Node.
 */

/** How a model is asked to fill in code between a prefix and a suffix. */
export type FimStyle =
  /** POST <base>/completions { prompt, suffix } (DeepSeek beta, llama.cpp, vLLM, most local servers) */
  | "completions"
  /** POST <base>/fim/completions { prompt, suffix } (Mistral Codestral) */
  | "mistral"
  /** POST <host>/api/generate { prompt, suffix } (Ollama's native API) */
  | "ollama"
  /** POST <base>/chat/completions with instructions: any chat model, slower */
  | "chat";

export interface CompletionEndpoint {
  style: FimStyle;
  /** Base URL including the version path (…/v1), or Ollama's host for "ollama" */
  baseUrl: string;
  model: string;
  headers: Record<string, string>;
}

export interface CompletionContext {
  prefix: string;
  suffix: string;
  languageId: string;
  /** Workspace-relative path, shown to chat models as a hint */
  path: string;
}

export interface CompletionOptions {
  maxLines: number;
  maxTokens: number;
  temperature: number;
}

export const DEFAULT_OPTIONS: CompletionOptions = { maxLines: 8, maxTokens: 128, temperature: 0.1 };

const MAX_PREFIX = 3000;
const MAX_SUFFIX = 1200;

/** The text before and after the cursor, cut at line boundaries so the model sees whole lines. */
export function buildContext(text: string, offset: number, languageId: string, path: string): CompletionContext {
  let start = Math.max(0, offset - MAX_PREFIX);
  if (start > 0) {
    const nl = text.indexOf("\n", start);
    if (nl !== -1 && nl < offset) { start = nl + 1; }
  }
  let end = Math.min(text.length, offset + MAX_SUFFIX);
  if (end < text.length) {
    const nl = text.lastIndexOf("\n", end);
    if (nl > offset) { end = nl; }
  }
  return { prefix: text.slice(start, offset), suffix: text.slice(offset, end), languageId, path };
}

/**
 * Whether a suggestion makes sense here. Not in the middle of a word (the text right after the cursor starts
 * with a letter or digit), and not on an empty document.
 */
export function shouldComplete(ctx: CompletionContext): boolean {
  if (ctx.prefix.trim().length === 0 && ctx.suffix.trim().length === 0) { return false; }
  const restOfLine = ctx.suffix.split("\n", 1)[0];
  if (/^[A-Za-z0-9_$]/.test(restOfLine)) { return false; }
  return true;
}

/** True when the cursor has code after it on the same line (then only one line is suggested). */
function midLine(ctx: CompletionContext): boolean {
  return ctx.suffix.split("\n", 1)[0].trim().length > 0;
}

const CHAT_SYSTEM =
  "You are a code completion engine. You are given the code before and after the cursor. " +
  "Reply with ONLY the code that should be inserted at the cursor: no explanations, no Markdown fences, " +
  "no repetition of the code before or after it. If nothing should be inserted, reply with nothing.";

/** The HTTP request for one completion. */
export function buildRequest(
  endpoint: CompletionEndpoint,
  ctx: CompletionContext,
  opts: CompletionOptions = DEFAULT_OPTIONS,
): { url: string; body: unknown; headers: Record<string, string> } {
  const base = endpoint.baseUrl.replace(/\/+$/, "");
  const headers = { "Content-Type": "application/json", ...endpoint.headers };
  const stop = midLine(ctx) ? ["\n"] : ["\n\n\n"];
  switch (endpoint.style) {
    case "completions":
      return {
        url: `${base}/completions`,
        headers,
        body: { model: endpoint.model, prompt: ctx.prefix, suffix: ctx.suffix, max_tokens: opts.maxTokens, temperature: opts.temperature, stop, stream: false },
      };
    case "mistral":
      return {
        url: `${base}/fim/completions`,
        headers,
        body: { model: endpoint.model, prompt: ctx.prefix, suffix: ctx.suffix, max_tokens: opts.maxTokens, temperature: opts.temperature, stop, stream: false },
      };
    case "ollama":
      return {
        url: `${base.replace(/\/v1$/, "")}/api/generate`,
        headers,
        body: {
          model: endpoint.model,
          prompt: ctx.prefix,
          suffix: ctx.suffix,
          stream: false,
          options: { num_predict: opts.maxTokens, temperature: opts.temperature, stop },
        },
      };
    case "chat":
      return {
        url: `${base}/chat/completions`,
        headers,
        body: {
          model: endpoint.model,
          temperature: opts.temperature,
          max_tokens: opts.maxTokens,
          stream: false,
          messages: [
            { role: "system", content: CHAT_SYSTEM },
            {
              role: "user",
              content:
                `File: ${ctx.path} (${ctx.languageId})\n\n<BEFORE_CURSOR>\n${ctx.prefix}</BEFORE_CURSOR>\n` +
                `<AFTER_CURSOR>${ctx.suffix}\n</AFTER_CURSOR>\n\nWrite the code for the cursor position.`,
            },
          ],
        },
      };
  }
}

/** The completion text from each style's response. */
export function parseResponse(style: FimStyle, json: unknown): string {
  const data = json as Record<string, unknown>;
  switch (style) {
    case "completions": {
      const choice = (data.choices as Array<Record<string, unknown>> | undefined)?.[0];
      return typeof choice?.text === "string" ? choice.text : "";
    }
    case "mistral":
    case "chat": {
      const choice = (data.choices as Array<{ message?: { content?: unknown } }> | undefined)?.[0];
      const content = choice?.message?.content;
      return typeof content === "string" ? content : "";
    }
    case "ollama":
      return typeof data.response === "string" ? data.response : "";
  }
}

/**
 * Makes a raw model answer safe to show as ghost text: strips Markdown fences, drops a repeat of the line the
 * cursor is on, stops where the model starts repeating the code after the cursor, and limits the length.
 */
export function cleanCompletion(raw: string, ctx: CompletionContext, opts: CompletionOptions = DEFAULT_OPTIONS): string {
  let text = raw.replace(/\r\n/g, "\n");

  // ```lang\n…\n``` (chat models)
  const fence = /^\s*```[\w+-]*\n([\s\S]*?)\n?```\s*$/.exec(text);
  if (fence) { text = fence[1]; }

  // The model repeated the start of the current line
  const currentLine = ctx.prefix.slice(ctx.prefix.lastIndexOf("\n") + 1);
  if (currentLine.trim().length > 0 && text.startsWith(currentLine)) { text = text.slice(currentLine.length); }

  // Stop before the model re-types what already follows the cursor
  text = cutSuffixRepeat(text, ctx.suffix);

  // Mid-line: one line only. Otherwise at most maxLines.
  const lines = text.split("\n");
  text = midLine(ctx) ? lines[0] : lines.slice(0, opts.maxLines).join("\n");

  text = text.replace(/\s+$/, "");
  if (text.trim().length === 0) { return ""; }
  // Don't add a duplicate closer: the suffix already starts with what the completion ends with
  const nextChar = ctx.suffix[0];
  if (nextChar && /[)\]}"'`;]/.test(nextChar) && text.endsWith(nextChar)) { text = text.slice(0, -1); }
  return text;
}

/**
 * Cuts the completion where it starts repeating the lines after the cursor. A first line of 3+ characters
 * (e.g. "return x;") is enough evidence; a short one (e.g. "}") must be followed by a second matching line,
 * so a completion that legitimately closes its own block isn't cut.
 */
function cutSuffixRepeat(text: string, suffix: string): string {
  const after = suffix.split("\n").map((l) => l.trim()).filter((l) => l.length > 0);
  if (after.length === 0) { return text; }
  const lines = text.split("\n");
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].trim() !== after[0]) { continue; }
    const next = lines.slice(i + 1).map((l) => l.trim()).find((l) => l.length > 0);
    if (after[0].length >= 3 || (after.length > 1 && next === after[1])) {
      return lines.slice(0, i).join("\n");
    }
  }
  // A single line that runs into the rest of the current line ("foo(x" + ", y)" → cut at ", y)")
  if (after[0].length >= 3) {
    const at = lines[0].indexOf(after[0]);
    if (at >= 0) { return lines[0].slice(0, at); }
  }
  return text;
}

/**
 * Remembers recent suggestions. When the user keeps typing the suggested text, the rest of it is reused
 * instead of asking the model again.
 */
export class CompletionCache {
  private readonly entries: Array<{ prefix: string; suffix: string; text: string }> = [];

  constructor(private readonly size = 50) {}

  get(ctx: CompletionContext): string | undefined {
    for (let i = this.entries.length - 1; i >= 0; i--) {
      const e = this.entries[i];
      if (e.suffix !== ctx.suffix) { continue; }
      if (e.prefix === ctx.prefix) { return e.text; }
      // Typed ahead: the new prefix is the old prefix plus the start of the suggestion
      if (ctx.prefix.startsWith(e.prefix)) {
        const typed = ctx.prefix.slice(e.prefix.length);
        if (typed.length > 0 && e.text.startsWith(typed) && e.text.length > typed.length) { return e.text.slice(typed.length); }
      }
    }
    return undefined;
  }

  set(ctx: CompletionContext, text: string): void {
    this.entries.push({ prefix: ctx.prefix, suffix: ctx.suffix, text });
    if (this.entries.length > this.size) { this.entries.shift(); }
  }
}

export interface CompletionResult {
  text: string;
  cached: boolean;
  ms: number;
}

/** Asks the model for one completion (cache first). Throws on HTTP/network errors; the caller decides. */
export class CompletionEngine {
  readonly cache = new CompletionCache();

  constructor(
    private readonly fetchImpl: typeof fetch = fetch,
    private readonly opts: CompletionOptions = DEFAULT_OPTIONS,
  ) {}

  async complete(endpoint: CompletionEndpoint, ctx: CompletionContext, signal?: AbortSignal): Promise<CompletionResult> {
    const started = Date.now();
    const cached = this.cache.get(ctx);
    if (cached !== undefined) { return { text: cached, cached: true, ms: 0 }; }

    const request = buildRequest(endpoint, ctx, this.opts);
    const timeout = AbortSignal.timeout(15_000);
    const response = await this.fetchImpl(request.url, {
      method: "POST",
      headers: request.headers,
      body: JSON.stringify(request.body),
      signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
    });
    if (!response.ok) {
      const detail = (await response.text().catch(() => "")).slice(0, 200);
      throw new CompletionHttpError(response.status, detail);
    }
    const text = cleanCompletion(parseResponse(endpoint.style, await response.json()), ctx, this.opts);
    this.cache.set(ctx, text);
    return { text, cached: false, ms: Date.now() - started };
  }
}

export class CompletionHttpError extends Error {
  constructor(readonly status: number, detail: string) {
    super(
      status === 401 || status === 403
        ? `The provider rejected the key (HTTP ${status}).`
        : status === 404
          ? `HTTP 404: this provider or model doesn't offer this kind of completion. Try the "Chat" style.${detail ? ` (${detail})` : ""}`
          : `HTTP ${status}${detail ? `: ${detail}` : ""}`,
    );
  }
}

/** The style a provider most likely supports, from its URL (the user can override it). */
export function detectStyle(baseUrl: string, providerId: string): FimStyle {
  const url = baseUrl.toLowerCase();
  if (providerId === "deepseek" || url.includes("api.deepseek.com")) { return "completions"; }
  if (url.includes("mistral.ai")) { return "mistral"; }
  if (url.includes(":11434") || providerId.startsWith("ollama")) { return "ollama"; }
  if (url.includes("localhost") || url.includes("127.0.0.1")) { return "completions"; }   // llama.cpp, vLLM, LM Studio…
  return "chat";
}
