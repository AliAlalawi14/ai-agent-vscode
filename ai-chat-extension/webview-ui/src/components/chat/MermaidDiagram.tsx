import React, { useEffect, useState } from "react";
import { CodeBlock } from "./CodeBlock";

let diagramCount = 0;

// Rendered SVGs keyed by theme + source, so a remount (or the same diagram shown again) doesn't re-run mermaid.
const svgCache = new Map<string, string>();
const failedCache = new Set<string>();

// Wait for the source to stop changing before rendering, so a diagram that is still streaming in
// isn't re-rendered on every token.
const RENDER_DELAY_MS = 400;

const isDarkTheme = () =>
  document.body.classList.contains("vscode-dark") || document.body.classList.contains("vscode-high-contrast");

/**
 * Renders a ```mermaid block (plans include flowcharts / sequence / ER diagrams, like Cursor's plans).
 * mermaid is loaded on first use only; if the diagram doesn't parse, the source is shown as code instead.
 */
export const MermaidDiagram: React.FC<{ code: string }> = React.memo(({ code }) => {
  const key = `${isDarkTheme() ? "dark" : "light"}:${code}`;
  const [svg, setSvg] = useState<string | null>(() => svgCache.get(key) ?? null);
  const [failed, setFailed] = useState(() => failedCache.has(key));

  useEffect(() => {
    const cached = svgCache.get(key);
    if (cached) {
      setSvg(cached);
      setFailed(false);
      return;
    }
    if (failedCache.has(key)) {
      setFailed(true);
      return;
    }

    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        const { default: mermaid } = await import("mermaid");
        mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: isDarkTheme() ? "dark" : "default" });
        const { svg } = await mermaid.render(`plan-diagram-${++diagramCount}`, code);
        svgCache.set(key, svg);
        if (!cancelled) {
          setSvg(svg);
          setFailed(false);
        }
      } catch {
        failedCache.add(key);
        if (!cancelled) setFailed(true);
      }
    }, RENDER_DELAY_MS);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [key, code]);

  if (failed) return <CodeBlock code={code} language="mermaid" />;
  if (!svg) return <div className="my-2 text-[11px] text-text-muted">Rendering diagram…</div>;
  return (
    <div
      className="my-2 p-2 rounded-md bg-bg-tertiary overflow-x-auto [&_svg]:max-w-full [&_svg]:h-auto"
      // mermaid's output with securityLevel "strict" (sanitized, no scripts or click handlers)
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  );
});
