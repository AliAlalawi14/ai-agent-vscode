import { usePlanStore, requestRun } from "../../stores/planStore";
import { useSettingsStore } from "../../stores/settingsStore";
import { vscode } from "../../services/vscodeApi";

/**
 * Runs the plan from the plan card or the sticky plan bar. Executing needs a mode that can edit,
 * so Ask/Plan switch to Agent (Auto stays Auto); every change still goes through approval in Agent.
 */
export function executePlan(what: "step" | "all" | "selected", step?: number, selected?: number[]): void {
  const plan = usePlanStore.getState().plan;
  if (!plan) return;

  const { mode, setMode } = useSettingsStore.getState();
  if (mode === "ask" || mode === "plan") setMode("agent");

  if (what === "step" && step) {
    requestRun(`Implement step ${step} of the plan: ${plan.steps[step - 1].title}`);
  } else if (what === "selected" && selected && selected.length > 0) {
    // Cursor-style "build selected to-dos": only these steps, in plan order
    const steps = [...selected].sort((a, b) => a - b);
    requestRun(
      `Implement ONLY plan steps ${steps.join(", ")}, in order (leave the other steps pending):\n` +
        steps.map((n) => `${n}. ${plan.steps[n - 1].title}`).join("\n"),
    );
  } else {
    requestRun("Implement the remaining plan steps in order, one at a time.");
  }
}

/** Opens the plan's Markdown file in the editor (edits there update the card and the next build). */
export function openPlanFile(): void {
  const path = usePlanStore.getState().plan?.path;
  if (path) vscode.postMessage({ type: "openPlanFile", path });
}

/** Scrolls the conversation to the plan card. */
export function scrollToPlan(cardId: string): void {
  document.getElementById(cardId)?.scrollIntoView({ behavior: "smooth", block: "start" });
}
