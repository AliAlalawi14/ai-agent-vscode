/**
 * Local, offline autocomplete through Ollama: which small fill-in-the-middle model fits this machine, whether
 * Ollama is running, and downloading the model with progress. No `vscode` import: unit-tested with plain Node.
 */

export const OLLAMA_HOST = "http://localhost:11434";

export interface LocalModel {
  name: string;
  sizeGb: number;
  why: string;
}

/**
 * Autocomplete must answer in well under a second while you type, so the model is small: on a laptop without a
 * big GPU a 1.5B coder model is the sweet spot; more memory allows a larger, smarter one.
 */
export function recommendLocalModel(totalRamGb: number, appleSilicon = false): LocalModel {
  if (totalRamGb >= 48 || (appleSilicon && totalRamGb >= 32)) {
    return { name: "qwen2.5-coder:7b-base", sizeGb: 4.7, why: "Plenty of memory: the most capable small coder model." };
  }
  if (totalRamGb >= 24 || (appleSilicon && totalRamGb >= 16)) {
    return { name: "qwen2.5-coder:3b-base", sizeGb: 1.9, why: "Good balance of speed and quality for this machine." };
  }
  if (totalRamGb >= 8) {
    return { name: "qwen2.5-coder:1.5b-base", sizeGb: 1.0, why: "Fast on a normal laptop CPU, about 1 GB." };
  }
  return { name: "qwen2.5-coder:0.5b-base", sizeGb: 0.5, why: "Smallest model, for machines with little memory." };
}

export interface OllamaStatus {
  running: boolean;
  version?: string;
  models: string[];
}

/** Whether Ollama answers on this machine, and which models it has. */
export async function ollamaStatus(host = OLLAMA_HOST, fetchImpl: typeof fetch = fetch): Promise<OllamaStatus> {
  try {
    const version = await fetchImpl(`${host}/api/version`, { signal: AbortSignal.timeout(2000) });
    if (!version.ok) { return { running: false, models: [] }; }
    const v = (await version.json()) as { version?: string };
    const tags = await fetchImpl(`${host}/api/tags`, { signal: AbortSignal.timeout(3000) });
    const t = tags.ok ? ((await tags.json()) as { models?: Array<{ name?: string }> }) : {};
    return { running: true, ...(v.version ? { version: v.version } : {}), models: (t.models ?? []).map((m) => m.name ?? "").filter(Boolean) };
  } catch {
    return { running: false, models: [] };
  }
}

/** "qwen2.5-coder:1.5b-base" is installed (Ollama may list it with or without the ":latest"-style tag). */
export function hasModel(installed: string[], name: string): boolean {
  const want = name.includes(":") ? name : `${name}:latest`;
  return installed.some((m) => m === want || m === name);
}

export interface PullProgress {
  status: string;
  /** 0–100 while downloading layers, undefined for steps without a size */
  percent?: number;
}

/**
 * Downloads a model (POST /api/pull, streamed as JSON lines). Reports progress; resolves when Ollama says
 * "success", rejects with its error otherwise. Abort to cancel.
 */
export async function pullModel(
  name: string,
  onProgress: (p: PullProgress) => void,
  signal?: AbortSignal,
  host = OLLAMA_HOST,
  fetchImpl: typeof fetch = fetch,
): Promise<void> {
  const response = await fetchImpl(`${host}/api/pull`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ model: name, name, stream: true }),
    ...(signal ? { signal } : {}),
  });
  if (!response.ok || !response.body) {
    throw new Error(`Ollama couldn't start the download (HTTP ${response.status}).`);
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let success = false;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) { break; }
    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split("\n");
    buffer = lines.pop() ?? "";
    for (const line of lines) {
      if (!line.trim()) { continue; }
      let event: { status?: string; total?: number; completed?: number; error?: string };
      try { event = JSON.parse(line); } catch { continue; }
      if (event.error) { throw new Error(event.error); }
      const percent = event.total ? Math.min(100, Math.round(((event.completed ?? 0) / event.total) * 100)) : undefined;
      onProgress({ status: event.status ?? "", ...(percent !== undefined ? { percent } : {}) });
      if (event.status === "success") { success = true; }
    }
  }
  if (!success) { throw new Error("The download ended before Ollama finished it."); }
}
