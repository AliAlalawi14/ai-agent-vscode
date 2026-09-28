import * as fs from "fs";
import * as path from "path";

/**
 * Detects a stale Extension Development Host: the bundles on disk were rebuilt (webpack watch /
 * vite build) after this window loaded them, so the running code is older than the source.
 * In a packaged .vsix the files never change, so this never fires there.
 */
const loaded = new Map<string, number>();

const BUNDLES = [
  path.join("dist", "extension.js"),
  path.join("webview-ui", "build", "assets", "index.js"),
];

function mtimeMs(file: string): number | null {
  try {
    return fs.statSync(file).mtimeMs;
  } catch {
    return null;
  }
}

/** Call once per load: at activation for the extension, when the webview is created for the UI. */
export function recordLoadedBundles(extensionPath: string): void {
  for (const rel of BUNDLES) {
    const full = path.join(extensionPath, rel);
    const time = mtimeMs(full);
    if (time !== null) {
      loaded.set(full, time);
    }
  }
}

/** Relative paths of bundles rebuilt since they were loaded (1 s tolerance). */
export function findStaleBundles(extensionPath: string): string[] {
  const stale: string[] = [];
  for (const rel of BUNDLES) {
    const full = path.join(extensionPath, rel);
    const before = loaded.get(full);
    const now = mtimeMs(full);
    if (before !== undefined && now !== null && now - before > 1000) {
      stale.push(rel);
    }
  }
  return stale;
}
