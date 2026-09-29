import React, { useMemo, useState } from "react";
import { useShallow } from "zustand/react/shallow";
import { Check, ChevronDown, ChevronRight, ExternalLink, FileCode, FileMinus, FilePlus, Undo2 } from "lucide-react";
import { isPendingReview, useChangeStore, type FileChange } from "../../stores/changeStore";
import { keepChanges, undoChanges } from "../../stores/changeActions";
import { patchStats } from "./PatchView";
import { useChatStore } from "../../stores/chatStore";
import { verifySummary, type VerifyData } from "./VerifyCard";
import { vscode } from "../../services/vscodeApi";

interface FileReview {
  filePath: string;
  /** Oldest first */
  changes: FileChange[];
  added: number;
  removed: number;
  isNewFile: boolean;
  isDeletion: boolean;
}

/** Pending edits grouped by file, in the order the files were first touched. */
function groupByFile(changes: FileChange[]): FileReview[] {
  const files = new Map<string, FileReview>();
  for (const c of [...changes].sort((a, b) => a.timestamp - b.timestamp)) {
    const stats = c.patch ? patchStats(c.patch) : { added: 0, removed: 0 };
    const file = files.get(c.filePath);
    if (file) {
      file.changes.push(c);
      file.added += stats.added;
      file.removed += stats.removed;
      file.isDeletion = Boolean(c.isDeletion);
    } else {
      files.set(c.filePath, {
        filePath: c.filePath,
        changes: [c],
        added: stats.added,
        removed: stats.removed,
        isNewFile: Boolean(c.isNewFile),
        isDeletion: Boolean(c.isDeletion),
      });
    }
  }
  return Array.from(files.values());
}

/** Diff of the whole file: before the agent's first edit (left) vs now (right). */
function openFileDiff(file: FileReview): void {
  const first = file.changes[0];
  const last = file.changes[file.changes.length - 1];
  const before = first.isNewFile ? "" : first.before;
  vscode.postMessage({
    type: "openDiff",
    filePath: file.filePath,
    before,
    // Without the full "before" (very large file) only a single edit's patch can rebuild it
    ...(before === undefined && file.changes.length === 1 ? { patch: last.patch } : {}),
    changeId: `review-${last.changeId}`,
  });
}

/**
 * Cursor-style review of edits the agent applied without asking: every changed file with Keep / Undo,
 * plus Keep all / Undo all. Shown above the input once the run is over.
 */
