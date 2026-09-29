/**
 * Message protocol for extension <-> webview communication
 * This file is shared between extension core and webview UI
 */

/**
 * Backend <-> extension contract version. Must equal AgentProtocol.Version in the backend
 * (Ai-Agent/Agent/AgentProtocol.cs); the health check flags a mismatch as "outdated".
 */
export const PROTOCOL_VERSION = 5;

// ── Message Types ───────────────────────────────────────────────────────

export type MessageSegment =
  | { type: "text"; content: string }
  | { type: "tool"; toolId: string }
  | { type: "change"; changeId: string };

export interface MessageMetrics {
  tokens?: number;
  cost?: number;
  latencyMs?: number;
}

export interface Message {
  id: string;
  role: "user" | "assistant";
  content: string;
  segments: MessageSegment[];
  timestamp: number;
  isStreaming?: boolean;
  metrics?: MessageMetrics;
  context?: MentionContext[];
}

export interface ToolExecution {
  id: string;
  tool: string;
  args: Record<string, unknown>;
  status: "running" | "completed" | "error";
  result?: string;
}

export interface SessionState {
  messages: Message[];
  activeTools: ToolExecution[];
  toolHistory: ToolExecution[];
  changes: FileChange[];
  timestamp: number;
}

export interface Conversation {
  id: string;
  title: string;
  messages: Message[];
  createdAt: number;
  updatedAt: number;
}

// Webview -> Extension
export type WebviewMessage =
  | { type: "init" }
  | {
      type: "runTask";
      task: string;
      workspace: string;
      context?: MentionContext[];
      history?: HistoryMessage[];
      model?: string;
      mode?: AgentMode;
      activePlan?: unknown;
      /** The conversation's plan file (.ai/plans/*.plan.md): the backend builds from its current content */
      planPath?: string;
    }
  | { type: "cancelTask" }
  /** Open the plan's Markdown file in the editor */
  | { type: "openPlanFile"; path: string }
  | { type: "revertFile"; filePath: string; changeId?: string }
  /** Undo several applied changes in this order (newest first), stopping at the first that can't be undone */
  | { type: "revertChanges"; changes: Array<{ changeId: string; filePath: string }> }
  | { type: "resolveApproval"; approvalId: string; approved: boolean }
  | {
      type: "openDiff";
      filePath: string;
      /** File content before the agent's change; missing for large files / old history */
      before?: string;
      /** Proposed content (for a change still awaiting approval) */
      after?: string;
      /** true = not on disk yet: diff the current file against `after` */
      proposed?: boolean;
      /** Unified diff; used to rebuild "before" when it is missing */
      patch?: string;
      changeId?: string;
    }
  | { type: "searchSymbols"; query: string }
  | { type: "searchFiles"; query: string }
  | { type: "healthCheck" }
  | { type: "updateSettings"; settings: Record<string, unknown> }
  | { type: "saveConversations"; conversations: Conversation[] }
  | { type: "loadConversations" }
  | { type: "saveCurrentSession"; session: SessionState }
  | { type: "getOpenFiles" }
  | { type: "getRecentFiles" }
  // Model-provider setup in the chat panel (instead of command-palette wizards)
  | { type: "getSetup" }
  | { type: "listProviderModels"; requestId: number; provider: ProviderSetupRequest }
  | { type: "saveProvider"; provider: ProviderSetupRequest; models: string[] }
  | { type: "removeProvider"; name: string }
  | { type: "backendAction"; action: "restart" | "showLog" }
  /** Opens a provider's "get a key" page (https only) */
  | { type: "openExternal"; url: string };

// Extension -> Webview
export type ExtensionMessage =
  | { type: "init"; theme: "light" | "dark" }
  | {
      type: "restoreState";
      conversations: Conversation[];
      activeConversationId: string | null;
      currentSession: SessionState | null;
    }
  | { type: "token"; content: string }
  | {
      type: "toolStart";
      tool: string;
      toolCallId?: string;
      args: Record<string, unknown>;
    }
  | {
      type: "toolResult";
      tool: string;
      toolCallId?: string;
      result: string;
      output?: string;
      status: string;
    }
  | { type: "changeEvent"; change: FileChange }
  /** Agent waits for Accept/Reject ({approvalId, kind, filePath, before, after, patch, command})
   *  or reports the recorded decision ({approvalId, decision}) */
  | { type: "approvalEvent"; approval: Record<string, unknown> }
  /** {type:"plan", plan:{title, steps}} or {type:"update", step, status} */
  | { type: "planEvent"; plan: Record<string, unknown> }
  /** Plan mode asked clarifying questions (the run ended; the answers are the next message) */
  | { type: "questionsEvent"; questions: PlanQuestion[] }
  /** A plan file changed on disk (e.g. edited by the user): its current content */
  | { type: "planUpdated"; plan: Record<string, unknown> }
  /** The run stopped at its step budget; the UI offers Continue */
  | { type: "limitEvent"; limit: Record<string, unknown> }
  /** Bundles rebuilt since this window loaded them (empty = up to date) */
  | { type: "extensionStatus"; staleBundles: string[] }
  /** Revert was refused or failed; the card goes back to "applied" */
  | { type: "revertFailed"; changeId: string; message: string }
  | { type: "loadConversationMessages"; messages: Message[] }
  | {
      type: "metrics";
      metrics: { tokens?: number; cost?: number; latencyMs?: number };
    }
  | { type: "streamDone" }
  | { type: "symbolResults"; query: string; symbols: SymbolResult[] }
  | { type: "fileResults"; query: string; files: string[] }
  | { type: "openFilesUpdate"; files: string[] }
  | { type: "recentFilesUpdate"; files: string[] }
  | {
      type: "healthStatus";
      /** setup = no model provider configured yet: the panel shows its setup form */
      status: "connected" | "degraded" | "disconnected" | "outdated" | "setup";
      detail?: string;
    }
  | { type: "setupState"; setup: SetupState }
  | { type: "providerModels"; requestId: number; models: string[]; error?: string }
  /** Result of saveProvider/removeProvider (ok = saved and the backend is up again) */
  | { type: "providerSaved"; ok: boolean; error?: string }
  | {
      type: "modelsAvailable";
      models: Array<{ id: string; name: string; provider: string }>;
    }
  | { type: "error"; message: string }
  | { type: "diagnostic"; stage: string; detail: string }
  | { type: "clearChat" }
  | { type: "sendSelection"; filePath: string; code: string; language: string };

