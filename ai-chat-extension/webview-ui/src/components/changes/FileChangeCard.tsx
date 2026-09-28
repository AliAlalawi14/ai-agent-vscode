import React, { useState } from "react";
import type { FileChange } from "../../stores/changeStore";
import {
  Check,
  X,
  FileCode,
  FilePlus,
  ExternalLink,
  ChevronDown,
  ChevronRight,
  Terminal,
  Undo2,
  Loader2,
} from "lucide-react";
import { PatchView, patchStats } from "./PatchView";
import { vscode } from "../../services/vscodeApi";

interface FileChangeCardProps {
  change: FileChange;
  onAccept: () => void;
  onReject: () => void;
  onRevert: () => void;
}

/** Status chip text, color and tooltip for each lifecycle state */
function statusChip(change: FileChange): { label: string; tone: string; title: string } | null {
  const isCommand = change.kind === "command";
  switch (change.status) {
    case "awaiting":
      return {
        label: isCommand ? "Needs approval" : "Not applied yet",
        tone: "text-warning bg-warning/10 border-warning/30",
        title: isCommand
          ? "The agent is waiting. Nothing runs until you click Run."
          : "The agent is waiting. Nothing is written until you click Accept.",
      };
    case "accepted":
      return { label: "Applying…", tone: "text-text-secondary bg-bg-tertiary border-border", title: "" };
    case "running":
      return { label: "Running…", tone: "text-accent bg-accent-subtle border-accent/20", title: "" };
    case "applied":
      return { label: "Applied", tone: "text-success bg-success/10 border-success/20", title: "Written to disk" };
    case "succeeded":
      return { label: "Succeeded", tone: "text-success bg-success/10 border-success/20", title: "Exit code 0" };
    case "failed":
      return { label: "Failed", tone: "text-error bg-error-subtle border-error/20", title: "Non-zero exit code" };
    case "rejected":
      return { label: "Rejected", tone: "text-text-muted bg-bg-tertiary border-border", title: "Nothing was changed" };
    case "expired":
      return {
        label: isCommand ? "No result" : "Not applied",
        tone: "text-text-muted bg-bg-tertiary border-border",
        title: "No decision/result was received before the run ended; nothing was changed by this card",
      };
    case "reverted":
      return { label: "Reverted", tone: "text-text-muted bg-bg-tertiary border-border", title: "Undone on disk" };
    default:
      return null;
  }
}

export const FileChangeCard: React.FC<FileChangeCardProps> = ({
  change,
  onAccept,
  onReject,
  onRevert,
}) => {
  const isAwaiting = change.status === "awaiting";
  const isCommand = change.kind === "command";
  // Diff open while the user has to decide; output open when a command failed
  const [expanded, setExpanded] = useState(isAwaiting || change.status === "failed");
  const fileName = change.filePath.split(/[/\\]/).pop() || change.filePath;
  const chip = statusChip(change);
  const stats = !isCommand && change.patch ? patchStats(change.patch) : null;
  const hasBody = isCommand ? Boolean(change.output) : Boolean(change.patch);

  const handleViewDiff = () => {
    vscode.postMessage({
      type: "openDiff",
      filePath: change.filePath,
      before: change.isNewFile ? "" : (change.before ?? undefined),
      after: change.after ?? undefined,
      proposed: isAwaiting,
      patch: change.patch,
      changeId: change.changeId || change.approvalId,
    });
  };

  const muted = ["rejected", "expired", "reverted"].includes(change.status);

  return (
    <div
      className={`my-1 rounded-lg border overflow-hidden animate-slide-up transition-all bg-bg-secondary
        ${isAwaiting ? "border-warning/40" : "border-border"} ${muted ? "opacity-60" : ""}`}
    >
      {/* Header: [icon name +a −r chip] ........ [actions] */}
      <div className="flex items-center gap-2 px-3 py-1.5">
        <button
          onClick={() => hasBody && setExpanded(!expanded)}
          className={`flex items-center gap-2 min-w-0 flex-1 text-left ${hasBody ? "cursor-pointer" : "cursor-default"}`}
          title={hasBody ? (expanded ? "Collapse" : "Expand") : undefined}
        >
          {hasBody ? (
            expanded ? (
              <ChevronDown size={12} className="text-text-muted shrink-0" />
            ) : (
              <ChevronRight size={12} className="text-text-muted shrink-0" />
            )
          ) : (
            <span className="w-3 shrink-0" />
          )}
          {isCommand ? (
            <Terminal size={13} className="text-text-muted shrink-0" />
          ) : change.isNewFile ? (
            <FilePlus size={13} className="text-accent shrink-0" />
          ) : (
            <FileCode size={13} className="text-text-muted shrink-0" />
          )}
          {isCommand ? (
            <code className="text-[12px] text-text-primary truncate">{change.command}</code>
          ) : (
            <span className="text-[12px] font-medium text-text-primary truncate" title={change.filePath}>
              {fileName}
            </span>
          )}
          {stats && (
            <span className="text-[11px] font-mono shrink-0">
              <span className="text-success">+{stats.added}</span>{" "}
              <span className="text-error">−{stats.removed}</span>
            </span>
          )}
          {chip && (
            <span
              className={`text-[10px] px-1.5 py-0.5 rounded border font-medium shrink-0 ${chip.tone}`}
              title={chip.title}
            >
              {change.status === "running" || change.status === "accepted" ? (
                <Loader2 size={9} className="inline animate-spin mr-1 -mt-px" />
              ) : null}
              {chip.label}
            </span>
          )}
        </button>

        <div className="flex items-center gap-1 shrink-0">
          {!isCommand && (
            <button
              onClick={handleViewDiff}
              className="p-1 rounded text-text-secondary hover:bg-bg-tertiary transition-colors"
              title="Open side-by-side diff in the editor"
            >
              <ExternalLink size={12} />
            </button>
          )}
          {isAwaiting && (
            <>
              <button
                onClick={(e) => {
                  e.stopPropagation();
                  onAccept();
                }}
                className="px-2 py-0.5 rounded text-[11px] font-medium text-white bg-success
                           hover:bg-success/80 transition-colors flex items-center gap-1"
                title={isCommand ? "Run this command (Alt+A)" : "Write this change (Alt+A)"}
              >
                <Check size={11} />
                {isCommand ? "Run" : "Accept"}
              </button>
              <button
                onClick={(e) => {
                  e.stopPropagation();
                  onReject();
                }}
                className="px-2 py-0.5 rounded text-[11px] font-medium text-text-secondary border border-border
                           hover:bg-error-subtle hover:text-error hover:border-error/30 transition-colors
                           flex items-center gap-1"
                title="Reject: nothing will be changed (Alt+R)"
              >
                <X size={11} />
                Reject
              </button>
            </>
          )}
          {change.status === "applied" && !isCommand && change.changeId && (
            <button
              onClick={(e) => {
                e.stopPropagation();
                onRevert();
              }}
              className="px-2 py-0.5 rounded text-[11px] text-text-secondary hover:bg-bg-tertiary
                         transition-colors flex items-center gap-1"
              title="Undo this change on disk"
            >
              <Undo2 size={11} />
              Revert
            </button>
          )}
        </div>
      </div>

      {/* Body: diff for files, output for commands */}
      {expanded && hasBody && (
        <div className="border-t border-border">
          {isCommand ? (
            <pre className="font-mono text-[11px] leading-[1.5] text-text-secondary px-3 py-2 max-h-[260px] overflow-auto whitespace-pre-wrap">
              {change.output}
            </pre>
          ) : (
            <PatchView patch={change.patch} />
          )}
        </div>
      )}
    </div>
  );
};
