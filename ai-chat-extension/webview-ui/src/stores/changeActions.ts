import { isPendingReview, useChangeStore, type FileChange } from "./changeStore";
import { vscode } from "../services/vscodeApi";

/** Accept a proposed change: the waiting agent applies it and continues. */
export function acceptChange(change: FileChange): void {
  if (change.status !== "awaiting" || !change.approvalId) return;
  useChangeStore.getState().setStatus(change.id, "accepted");
  vscode.postMessage({
    type: "resolveApproval",
    approvalId: change.approvalId,
    approved: true,
  });
}

/** Reject a proposed change: nothing is written; the agent is told and asks what to do instead. */
export function rejectChange(change: FileChange): void {
  if (change.status !== "awaiting" || !change.approvalId) return;
  useChangeStore.getState().setStatus(change.id, "rejected");
  vscode.postMessage({
    type: "resolveApproval",
    approvalId: change.approvalId,
    approved: false,
  });
}

/** Undo a change that was already applied, using the backend's recorded patch. */
export function revertAppliedChange(change: FileChange): void {
  if (change.status !== "applied" || change.kind !== "file" || !change.changeId) return;
  useChangeStore.getState().setStatus(change.id, "reverted");
  vscode.postMessage({
    type: "revertFile",
    filePath: change.filePath,
    changeId: change.changeId,
  });
}

// ── Review (edits applied without asking) ───────────────────────────────

/** Keep edits: they stay on disk and leave the review. */
export function keepChanges(changes: FileChange[]): void {
  useChangeStore.getState().markKept(changes.filter(isPendingReview).map((c) => c.id));
}

/**
 * Undo edits: newest first, one after the other in the extension, so each undo lands on the content the
 * next-newer edit left behind. Shown as reverted at once; a change that can't be undone comes back as applied.
 */
export function undoChanges(changes: FileChange[]): void {
  const toUndo = changes
    .filter(isPendingReview)
    .sort((a, b) => b.timestamp - a.timestamp);
  if (toUndo.length === 0) return;
  const store = useChangeStore.getState();
  toUndo.forEach((c) => store.setStatus(c.id, "reverted"));
  vscode.postMessage({
    type: "revertChanges",
    changes: toUndo.map((c) => ({ changeId: c.changeId, filePath: c.filePath })),
  });
}
