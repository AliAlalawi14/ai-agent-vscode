import * as vscode from "vscode";
import type {
  AgentRunRequest,
  FileChange,
  PlanQuestion,
  ToolEvent,
} from "../shared/protocol";
import { apiUrl } from "../shared/endpoints";

function getBackendUrl(): string {
  return vscode.workspace
    .getConfiguration("aiChat")
    .get<string>("backendUrl", "http://localhost:5036");
}

/** Headers every backend request needs (the backend rejects requests without the token). */
export function authHeaders(): Record<string, string> {
  const token = vscode.workspace
    .getConfiguration("aiChat")
    .get<string>("apiToken", "");
  return { "X-Agent-Token": token };
}

/** Log prefix for tracing the streaming pipeline */
const LOG = "[AgentApiClient]";

export class AgentApiClient {
  private get urls() {
    return apiUrl(getBackendUrl());
  }

  /**
   * Run agent with streaming response.
   * Yields content chunks, tool events, and file changes.
   * Accepts an optional AbortSignal so the caller can cancel the fetch.
   */
  async *runStream(
    request: AgentRunRequest,
    signal?: AbortSignal,
  ): AsyncGenerator<
    | { type: "content"; content: string }
    | { type: "toolStart"; event: ToolEvent }
    | { type: "toolResult"; event: ToolEvent }
    | { type: "change"; change: FileChange }
    | { type: "approval"; approval: Record<string, unknown> }
    | { type: "plan"; plan: Record<string, unknown> }
    | { type: "questions"; questions: PlanQuestion[] }
    | { type: "limit"; limit: Record<string, unknown> }
    | { type: "metrics"; metrics: Record<string, unknown> }
    | { type: "done" }
    | { type: "error"; message: string }
    | { type: "diagnostic"; stage: string; detail: string },
    void,
    unknown
  > {
    const startTime = Date.now();
    let contentCharCount = 0;
    let toolEventCount = 0;
    let changeCount = 0;
    let rawLineCount = 0;

    try {
      console.log(`${LOG} Connecting to ${this.urls.stream}...`);
      yield {
        type: "diagnostic",
        stage: "connect",
        detail: `Connecting to ${this.urls.stream}`,
      };

      const response = await fetch(this.urls.stream, {
        method: "POST",
        headers: { "Content-Type": "application/json", ...authHeaders() },
        body: JSON.stringify(request),
        signal,
      });

      console.log(
        `${LOG} Response status: ${response.status} ${response.statusText}`,
      );
      console.log(
        `${LOG} Content-Type: ${response.headers.get("content-type")}`,
      );
      yield {
        type: "diagnostic",
        stage: "response",
        detail: `HTTP ${response.status} ${response.statusText}, Content-Type: ${response.headers.get("content-type")}`,
      };

      if (!response.ok) {
        yield {
          type: "error",
          message:
            response.status === 401
              ? "Backend rejected the request (401). Set 'aiChat.apiToken' to the backend's Agent:ApiToken."
              : `Backend returned ${response.status}: ${response.statusText}`,
        };
        return;
      }

      if (!response.body) {
        throw new Error("No response body");
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder("utf-8");
      let buffer = "";

      while (true) {
        const { done, value } = await reader.read();
        if (done) {
          console.log(
            `${LOG} Stream ended. Raw lines: ${rawLineCount}, content chars: ${contentCharCount}, tool events: ${toolEventCount}, changes: ${changeCount}, elapsed: ${Date.now() - startTime}ms`,
          );
          break;
        }

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n");
        buffer = lines.pop() || "";

        for (const line of lines) {
          rawLineCount++;
          const trimmed = line.trim();
          if (!trimmed.startsWith("data: ")) continue;

          const dataStr = trimmed.replace("data: ", "").trim();
          if (!dataStr) continue;

          try {
            const parsed = JSON.parse(dataStr);

            // Content event: {"content":"..."}
            if (parsed.content !== undefined) {
              contentCharCount += String(parsed.content).length;
              yield { type: "content", content: parsed.content };
              continue;
            }

            // Tool event: {"tool":{"type":"tool_start",...}}
            if (parsed.tool) {
              toolEventCount++;
              const toolEvent = parsed.tool as ToolEvent;
              if (toolEvent.type === "tool_start") {
                yield { type: "toolStart", event: toolEvent };
              } else if (toolEvent.type === "tool_result") {
                yield { type: "toolResult", event: toolEvent };
              } else if (toolEvent.type === "session_start") {
                yield {
                  type: "diagnostic",
                  stage: "session",
                  detail: `Session started: ${toolEvent.result || (toolEvent as any).sessionId || "unknown"}`,
                };
              }
              continue;
            }

            // Approval event: the agent waits for Accept/Reject ({approvalId,...}),
            // or the decision was recorded ({approvalId, decision})
            // Run summary: {tokens, cost, latencyMs, steps, toolCalls, toolErrors}
            if (parsed.metrics) {
              yield { type: "metrics", metrics: parsed.metrics };
              continue;
            }

            // Step budget reached: {steps, mode}
            if (parsed.limit) {
              yield { type: "limit", limit: parsed.limit };
              continue;
            }

            // Plan events: a submitted plan, or a step status update
            if (parsed.plan) {
              yield { type: "plan", plan: parsed.plan };
              continue;
            }

            if (parsed.approval) {
              yield { type: "approval", approval: parsed.approval };
              continue;
            }

            // Plan mode's clarifying questions: {"questions":[{question, options, multiple}]}
            if (parsed.questions) {
              yield { type: "questions", questions: parsed.questions as PlanQuestion[] };
              continue;
            }

            // Change event (v2 unified-diff protocol)
            if (parsed.change) {
              changeCount++;
              yield { type: "change", change: parsed.change as FileChange };
              continue;
            }

            // Done event: {"done":true}
            if (parsed.done) {
              console.log(`${LOG} Received done event`);
              yield { type: "done" };
              return;
            }

            // Error event: {"error":"..."}
            if (parsed.error) {
              yield { type: "error", message: parsed.error };
              return;
            }

            // Unknown event — log for debugging
            console.warn(
              `${LOG} Unknown SSE event shape:`,
              Object.keys(parsed),
            );
          } catch {
            console.warn(
              `${LOG} Unparseable SSE line (#${rawLineCount}):`,
              dataStr.slice(0, 200),
            );
          }
        }
      }

      // Process any remaining buffer
      if (buffer.trim()) {
        const trimmed = buffer.trim();
        if (trimmed.startsWith("data: ")) {
          const dataStr = trimmed.replace("data: ", "").trim();
          try {
            const parsed = JSON.parse(dataStr);
            if (parsed.done) {
              yield { type: "done" };
            } else if (parsed.error) {
              yield { type: "error", message: parsed.error };
            }
          } catch {
            console.warn(
              `${LOG} Unparseable final SSE line:`,
              dataStr.slice(0, 200),
            );
          }
        }
      }

      // Stream ended without explicit "done" — that's fine, emit done
      yield { type: "done" };
    } catch (error) {
      yield {
        type: "error",
        message: error instanceof Error ? error.message : "Unknown error",
      };
    }
  }

  /**
   * Search symbols for @-mentions
   */
  async searchSymbols(
    query: string,
    workspace?: string,
  ): Promise<
    Array<{
      type: "class" | "method" | "property";
      name: string;
      className: string;
      filePath: string;
    }>
  > {
    const params = new URLSearchParams();
    if (query) params.append("query", query);
    if (workspace) params.append("workspace", workspace);

    const response = await fetch(`${this.urls.symbols}?${params.toString()}`, {
      headers: authHeaders(),
    });

    if (!response.ok) {
      throw new Error(`Failed to search symbols: ${response.statusText}`);
    }

    const data = (await response.json()) as {
      symbols?: Array<{
        type: "class" | "method" | "property";
        name: string;
        className: string;
        filePath: string;
      }>;
    };
    return data.symbols || [];
  }

  /**
   * Search files for @-mentions
   */
  async searchFiles(query: string, workspace?: string): Promise<string[]> {
    const params = new URLSearchParams();
    if (query) params.append("query", query);
    if (workspace) params.append("workspace", workspace);

    const response = await fetch(`${this.urls.files}?${params.toString()}`, {
      headers: authHeaders(),
    });

    if (!response.ok) {
      throw new Error(`Failed to search files: ${response.statusText}`);
    }

    const data = (await response.json()) as { files?: string[] };
    return data.files || [];
  }

  /** Models of every configured provider that can run the agent, the default one first. */
  async getModels(): Promise<Array<{ id: string; name: string; provider: string }>> {
    const response = await fetch(this.urls.models, { headers: authHeaders() });
    if (!response.ok) { return []; }
    const data = (await response.json()) as { models?: Array<{ id: string; name: string; provider: string }> };
    return data.models ?? [];
  }

  /** A plan file's current content, parsed by the backend (null if it is gone or not a plan file). */
  async getPlan(path: string, workspace: string): Promise<Record<string, unknown> | null> {
    const params = new URLSearchParams({ path, workspace });
    const response = await fetch(`${this.urls.plan}?${params.toString()}`, { headers: authHeaders() });
    if (!response.ok) { return null; }
    const data = (await response.json()) as { plan?: Record<string, unknown> };
    return data.plan ?? null;
  }

  /** Accept or reject a change/command the agent is waiting on. */
  async approve(approvalId: string, approved: boolean): Promise<void> {
    const response = await fetch(this.urls.approve, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...authHeaders() },
      body: JSON.stringify({ approvalId, approved }),
    });
    if (!response.ok) {
      throw new Error(
        response.status === 404
          ? "The agent is no longer waiting for this change (it timed out or the run ended)."
          : `Approval failed: ${response.status} ${response.statusText}`,
      );
    }
  }

  /**
   * Revert a file change via the backend ChangeTracker.
   * Prefers changeId-based revert (v2), falls back to filePath-based (legacy).
   */
  async revertFile(
    changeId: string,
    sessionId?: string,
    filePath?: string,
    workspace?: string,
  ): Promise<void> {
    const payload: Record<string, unknown> = { workspace };

    if (changeId) {
      payload.changeId = changeId;
    } else if (filePath) {
      payload.filePath = filePath;
    }

    const response = await fetch(this.urls.revert, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...authHeaders() },
      body: JSON.stringify(payload),
    });

    if (!response.ok) {
      // 409 = the file changed since and the backend refused to overwrite it; show its reason
      const body = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(body.error || `Failed to revert file: ${response.status} ${response.statusText}`);
    }
  }
}

export const agentApiClient = new AgentApiClient();
