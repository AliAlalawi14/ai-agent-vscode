import React from "react";
import { ListChecks, Play, Hammer, ArrowUp, Loader2 } from "lucide-react";
import { usePlanStore, nextPendingStep } from "../../stores/planStore";
import { useChatStore } from "../../stores/chatStore";
import { executePlan } from "./planActions";

/**
 * Sticky plan progress above the input, so the next step can be run without
 * scrolling back to the plan card.
 */
export const PlanBar: React.FC<{ onView: () => void }> = ({ onView }) => {
  const plan = usePlanStore((s) => s.plan);
  const isStreaming = useChatStore((s) => s.isStreaming);
  if (!plan) return null;

  const next = nextPendingStep(plan);
  const done = plan.steps.filter((s) => s.status === "done").length;
  const inProgress = plan.steps.findIndex((s) => s.status === "in_progress");
  const focusStep = inProgress >= 0 ? inProgress + 1 : next;

  return (
    <div
      className="mx-4 mb-1 flex items-center gap-2 px-3 py-1.5 rounded-md bg-bg-secondary
                 border border-accent/25 animate-slide-up text-[11px]"
    >
      <ListChecks size={12} className="text-accent shrink-0" />
      <span className="text-text-muted shrink-0">
        Plan {done}/{plan.steps.length}
      </span>
      <span className="text-text-secondary truncate flex-1" title={focusStep ? plan.steps[focusStep - 1].title : ""}>
        {focusStep ? (
          <>
            {inProgress >= 0 && isStreaming ? (
              <Loader2 size={10} className="inline animate-spin mr-1 -mt-px text-accent" />
            ) : null}
            {inProgress >= 0 && isStreaming ? "Working on" : "Next"}: {focusStep}. {plan.steps[focusStep - 1].title}
          </>
        ) : (
          <span className="text-success">All steps done</span>
        )}
      </span>

      {next && !isStreaming && (
        <>
          <button
            onClick={() => executePlan("step", next)}
            className="flex items-center gap-1 px-2 py-0.5 rounded font-medium text-white bg-accent
                       hover:bg-accent/80 transition-colors shrink-0"
          >
            <Play size={10} />
            Step {next}
          </button>
          <button
            onClick={() => executePlan("all")}
            className="flex items-center gap-1 px-2 py-0.5 rounded font-medium text-text-secondary border border-border
                       hover:bg-bg-tertiary transition-colors shrink-0"
            title="Build the remaining steps in order"
          >
            <Hammer size={10} />
            Build
          </button>
        </>
      )}
      <button
        onClick={onView}
        className="p-0.5 rounded text-text-muted hover:text-text-primary hover:bg-bg-tertiary shrink-0"
        title="Show the plan"
      >
        <ArrowUp size={12} />
      </button>
    </div>
  );
};
