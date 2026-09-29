import React, { useEffect, useRef, useState } from "react";
import { ChevronDown, MessageCircleQuestion, ListChecks, Bot, Zap } from "lucide-react";
import { useSettingsStore, type AgentMode } from "../../stores/settingsStore";
import { useSetupStore } from "../../stores/setupStore";

const MODES: {
  id: AgentMode;
  label: string;
  icon: React.ComponentType<{ size?: number; className?: string }>;
  description: string;
}[] = [
  { id: "ask", label: "Ask", icon: MessageCircleQuestion, description: "Questions and explanations. Read-only: never changes files." },
  { id: "plan", label: "Plan", icon: ListChecks, description: "Investigates read-only, then proposes a step-by-step plan you can execute." },
  { id: "agent", label: "Agent", icon: Bot, description: "Edits files and runs commands. Each change waits for your Accept." },
  { id: "auto", label: "Auto", icon: Zap, description: "Like Agent, but edits and read-only/build commands (build, test, git status…) run without asking." },
];

/** Mode picker shown above the input (like Cursor's Ask/Agent switch). */
export const ModeSelector: React.FC = () => {
  const mode = useSettingsStore((s) => s.mode);
  const setMode = useSettingsStore((s) => s.setMode);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const reviewEdits = useSetupStore((s) => s.reviewEdits);
  // Agent mode's wording follows the review setting
  const modes = MODES.map((m) =>
    m.id === "agent" && reviewEdits
      ? { ...m, description: "Edits files right away; you Keep or Undo them afterwards. Commands wait for your approval." }
      : m,
  );
  const current = modes.find((m) => m.id === mode) ?? modes[2];

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener("mousedown", close);
    return () => window.removeEventListener("mousedown", close);
  }, [open]);

  const tone =
    mode === "auto"
      ? "text-warning border-warning/30 bg-warning/10"
      : mode === "agent"
        ? "text-accent border-accent/30 bg-accent-subtle"
        : "text-text-secondary border-border bg-bg-secondary";

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen(!open)}
        className={`flex items-center gap-1 px-2 py-0.5 rounded-md border text-[11px] font-medium transition-colors ${tone}`}
        title={current.description}
      >
        <current.icon size={11} />
        {current.label}
        <ChevronDown size={10} className="opacity-70" />
      </button>

      {open && (
        <div
          className="absolute bottom-full mb-1 left-0 z-20 w-[260px] rounded-lg border border-border
                     bg-bg-secondary shadow-lg py-1 animate-fade-in"
        >
          {modes.map((m) => (
            <button
              key={m.id}
              onClick={() => {
                setMode(m.id);
                setOpen(false);
              }}
              className={`w-full flex items-start gap-2 px-2.5 py-1.5 text-left hover:bg-bg-tertiary transition-colors
                ${m.id === mode ? "bg-bg-tertiary" : ""}`}
            >
              <m.icon size={13} className={`mt-0.5 shrink-0 ${m.id === mode ? "text-accent" : "text-text-muted"}`} />
              <span className="min-w-0">
                <span className="block text-[12px] font-medium text-text-primary">{m.label}</span>
                <span className="block text-[11px] text-text-muted leading-snug">{m.description}</span>
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
};