export const ReviewBar: React.FC = () => {
  const pending = useChangeStore(useShallow((state) => state.changes.filter(isPendingReview)));
  const files = useMemo(() => groupByFile(pending), [pending]);
  const [expanded, setExpanded] = useState(true);
  // The latest check of the agent's changes, shown next to the file count
  const verify = useChatStore((state) => {
    for (let i = state.messages.length - 1; i >= 0; i--) {
      const seg = [...state.messages[i].segments].reverse().find((s) => s.type === "verify");
      if (seg && seg.type === "verify") return seg.verify;
    }
    return null;
  }) as VerifyData | null;

  if (files.length === 0) return null;

  const added = files.reduce((n, f) => n + f.added, 0);
  const removed = files.reduce((n, f) => n + f.removed, 0);

  return (
    <div className="mx-4 mb-1 rounded-lg border border-accent/25 bg-bg-secondary animate-slide-up overflow-hidden">
      <div className="flex items-center gap-2 px-3 py-1.5">
        <button
          onClick={() => setExpanded(!expanded)}
          className="flex items-center gap-1.5 min-w-0 flex-1 text-left"
          title={expanded ? "Hide files" : "Show files"}
        >
          {expanded ? (
            <ChevronDown size={12} className="text-text-muted shrink-0" />
          ) : (
            <ChevronRight size={12} className="text-text-muted shrink-0" />
          )}
          <span className="text-[12px] font-medium text-text-primary truncate">
            {files.length} file{files.length === 1 ? "" : "s"} changed
          </span>
          <span className="text-[11px] font-mono shrink-0">
            <span className="text-success">+{added}</span> <span className="text-error">−{removed}</span>
          </span>
        </button>
        {verify && verify.status !== "unavailable" && (
          <span
            title={verifySummary(verify)}
            className={`shrink-0 max-w-[45%] truncate text-[11px] px-1.5 py-0.5 rounded border
              ${verify.status === "passed" ? "text-success border-success/30 bg-success/10"
                : verify.status === "failed" ? "text-error border-error/30 bg-error-subtle"
                  : "text-text-secondary border-border"}`}
          >
            {verify.status === "passed" ? "✓ " : verify.status === "failed" ? "✗ " : "… "}
            {verify.status === "running" ? "Checking…" : verifySummary(verify)}
          </span>
        )}
        <button
          onClick={() => undoChanges(pending)}
          className="shrink-0 flex items-center gap-1 px-2 py-0.5 rounded text-[11px] text-text-secondary border border-border
                     hover:bg-error-subtle hover:text-error hover:border-error/30 transition-colors"
          title="Undo every edit listed here"
        >
          <Undo2 size={11} /> Undo all
        </button>
        <button
          onClick={() => keepChanges(pending)}
          className="shrink-0 flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-medium text-white bg-accent
                     hover:bg-accent-hover transition-colors"
          title="Keep every edit listed here"
        >
          <Check size={11} /> Keep all
        </button>
      </div>

      {expanded && (
        <div className="border-t border-border max-h-48 overflow-y-auto">
          {files.map((file) => {
            const name = file.filePath.split(/[/\\]/).pop() || file.filePath;
            const dir = file.filePath.slice(0, file.filePath.length - name.length).replace(/[/\\]$/, "");
            const Icon = file.isDeletion ? FileMinus : file.isNewFile ? FilePlus : FileCode;
            return (
              <div key={file.filePath} className="group flex items-center gap-2 px-3 py-1 hover:bg-bg-tertiary">
                <Icon
                  size={12}
                  className={`shrink-0 ${file.isDeletion ? "text-error" : file.isNewFile ? "text-accent" : "text-text-muted"}`}
                />
                <button
                  onClick={() => openFileDiff(file)}
                  disabled={file.isDeletion}
                  className="min-w-0 flex-1 text-left flex items-baseline gap-1.5"
                  title={file.isDeletion ? `${file.filePath} (deleted)` : `Open the diff of ${file.filePath}`}
                >
                  <span className={`text-[12px] truncate ${file.isDeletion ? "line-through text-text-muted" : "text-text-primary"}`}>
                    {name}
                  </span>
                  {dir && <span className="text-[10px] text-text-muted truncate">{dir}</span>}
                </button>
                {file.changes.length > 1 && (
                  <span className="text-[10px] text-text-muted shrink-0">{file.changes.length} edits</span>
                )}
                <span className="text-[11px] font-mono shrink-0">
                  <span className="text-success">+{file.added}</span> <span className="text-error">−{file.removed}</span>
                </span>
                <div className="flex items-center gap-0.5 shrink-0">
                  {!file.isDeletion && (
                    <button
                      onClick={() => openFileDiff(file)}
                      className="p-1 rounded text-text-muted hover:text-text-primary hover:bg-bg-active"
                      title="Open the diff in the editor"
                    >
                      <ExternalLink size={11} />
                    </button>
                  )}
                  <button
                    onClick={() => undoChanges(file.changes)}
                    className="p-1 rounded text-text-muted hover:text-error hover:bg-error-subtle"
                    title={`Undo ${file.changes.length === 1 ? "this edit" : `all ${file.changes.length} edits`} to ${name}`}
                  >
                    <Undo2 size={12} />
                  </button>
                  <button
                    onClick={() => keepChanges(file.changes)}
                    className="p-1 rounded text-text-muted hover:text-success hover:bg-success/10"
                    title={`Keep the changes to ${name}`}
                  >
                    <Check size={12} />
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
};
