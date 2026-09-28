import React, { useEffect, useState } from "react";
import { CodeBlock } from "./CodeBlock";

let diagramCount = 0;

/**
 * Renders a ```mermaid block (plans include flowcharts / sequence / ER diagrams, like Cursor's plans).
 * mermaid is loaded on first use only; if the diagram doesn't parse, the source is shown as code instead.
 */
export const MermaidDiagram: React.FC<{ code: string }> = ({ code }) => {
  const [svg, setSvg] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const { default: mermaid } = await import("mermaid");
        const dark = document.body.classList.contains("vscode-dark") ||
          document.body.classList.contains("vscode-high-contrast");
        mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: dark ? "dark" : "default" });
        const { svg } = await mermaid.render(`plan-diagram-${++diagramCount}`, code);
        if (!cancelled) setSvg(svg);
      } catch {
        if (!cancelled) setFailed(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [code]);

  if (failed) return <CodeBlock code={code} language="mermaid" />;
  if (!svg) return <div className="my-2 text-[11px] text-text-muted">Rendering diagram…</div>;
  return (
    <div
      className="my-2 p-2 rounded-md bg-bg-tertiary overflow-x-auto [&_svg]:max-w-full [&_svg]:h-auto"
      // mermaid's output with securityLevel "strict" (sanitized, no scripts or click handlers)
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  );
};