// File change from backend (unified-diff protocol v2)
export interface FileChange {
  changeId: string;
  sessionId: string;
  filePath: string;
  patch: string;
  before?: string;
  after?: string;
  toolUsed: string;
  isNewFile?: boolean;
  /** The change deleted the file (delete_file, or the source of move_file) */
  isDeletion?: boolean;
  summary: string;
  patchSize: number;
}

/** A clarifying question from Plan mode, answered by picking options (or typing) */
export interface PlanQuestion {
  question: string;
  options: string[];
  multiple?: boolean;
}

// Tool event from backend stream
export interface ToolEvent {
  type: "tool_start" | "tool_result" | "session_start";
  tool: string;
  toolCallId?: string;
  args?: Record<string, unknown>;
  result?: string;
  status?: "completed" | "error";
  summary?: string;
}

// API Types
export interface AgentRunRequest {
  task: string;
  workspace?: string;
  context?: MentionContext[];
  history?: HistoryMessage[];
  model?: string;
  mode?: AgentMode;
  /** The conversation's plan with step statuses (from Plan mode) */
  activePlan?: unknown;
  /** The plan's file; when set the backend reads the plan from it (so the user's edits count) */
  planPath?: string;
  /** Agent/Auto: edits apply at once and are reviewed afterwards (Keep/Undo); commands still ask */
  reviewEdits?: boolean;
}

/** ask = read-only Q&A, plan = read-only + plan, agent = edits with approval, auto = auto-approved edits */
export type AgentMode = "ask" | "plan" | "agent" | "auto";

/** A previous chat turn, sent so the agent can resolve follow-ups ("now fix it"). */
export interface HistoryMessage {
  role: "user" | "assistant";
  content: string;
}

export interface SymbolResult {
  type: "class" | "method" | "property";
  name: string;
  className: string;
  filePath: string;
}

export interface RevertRequest {
  filePath: string;
  workspace?: string;
}

// Context from @-mentions
export interface MentionContext {
  type: "file" | "symbol" | "selection";
  name: string;
  filePath: string;
  className?: string;
  symbolType?: string;
  /** The file focused in the editor when the message was sent */
  active?: boolean;
  /** Added automatically (recent file), not picked by the user */
  autoContext?: boolean;
  /** Selected code (type "selection") */
  code?: string;
  startLine?: number;
  endLine?: number;
}

// ── Model-provider setup ────────────────────────────────────────────────

/** A provider the setup form offers: built-in (Claude, DeepSeek: key only), OpenAI-compatible preset, or custom URL. */
export interface SetupPreset {
  id: string;
  label: string;
  detail: string;
  kind: "builtin" | "openai" | "custom";
  auth: "bearer" | "api-key" | "none";
  baseUrl: string;
  keyUrl?: string;
  /** Azure: the base URL contains {resource}, asked in the form */
  needsResource?: boolean;
  /** Runs on the user's machine (Ollama, LM Studio) */
  local?: boolean;
}

/** A provider that is set up (keys are never sent to the webview). */
export interface ConfiguredProvider {
  name: string;
  label: string;
  models: string[];
  /** false for the legacy aiChat.openai settings, which are edited in VS Code settings */
  removable: boolean;
}

export interface SetupState {
  /** No provider configured: the backend can't start until one is added */
  needsSetup: boolean;
  /** aiChat.backendUrl points at a backend the user runs (providers are configured there) */
  external: boolean;
  /** The backend in use, when known (managed: its 127.0.0.1 port; external: the setting) */
  backendUrl: string | null;
  /** Why the backend isn't running, when it isn't */
  problem: string | null;
  /** aiChat.reviewEdits: edits apply at once and are reviewed afterwards */
  reviewEdits: boolean;
  presets: SetupPreset[];
  providers: ConfiguredProvider[];
}

/** What the setup form sends to list models or save a provider. */
export interface ProviderSetupRequest {
  presetId: string;
  key?: string;
  /** Azure resource name */
  resource?: string;
  /** Custom server */
  baseUrl?: string;
  auth?: "bearer" | "api-key" | "none";
}
