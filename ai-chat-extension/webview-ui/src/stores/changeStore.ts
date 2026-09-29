import { create } from "zustand";

/**
 * Lifecycle of an agent change:
 *   awaiting  → the agent proposed it and is WAITING for Accept/Reject (nothing on disk yet)
 *   accepted  → user clicked Accept, backend is applying it
 *   applied   → written to disk (or command ran)
 *   rejected  → user clicked Reject; nothing changed
 *   expired   → nobody answered in time (or the run was cancelled); nothing changed
 *   reverted  → was applied, then undone via Revert
 * Commands: awaiting → running (after Run) → succeeded | failed (from the tool result)
 *
 * Review (aiChat.reviewEdits, like Cursor): edits are applied without asking, so an applied edit
 * starts with review "pending" until the user clicks Keep (review "kept") or Undo (status "reverted").
 */
export type ChangeStatus =
  | "awaiting"
  | "accepted"
  | "applied"
  | "rejected"
  | "expired"
  | "reverted"
  | "running"
  | "succeeded"
  | "failed";

export interface FileChange {
  id: string; // approvalId while awaiting; stays the card's id after it is applied
  changeId: string; // backend ChangeTracker ID (set once applied) — used for Revert
  approvalId?: string;
  /** The tool call this card belongs to: its tool row is hidden, this card shows the status */
  toolCallId?: string;
  kind: "file" | "command";
  command?: string;
  /** Command output (from the tool result) */
  output?: string;
  sessionId: string;
  filePath: string;
  patch: string;
  before?: string;
  after?: string;
  toolUsed: string;
  isNewFile?: boolean;
  /** The change deleted the file */
  isDeletion?: boolean;
  summary: string;
  patchSize: number;
  status: ChangeStatus;
  /** Applied without asking: waiting for Keep/Undo in the review */
  review?: "pending" | "kept";
  timestamp: number;
}

export interface ApprovalRequest {
  approvalId: string;
  toolCallId?: string;
  tool: string;
  kind: "file" | "command";
  filePath?: string | null;
  isNewFile?: boolean;
  before?: string | null;
  after?: string | null;
  patch?: string | null;
  command?: string | null;
  summary?: string;
  /** Auto mode ran it without asking: the card starts as "running", with no buttons */
  autoApproved?: boolean;
}

interface ChangeState {
  changes: FileChange[];
  selectedChangeId: string | null;

  addApproval: (approval: ApprovalRequest) => FileChange;
  /** Backend recorded the approved change: fill in changeId/patch and mark applied */
  completeApproval: (approvalId: string, applied: Partial<FileChange>) => boolean;
  /** An edit applied without an approval card (review mode, Auto mode): it waits for Keep/Undo */
  addAppliedChange: (change: Omit<FileChange, "id" | "status" | "timestamp" | "kind">) => FileChange;
  /** Keep: the edits stay, they leave the review */
  markKept: (ids: string[]) => void;
  /** Applied edits still waiting for Keep/Undo, oldest first */
  getPendingReview: () => FileChange[];
  setStatus: (id: string, status: ChangeStatus) => void;
  /** A command card's tool finished: record success/failure and its output */
  finishCommand: (toolCallId: string, ok: boolean, output: string) => boolean;
  selectChange: (id: string | null) => void;
  clearChanges: () => void;
  getAwaitingChanges: () => FileChange[];
  hydrateChanges: (changes: FileChange[]) => void;
}

export const useChangeStore = create<ChangeState>((set, get) => ({
  changes: [],
  selectedChangeId: null,

  addApproval: (approval) => {
    const change: FileChange = {
      id: approval.approvalId,
      changeId: "",
      approvalId: approval.approvalId,
      toolCallId: approval.toolCallId,
      kind: approval.kind,
      command: approval.command ?? undefined,
      sessionId: approval.approvalId.split(":")[0] ?? "",
      filePath: approval.filePath ?? "",
      patch: approval.patch ?? "",
      before: approval.before ?? undefined,
      after: approval.after ?? undefined,
      toolUsed: approval.tool,
      isNewFile: approval.isNewFile,
      summary: approval.summary ?? "",
      patchSize: approval.patch?.length ?? 0,
      status: approval.autoApproved ? "running" : "awaiting",
      timestamp: Date.now(),
    };
    set((state) => ({ changes: [...state.changes, change] }));
    return change;
  },

  completeApproval: (approvalId, applied) => {
    const existing = get().changes.find((c) => c.approvalId === approvalId);
    if (!existing) return false;
    set((state) => ({
      changes: state.changes.map((c) =>
        c.approvalId === approvalId
          ? { ...c, ...applied, id: c.id, approvalId, status: "applied" as const }
          : c,
      ),
    }));
    return true;
  },

  addAppliedChange: (change) => {
    const newChange: FileChange = {
      ...change,
      kind: "file",
      id:
        change.changeId ||
        `change-${Date.now()}-${Math.random().toString(36).slice(2, 11)}`,
      status: "applied",
      review: "pending",
      timestamp: Date.now(),
    };
    set((state) => ({ changes: [...state.changes, newChange] }));
    return newChange;
  },

  markKept: (ids) => {
    const keep = new Set(ids);
    set((state) => ({
      changes: state.changes.map((c) => (keep.has(c.id) && c.review === "pending" ? { ...c, review: "kept" as const } : c)),
    }));
  },

  getPendingReview: () => get().changes.filter(isPendingReview),

  setStatus: (id, status) => {
    set((state) => ({
      changes: state.changes.map((c) => (c.id === id ? { ...c, status } : c)),
    }));
  },

  finishCommand: (toolCallId, ok, output) => {
    const card = get().changes.find((c) => c.kind === "command" && c.toolCallId === toolCallId);
    if (!card) return false;
    set((state) => ({
      changes: state.changes.map((c) =>
        c.id === card.id
          ? {
              ...c,
              output,
              // A rejected/expired command never ran; keep that status
              status: c.status === "running" || c.status === "accepted" ? (ok ? "succeeded" : "failed") : c.status,
            }
          : c,
      ),
    }));
    return true;
  },

  selectChange: (id: string | null) => set({ selectedChangeId: id }),

  clearChanges: () => set({ changes: [], selectedChangeId: null }),

  getAwaitingChanges: () =>
    get().changes.filter((c) => c.status === "awaiting"),

  hydrateChanges: (changes) =>
    set({
      // Restored sessions: nothing can still be waiting (the run is gone), and the old
      // "pending" status meant "already on disk" before the approval gate existed.
      changes: changes.map((c) => {
        const status = c.status as string;
        return {
          ...c,
          kind: c.kind ?? "file",
          status:
            status === "pending"
              ? "applied"
              : status === "awaiting" || status === "accepted"
                ? "expired"
                : status === "running"
                  ? "expired" // window closed before the result arrived: outcome unknown
                  : c.status,
        };
      }),
      selectedChangeId: null,
    }),
}));

/** An applied edit nobody has kept or undone yet. */
export function isPendingReview(c: FileChange): boolean {
  return c.kind === "file" && c.status === "applied" && c.review === "pending" && Boolean(c.changeId);
}
