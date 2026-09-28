import React, { useState, useCallback, useRef, useEffect } from "react";
import {
  ArrowUp,
  AtSign,
  Square,
  CornerDownLeft,
  FilePlus,
  ListChecks,
  X,
} from "lucide-react";
import { useMentions } from "../../hooks/useMentions";
import { useMentionStore } from "../../stores/mentionStore";
import { useSettingsStore, type AgentMode } from "../../stores/settingsStore";
import { usePlanStore } from "../../stores/planStore";

/** Shift+Tab cycles through the modes (like Cursor's Shift+Tab into Plan mode). */
const MODE_CYCLE: AgentMode[] = ["ask", "plan", "agent", "auto"];

/** Words that usually mean a multi-file feature: worth planning first. */
const COMPLEX_TASK = /\b(build|implement|add (a |an |the )?(new )?feature|refactor|redesign|design|architecture|migrate|system)\b/i;
import { MentionDropdown } from "../mentions/MentionDropdown";
import { ContextChip } from "../mentions/ContextChip";

interface MessageInputProps {
  onSend: (message: string, context?: any[]) => void;
  onStop?: () => void;
  disabled?: boolean;
  isStreaming?: boolean;
  placeholder?: string;
}

export const MessageInput: React.FC<MessageInputProps> = ({
  onSend,
  onStop,
  disabled = false,
  isStreaming = false,
  placeholder = "Ask anything... @ to mention files, Shift+Enter for newline",
}) => {
  const [value, setValue] = useState("");
  const [isFocused, setIsFocused] = useState(false);
  const [planHintDismissed, setPlanHintDismissed] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const mode = useSettingsStore((s) => s.mode);
  const setMode = useSettingsStore((s) => s.setMode);
  const hasPlan = usePlanStore((s) => s.plan !== null);
  // Cursor suggests Plan mode for complex requests; so do we (only in Agent mode, without a plan yet)
  const suggestPlan =
    mode === "agent" && !hasPlan && !planHintDismissed &&
    (value.trim().length > 200 || COMPLEX_TASK.test(value));

  const context = useMentionStore((state) => state.context);
  const removeContext = useMentionStore((state) => state.removeContext);
  const clearContext = useMentionStore((state) => state.clearContext);

  const {
    showDropdown,
    results,
    prioritizedResults,
    activeIndex,
    isSearching,
    query,
    handleMentionDetection,
    handleMentionKeyDown,
    selectResult,
    openMentionPicker,
  } = useMentions({ textareaRef, value, setValue });

  const handleSubmit = useCallback(() => {
    const trimmed = value.trim();
    if (!trimmed || disabled) return;

    const contextPayload =
      context.length > 0
        ? context.map((c) => ({
            type: c.kind,
            name: c.name,
            filePath: c.filePath,
            className: c.className,
            symbolType: c.symbolType,
          }))
        : undefined;

    onSend(trimmed, contextPayload);
    setValue("");
    clearContext();

    if (textareaRef.current) {
      textareaRef.current.style.height = "auto";
    }
  }, [value, disabled, onSend, context, clearContext]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (handleMentionKeyDown(e)) return;

      if (e.key === "Tab" && e.shiftKey) {
        e.preventDefault();
        const current = useSettingsStore.getState().mode;
        setMode(MODE_CYCLE[(MODE_CYCLE.indexOf(current) + 1) % MODE_CYCLE.length]);
        return;
      }

      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        handleSubmit();
      }

      // Escape to blur
      if (e.key === "Escape" && !showDropdown) {
        e.preventDefault();
        textareaRef.current?.blur();
      }
    },
    [handleSubmit, handleMentionKeyDown, showDropdown, setMode],
  );

  const handleChange = useCallback(
    (e: React.ChangeEvent<HTMLTextAreaElement>) => {
      const newValue = e.target.value;
      setValue(newValue);
      handleMentionDetection(newValue);

      const textarea = e.target;
      textarea.style.height = "auto";
      textarea.style.height = Math.min(textarea.scrollHeight, 160) + "px";
    },
    [handleMentionDetection],
  );

  const hasContent = value.trim().length > 0;

  return (
    <div className="flex flex-col gap-1.5 relative">
      {/* Context chips */}
      {context.length > 0 && (
        <div className="flex flex-wrap gap-1.5 px-0.5 animate-slide-up">
          {context.map((item) => (
            <ContextChip
              key={item.id}
              item={item}
              onRemove={() => removeContext(item.id)}
            />
          ))}
        </div>
      )}

      {/* Complex request in Agent mode: suggest planning first */}
      {suggestPlan && (
        <div className="flex items-center gap-1.5 px-2 py-1 rounded-md bg-accent-subtle/50 border border-accent/25
                        text-[11px] text-text-secondary animate-slide-up">
          <ListChecks size={12} className="text-accent shrink-0" />
          <span className="flex-1">Complex task? Plan it first: questions, an editable plan, then Build.</span>
          <button
            onClick={() => setMode("plan")}
            className="px-1.5 py-0.5 rounded font-medium text-accent hover:bg-accent-subtle shrink-0"
            title="Switch to Plan mode (Shift+Tab)"
          >
            Plan mode
          </button>
          <button
            onClick={() => setPlanHintDismissed(true)}
            className="p-0.5 rounded text-text-muted hover:text-text-primary shrink-0"
            aria-label="Dismiss"
          >
            <X size={11} />
          </button>
        </div>
      )}

      {/* Mention dropdown */}
      {showDropdown && (
        <MentionDropdown
          results={results}
          prioritizedResults={prioritizedResults}
          activeIndex={activeIndex}
          isSearching={isSearching}
          query={query}
          onSelect={selectResult}
        />
      )}

      {/* Input container */}
      <div
        className={`flex items-end gap-1.5 px-3 py-2.5 rounded-2xl border-2 transition-all duration-200
        bg-bg-secondary
        ${
          isFocused
            ? "border-accent/40 shadow-[0_0_0_2px_rgba(0,122,204,0.08)] bg-bg-tertiary"
            : "border-border hover:border-border-light"
        }
        ${disabled ? "opacity-50" : ""}
      `}
      >
        {/* Send / Stop button — left side during streaming */}
        {isStreaming && (
          <button
            onClick={onStop}
            className="shrink-0 w-7 h-7 rounded-lg flex items-center justify-center
                       transition-all bg-error/90 hover:bg-error text-white
                       active:scale-95"
            aria-label="Stop generation"
            title="Stop (Escape)"
          >
            <Square size={12} fill="currentColor" />
          </button>
        )}

        {/* @ mention button */}
        {!isStreaming && (
          <button
            onClick={openMentionPicker}
            className="shrink-0 p-1.5 rounded-lg text-text-muted hover:text-accent
                       hover:bg-accent-subtle transition-all duration-150"
            title="Mention file or symbol (@)"
            disabled={disabled}
          >
            <AtSign size={16} />
          </button>
        )}

        {/* Textarea */}
        <textarea
          ref={textareaRef}
          value={value}
          onChange={handleChange}
          onKeyDown={handleKeyDown}
          onFocus={() => setIsFocused(true)}
          onBlur={() => setIsFocused(false)}
          placeholder={placeholder}
          disabled={disabled}
          rows={1}
          className="flex-1 min-h-[24px] max-h-[160px] py-0.5 bg-transparent
                     resize-none text-[13px] leading-relaxed text-text-primary
                     placeholder:text-text-muted focus:outline-none"
        />

        {/* Send button — right side when not streaming */}
        {!isStreaming && (
          <button
            onClick={handleSubmit}
            disabled={disabled || !hasContent}
            className={`shrink-0 w-7 h-7 rounded-lg flex items-center justify-center
                        transition-all duration-150 active:scale-95
              ${
                hasContent && !disabled
                  ? "bg-accent text-white hover:bg-accent-hover shadow-sm"
                  : "text-text-muted bg-bg-tertiary"
              }
            `}
            aria-label="Send message"
            title="Send (Enter)"
          >
            <ArrowUp size={14} strokeWidth={2.5} />
          </button>
        )}
      </div>

      {/* Hint row */}
      <div className="flex items-center justify-between px-1 text-[10px] text-text-muted select-none">
        <span className="flex items-center gap-1">
          <CornerDownLeft size={10} />
          <span>Send</span>
          <span className="text-text-muted/40">·</span>
          <span>Shift+Enter newline</span>
          <span className="text-text-muted/40">·</span>
          <span>Shift+Tab mode ({mode})</span>
        </span>
        {context.length > 0 && (
          <span className="flex items-center gap-1 text-accent">
            <FilePlus size={10} />
            {context.length} context item{context.length !== 1 ? "s" : ""}
          </span>
        )}
      </div>
    </div>
  );
};
