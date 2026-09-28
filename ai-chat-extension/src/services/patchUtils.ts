/**
 * Rebuilds the file content from BEFORE a change by reverse-applying its unified diff
 * to the current content. Used when a change event carries only the patch
 * (large files, or conversations restored from history).
 *
 * Returns null if the patch doesn't match the current file (it was edited again since).
 */
export function reverseApplyUnifiedPatch(
  current: string,
  patch: string,
): string | null {
  const eol = current.includes("\r\n") ? "\r\n" : "\n";
  const lines = current.split(/\r?\n/);
  const hunks = parseHunks(patch);
  if (hunks.length === 0) {
    return null;
  }

  // Apply bottom-up so earlier hunks' line numbers stay valid
  for (const hunk of [...hunks].sort((a, b) => b.newStart - a.newStart)) {
    const newLines = hunk.lines
      .filter((l) => l.kind !== "-")
      .map((l) => l.text);
    const oldLines = hunk.lines
      .filter((l) => l.kind !== "+")
      .map((l) => l.text);

    // Unified diff lines are 1-based; a count of 0 means "insert after line N"
    const start = newLines.length === 0 ? hunk.newStart : hunk.newStart - 1;
    const actual = lines.slice(start, start + newLines.length);
    const matches = actual.every(
      (text, i) => text.trimEnd() === newLines[i].trimEnd(),
    );
    if (!matches || actual.length !== newLines.length) {
      return null;
    }
    lines.splice(start, newLines.length, ...oldLines);
  }

  return lines.join(eol);
}

interface Hunk {
  newStart: number;
  lines: { kind: " " | "+" | "-"; text: string }[];
}

function parseHunks(patch: string): Hunk[] {
  const hunks: Hunk[] = [];
  let current: Hunk | null = null;

  // The backend splits files on "\n", so patch lines of CRLF files keep a trailing "\r"
  for (const raw of patch.split("\n").map((l) => l.replace(/\r+$/, ""))) {
    const header = /^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(raw);
    if (header) {
      current = { newStart: parseInt(header[1], 10), lines: [] };
      hunks.push(current);
      continue;
    }
    if (!current || raw.startsWith("+++") || raw.startsWith("---")) {
      continue;
    }
    const kind = raw[0];
    if (kind === "+" || kind === "-" || kind === " ") {
      current.lines.push({ kind, text: raw.slice(1) });
    } else if (raw === "") {
      // Some diff writers drop the leading space on empty context lines
      current.lines.push({ kind: " ", text: "" });
    }
  }
  // A trailing empty line from the final newline is not part of the last hunk
  for (const h of hunks) {
    while (h.lines.length > 0) {
      const last = h.lines[h.lines.length - 1];
      if (last.kind === " " && last.text === "") {
        h.lines.pop();
      } else {
        break;
      }
    }
  }
  return hunks;
}
