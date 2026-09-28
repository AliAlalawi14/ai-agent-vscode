import React, { useState } from "react";
import { HelpCircle, Send, Check } from "lucide-react";
import { useChatStore, type PlanQuestion } from "../../stores/chatStore";
import { useSettingsStore } from "../../stores/settingsStore";
import { requestRun } from "../../stores/planStore";

/**
 * Plan mode's clarifying questions (Cursor-style): pick an option per question, or type your own,
 * then Submit. The answers go back as the next message and the agent writes the plan.
 */
export const QuestionsCard: React.FC<{
  messageId: string;
  questions: PlanQuestion[];
  answers?: string[];
}> = ({ messageId, questions, answers }) => {
  const isStreaming = useChatStore((s) => s.isStreaming);
  const [picked, setPicked] = useState<string[][]>(() => questions.map(() => []));
  const [other, setOther] = useState<string[]>(() => questions.map(() => ""));

  const answered = answers !== undefined;
  const answerFor = (i: number) => [...picked[i], other[i].trim()].filter((a) => a.length > 0).join(", ");
  const complete = questions.every((_, i) => answerFor(i).length > 0);

  const toggle = (i: number, option: string) => {
    if (answered) return;
    setPicked((prev) =>
      prev.map((sel, qi) => {
        if (qi !== i) return sel;
        if (questions[i].multiple) return sel.includes(option) ? sel.filter((o) => o !== option) : [...sel, option];
        return sel[0] === option ? [] : [option];
      }),
    );
  };

  const submit = () => {
    if (!complete || answered || isStreaming) return;
    const final = questions.map((_, i) => answerFor(i));
    useChatStore.getState().answerQuestions(messageId, final);
    // Stay in Plan mode: the answers are for the plan that comes next
    useSettingsStore.getState().setMode("plan");
    requestRun("Answers:\n" + questions.map((q, i) => `${i + 1}. ${q.question} → ${final[i]}`).join("\n"));
  };

  return (
    <div className="my-1 rounded-lg border border-accent/30 bg-bg-secondary overflow-hidden animate-slide-up">
      <div className="flex items-center gap-2 px-3 py-2 border-b border-border">
        <HelpCircle size={14} className="text-accent shrink-0" />
        <span className="text-[12.5px] font-semibold text-text-primary flex-1">
          {answered ? "Your answers" : "A few questions before the plan"}
        </span>
      </div>

      <ol className="px-3 py-2 space-y-3">
        {questions.map((q, i) => (
          <li key={i}>
            <div className="text-[12.5px] font-medium text-text-primary mb-1.5">
              <span className="text-text-muted mr-1">{i + 1}.</span>
              {q.question}
              {q.multiple && !answered && <span className="ml-1 text-[10.5px] text-text-muted">(pick any)</span>}
            </div>

            {answered ? (
              <div className="flex items-center gap-1.5 text-[12px] text-text-secondary">
                <Check size={12} className="text-success shrink-0" />
                {answers[i]}
              </div>
            ) : (
              <>
                <div className="flex flex-wrap gap-1.5">
                  {q.options.map((option) => {
                    const on = picked[i].includes(option);
                    return (
                      <button
                        key={option}
                        onClick={() => toggle(i, option)}
                        className={`px-2 py-1 rounded-md text-[11.5px] border transition-colors ${
                          on
                            ? "bg-accent text-white border-accent"
                            : "border-border text-text-secondary hover:bg-bg-tertiary"
                        }`}
                      >
                        {option}
                      </button>
                    );
                  })}
                </div>
                <input
                  value={other[i]}
                  onChange={(e) => setOther((prev) => prev.map((v, qi) => (qi === i ? e.target.value : v)))}
                  onKeyDown={(e) => e.key === "Enter" && submit()}
                  placeholder="Other…"
                  className="mt-1.5 w-full px-2 py-1 rounded-md bg-bg-tertiary border border-border text-[11.5px]
                             text-text-primary placeholder:text-text-muted focus:outline-none focus:border-accent/50"
                />
              </>
            )}
          </li>
        ))}
      </ol>

      {!answered && (
        <div className="flex items-center gap-2 px-3 py-2 border-t border-border">
          <button
            disabled={!complete || isStreaming}
            onClick={submit}
            className="flex items-center gap-1 px-2.5 py-1 rounded-md text-[11px] font-medium text-white
                       bg-accent hover:bg-accent/80 disabled:opacity-40 transition-colors"
          >
            <Send size={11} />
            Submit answers
          </button>
          <span className="text-[10px] text-text-muted ml-auto">
            {complete ? "The agent writes the plan next" : "Answer every question"}
          </span>
        </div>
      )}
    </div>
  );
};
