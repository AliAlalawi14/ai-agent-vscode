import { useChangeStore, type FileChange } from "./changeStore";
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
