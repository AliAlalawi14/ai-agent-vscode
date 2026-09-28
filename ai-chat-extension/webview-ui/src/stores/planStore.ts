import { create } from "zustand";

export type PlanStepStatus = "pending" | "in_progress" | "done" | "skipped";

export interface PlanStep {
  title: string;
  files: string[];
  details: string;
  status: PlanStepStatus;
}

export interface Plan {
  title: string;
  /** Markdown overview: goal, approach, design, decisions, open questions */
  summary?: string;
  steps: PlanStep[];
  /** The plan's Markdown file (.ai/plans/*.plan.md); requests send it so the backend reads the user's edits */
  path?: string;
}

interface PlanState {
  /** The current conversation's plan (from Plan mode); sent with every request as the ACTIVE PLAN */
  plan: Plan | null;
  setPlan: (plan: Plan) => void;
  /** A plan file changed on disk: take its content if it is this conversation's plan */
  applyFileUpdate: (plan: Plan) => void;
  updateStep: (stepNumber: number, status: PlanStepStatus) => void;
  clearPlan: () => void;
}

export const usePlanStore = create<PlanState>((set) => ({
  plan: null,

  setPlan: (plan) =>
    set({
      plan: {
        title: plan.title,
        summary: plan.summary ?? "",
        path: plan.path ?? undefined,
        steps: plan.steps.map((s) => ({
          title: s.title,
          files: s.files ?? [],
          details: s.details ?? "",
          status: s.status ?? "pending",
        })),
      },
    }),

  applyFileUpdate: (plan) => {
    const current = usePlanStore.getState().plan;
    if (!current?.path || current.path !== plan.path) return;
    usePlanStore.getState().setPlan(plan);
  },

  updateStep: (stepNumber, status) =>
    set((state) => {
      if (!state.plan) return state;
      return {
        plan: {
          ...state.plan,
          steps: state.plan.steps.map((s, i) => (i === stepNumber - 1 ? { ...s, status } : s)),
        },
      };
    }),

  clearPlan: () => set({ plan: null }),
}));

/** First step that is not finished (1-based), or null when all are done/skipped */
export function nextPendingStep(plan: Plan | null): number | null {
  if (!plan) return null;
  const idx = plan.steps.findIndex((s) => s.status === "pending" || s.status === "in_progress");
  return idx >= 0 ? idx + 1 : null;
}

/** Ask the chat to send a message (used by the plan card's Execute buttons). */
export function requestRun(task: string): void {
  window.dispatchEvent(new CustomEvent("ai-agent:run", { detail: task }));
}
