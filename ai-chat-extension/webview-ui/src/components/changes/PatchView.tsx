import React from "react";

interface PatchLine {
  kind: "+" | "-" | " ";
  text: string;
  newNo?: number; // line number in the new file (none for removed lines)
}

interface PatchHunk {
  oldStart: number;
  newStart: number;
  lines: PatchLine[];
}

/** Parses a unified diff. Tolerates "\r" line endings and blank trailing lines. */
export function parsePatch(patch: string): PatchHunk[] {
  const hunks: PatchHunk[] = [];
  let current: PatchHunk | null = null;
  let newNo = 0;

  for (const raw of patch.split("\n").map((l) => l.replace(/\r+$/, ""))) {
    const header = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(raw);
    if (header) {
      current = { oldStart: +header[1], newStart: +header[2], lines: [] };
      newNo = +header[2];
      hunks.push(current);
      continue;
    }
    if (!current || raw.startsWith("+++") || raw.startsWith("---")) continue;
    const kind = raw[0];
    if (kind === "+" || kind === " ") {
      current.lines.push({ kind, text: raw.slice(1), newNo: newNo++ });
    } else if (kind === "-") {
      current.lines.push({ kind: "-", text: raw.slice(1) });
    }
  }
  return hunks;
}

/** "+11 −2" style counts for a card header. */
export function patchStats(patch: string): { added: number; removed: number } {
  let added = 0;
  let removed = 0;
  for (const hunk of parsePatch(patch)) {
    for (const line of hunk.lines) {
      if (line.kind === "+") added++;
      else if (line.kind === "-") removed++;
    }
  }
  return { added, removed };
}

/**
 * Compact unified diff for change cards: keeps indentation (white-space: pre), colors whole
 * lines, shows new-file line numbers, and replaces unchanged regions and raw "@@" headers
 * with a thin "⋯ N unchanged lines" separator.
 */
export const PatchView: React.FC<{ patch: string; maxHeight?: number }> = ({
  patch,
  maxHeight = 320,
}) => {
  const hunks = parsePatch(patch);
  if (hunks.length === 0) {
    return <div className="px-3 py-2 text-[11px] text-text-muted">No line changes.</div>;
  }

  const rows: React.ReactNode[] = [];
  let prevNewEnd = 1; // next new-file line after the previous hunk

  hunks.forEach((hunk, h) => {
    const skipped = hunk.newStart - prevNewEnd;
    if (skipped > 0) {
      rows.push(
        <div
          key={`gap-${h}`}
          className="px-3 py-0.5 text-[10px] text-text-muted bg-bg-tertiary/40 select-none"
        >
          ⋯ {skipped} unchanged line{skipped !== 1 ? "s" : ""}
        </div>,
      );
    }

    hunk.lines.forEach((line, i) => {
      const tone =
        line.kind === "+"
          ? "bg-[rgba(34,197,94,0.14)]"
          : line.kind === "-"
            ? "bg-[rgba(239,68,68,0.14)]"
            : "";
      const signTone =
        line.kind === "+" ? "text-success" : line.kind === "-" ? "text-error" : "text-text-muted";
      rows.push(
        <div key={`${h}-${i}`} className={`flex ${tone}`}>
          <span className="w-10 shrink-0 pr-2 text-right text-text-muted/70 select-none">
            {line.newNo ?? ""}
          </span>
          <span className={`w-4 shrink-0 select-none ${signTone}`}>
            {line.kind === " " ? "" : line.kind === "+" ? "+" : "−"}
          </span>
          <span className="whitespace-pre text-text-primary">{line.text || " "}</span>
        </div>,
      );
    });

    const newCount = hunk.lines.filter((l) => l.kind !== "-").length;
    prevNewEnd = hunk.newStart + newCount;
  });

  return (
    <div
      className="font-mono text-[11.5px] leading-[1.55] overflow-auto"
      style={{ maxHeight }}
    >
      <div className="min-w-max py-1">{rows}</div>
    </div>
  );
};
