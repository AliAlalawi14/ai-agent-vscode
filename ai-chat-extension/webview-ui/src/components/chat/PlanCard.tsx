import React, { useState } from "react";
import {
  CheckCircle2,
  Circle,
  Loader2,
  MinusCircle,
  ListChecks,
  Play,
  PlayCircle,
  ChevronDown,
  ChevronRight,
  FileCode,
  FileText,
  Hammer,
} from "lucide-react";
import { usePlanStore, nextPendingStep, type PlanStep } from "../../stores/planStore";
import { useChatStore } from "../../stores/chatStore";
import { MarkdownRenderer } from "./MarkdownRenderer";
import { executePlan, openPlanFile } from "./planActions";

export const PLAN_CARD_ID = "ai-agent-plan-card";

const StepIcon: React.FC<{ status: PlanStep["status"] }> = ({ status }) => {
  if (status === "done") return <CheckCircle2 size={14} className="text-success shrink-0 mt-0.5" />;
  if (status === "in_progress") return <Loader2 size={14} className="text-accent animate-spin shrink-0 mt-0.5" />;
  if (status === "skipped") return <MinusCircle size={14} className="text-text-muted shrink-0 mt-0.5" />;
  return <Circle size={14} className="text-text-muted shrink-0 mt-0.5" />;
};

/**
 * The conversation's plan: an Overview (goal, approach, design, decisions, open questions)
 * and a live checklist whose steps show their files and details inline.
 */
export const PlanCard: React.FC = () => {
  const plan = usePlanStore((s) => s.plan);
  const isStreaming = useChatStore((s) => s.isStreaming);
  const [showOverview, setShowOverview] = useState(true);
  const [selected, setSelected] = useState<number[]>([]);

  if (!plan) return null;
  const toggleSelected = (n: number) =>
    setSelected((prev) => (prev.includes(n) ? prev.filter((x) => x !== n) : [...prev, n]));
  const next = nextPendingStep(plan);
  const doneCount = plan.steps.filter((s) => s.status === "done").length;

  return (
    <div
      id={PLAN_CARD_ID}
      className="my-1 rounded-lg border border-accent/30 bg-bg-secondary overflow-hidden animate-slide-up scroll-mt-4"
    >
      <div className="flex items-center gap-2 px-3 py-2 border-b border-border">
        <ListChecks size={14} className="text-accent shrink-0" />
        <span className="text-[12.5px] font-semibold text-text-primary flex-1 truncate">{plan.title}</span>
        <span className="text-[11px] text-text-muted shrink-0">
          {doneCount}/{plan.steps.length} done
        </span>
        {plan.path && (
          <button
            onClick={openPlanFile}
            className="flex items-center gap-1 px-1.5 py-0.5 rounded text-[10.5px] text-text-secondary
                       hover:bg-bg-tertiary hover:text-text-primary shrink-0"
            title={`Open ${plan.path}: edit it like any file; the card and the next build use your edits`}
          >
            <FileText size={11} />
            Open plan file
          </button>
        )}
      </div>

      {/* Overview: the reasoning behind the steps */}
      {plan.summary && (
        <div className="border-b border-border">
          <button
            onClick={() => setShowOverview(!showOverview)}
            className="w-full flex items-center gap-1.5 px-3 py-1.5 text-[11px] font-medium text-text-secondary hover:bg-bg-tertiary"
          >
            {showOverview ? <ChevronDown size={11} /> : <ChevronRight size={11} />}
            Overview
          </button>
          {showOverview && (
            <div className="px-4 pb-2 text-[12.5px] leading-relaxed text-text-primary">
              <MarkdownRenderer content={plan.summary} />
            </div>
          )}
        </div>
      )}

      {/* Steps with their details inline */}
      <ol className="py-1.5">
        {plan.steps.map((step, i) => {
          const n = i + 1;
          const isNext = n === next;
          const finished = step.status === "done" || step.status === "skipped";
          return (
            <li key={n} className={`px-3 py-1.5 flex gap-2 ${isNext ? "bg-accent-subtle/40" : ""}`}>
              {!finished && (
                <input
                  type="checkbox"
                  checked={selected.includes(n)}
                  onChange={() => toggleSelected(n)}
                  disabled={isStreaming}
                  className="mt-1 shrink-0 accent-[var(--color-accent,#007acc)]"
                  title="Select to build only some steps"
                />
              )}
              <StepIcon status={step.status} />
              <div className="min-w-0 flex-1">
                <div
                  className={`text-[12.5px] font-medium leading-snug ${finished ? "text-text-muted" : "text-text-primary"}`}
                >
                  <span className="text-text-muted mr-1">{n}.</span>
                  {step.title}
                  {isNext && !isStreaming && (
                    <span className="ml-2 text-[10px] font-normal text-accent">next</span>
                  )}
                </div>
                {step.details && (
                  <p className={`text-[11.5px] leading-snug mt-0.5 ${finished ? "text-text-muted" : "text-text-secondary"}`}>
                    {step.details}
                  </p>
                )}
                {step.files.length > 0 && (
                  <div className="flex flex-wrap gap-1 mt-1">
                    {step.files.map((f) => (
                      <span
                        key={f}
                        className="inline-flex items-center gap-1 px-1.5 py-px rounded bg-bg-tertiary text-[10.5px] font-mono text-text-muted"
                      >
                        <FileCode size={9} />
                        {f}
                      </span>
                    ))}
                  </div>
                )}
              </div>
            </li>
          );
        })}
      </ol>

      <div className="flex items-center gap-2 px-3 py-2 border-t border-border">
        {next ? (
          <>
            <button
              disabled={isStreaming}
              onClick={() => executePlan("all")}
              className="flex items-center gap-1 px-2.5 py-1 rounded-md text-[11px] font-medium text-white
                         bg-accent hover:bg-accent/80 disabled:opacity-40 transition-colors"
              title="Build the remaining steps in order. Switches to Agent mode if needed; each change still asks for approval"
            >
              <Hammer size={11} />
              Build
            </button>
            {selected.length > 0 ? (
              <button
                disabled={isStreaming}
                onClick={() => {
                  executePlan("selected", undefined, selected);
                  setSelected([]);
                }}
                className="flex items-center gap-1 px-2.5 py-1 rounded-md text-[11px] font-medium text-text-secondary
                           border border-border hover:bg-bg-tertiary disabled:opacity-40 transition-colors"
              >
                <PlayCircle size={11} />
                Build selected ({selected.length})
              </button>
            ) : (
              <button
                disabled={isStreaming}
                onClick={() => executePlan("step", next)}
                className="flex items-center gap-1 px-2.5 py-1 rounded-md text-[11px] font-medium text-text-secondary
                           border border-border hover:bg-bg-tertiary disabled:opacity-40 transition-colors"
              >
                <Play size={11} />
                Build step {next}
              </button>
            )}
            <span className="text-[10px] text-text-muted ml-auto">or type "start" / "continue"</span>
          </>
        ) : (
          <span className="text-[11px] text-success">All steps done.</span>
        )}
      </div>
    </div>
  );
};
