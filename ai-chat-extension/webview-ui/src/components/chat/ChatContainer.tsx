import React, { useEffect, useRef, useCallback, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useShallow } from "zustand/react/shallow";
import {
  AlertCircle,
  ArrowDown,
  RefreshCw,
  CheckCheck,
  XCircle,
  Zap,
  Brain,
  StopCircle,
  Loader2,
  Sparkles,
  PauseCircle,
} from "lucide-react";
import { useChatStore } from "../../stores/chatStore";
import { useChangeStore } from "../../stores/changeStore";
import { acceptChange, rejectChange } from "../../stores/changeActions";
import { useConversationStore } from "../../stores/conversationStore";
import { useMentionStore } from "../../stores/mentionStore";
import { useSettingsStore } from "../../stores/settingsStore";
import { usePlanStore } from "../../stores/planStore";
import { ModeSelector } from "../common/ModeSelector";
import { PlanBar } from "./PlanBar";
import { MessageBubble } from "./MessageBubble";
import { MessageInput } from "./MessageInput";
import { ChatHeader } from "./ChatHeader";
import { EmptyState } from "../common/EmptyState";
import { HistorySidebar } from "../history/HistorySidebar";
import { SettingsPanel } from "../settings/SettingsPanel";
import { useAgentStream } from "../../hooks/useAgentStream";
import { useHealthCheck } from "../../hooks/useHealthCheck";
import { vscode } from "../../services/vscodeApi";

/** "Running `dotnet build`…", "Reading Calculator.cs…" for the footer */
function describeTool(tool: { tool: string; args: Record<string, unknown> }): string {
  const verbs: Record<string, string> = {
    read_file: "Reading",
    read_files: "Reading",
    write_file: "Writing",
    replace_lines: "Editing",
    edit_file: "Editing",
    find_files: "Finding",
    search_code: "Searching",
    semantic_search: "Searching",
    list_directory: "Listing",
    run_terminal: "Running",
  };
  const verb = verbs[tool.tool] ?? tool.tool.replace(/_/g, " ");
  const a = tool.args ?? {};
  const target = a.command
    ? `\`${String(a.command)}\``
    : a.path
      ? String(a.path).split(/[/\\]/).pop()
      : a.pattern
        ? String(a.pattern)
        : a.query
        ? `"${String(a.query).slice(0, 40)}"`
        : "";
  return `${verb} ${target}…`.replace(/ …$/, "…");
}

/** No stream event for this long (and nothing running or awaiting approval) = probably stuck */
const STALL_SECONDS = 30;

export const ChatContainer: React.FC = () => {
  const scrollContainerRef = useRef<HTMLDivElement>(null);
  const [showScrollBtn, setShowScrollBtn] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [autoScrollEnabled, setAutoScrollEnabled] = useState(true);

  const messages = useChatStore((state) => state.messages);
  const isStreaming = useChatStore((state) => state.isStreaming);
  const error = useChatStore((state) => state.error);
  const clearError = useChatStore((state) => state.setError);
  const clearChat = useChatStore((state) => state.clearChat);
  const activeTools = useChatStore((state) => state.activeTools);
  const backendStatus = useSettingsStore((state) => state.backendStatus);
  const staleBundles = useSettingsStore((state) => state.staleBundles);
  const mode = useSettingsStore((state) => state.mode);
  const lastEventAt = useChatStore((state) => state.lastEventAt);
  const limitReached = useChatStore((state) => state.limitReached);
  const backendDetail = useSettingsStore((state) => state.backendDetail);

  // useShallow: filter() returns a new array each call, which makes zustand v5
  // re-render forever (React error #185) unless the result is compared shallowly.
  const pendingChanges = useChangeStore(
    useShallow((state) => state.changes.filter((c) => c.status === "awaiting")),
  );

  const showHistory = useConversationStore((state) => state.showHistory);
  const toggleHistory = useConversationStore((state) => state.toggleHistory);
  const saveCurrentConversation = useConversationStore(
    (state) => state.saveCurrentConversation,
  );

  const { runTask, handleMessage, abort } = useAgentStream();

  useHealthCheck();

  const virtualizer = useVirtualizer({
    count: messages.length,
    getScrollElement: () => scrollContainerRef.current,
    estimateSize: () => 120,
    overscan: 5,
  });

  const scrollToBottom = useCallback(
    (immediate = false) => {
      const container = scrollContainerRef.current;
      if (!container) return;
      if (immediate) {
        container.scrollTop = container.scrollHeight;
      } else if (isStreaming) {
        container.scrollTop = container.scrollHeight;
      } else {
        container.scrollTo({ top: container.scrollHeight, behavior: "smooth" });
      }
    },
    [isStreaming],
  );

  // Track manual scroll
  useEffect(() => {
    const container = scrollContainerRef.current;
    if (!container) return;
    const handleScroll = () => {
      const dist =
        container.scrollHeight - container.scrollTop - container.clientHeight;
      const atBottom = dist < 50;
      setAutoScrollEnabled(atBottom);
      setShowScrollBtn(dist > 100);
    };
    container.addEventListener("scroll", handleScroll, { passive: true });
    return () => container.removeEventListener("scroll", handleScroll);
  }, []);

  // Auto-scroll during streaming
  useEffect(() => {
    if (autoScrollEnabled) {
      requestAnimationFrame(() => scrollToBottom(false));
    }
  }, [messages, autoScrollEnabled, scrollToBottom]);

  // Listen for messages from extension
  useEffect(() => {
    const listener = (event: MessageEvent) => {
      handleMessage(event.data);
    };
    window.addEventListener("message", listener);
    return () => window.removeEventListener("message", listener);
  }, [handleMessage]);

  // Keyboard shortcuts
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement;
      if (t.tagName === "TEXTAREA" || t.tagName === "INPUT") return;

      if (e.key === "Escape" && isStreaming) {
        e.preventDefault();
        handleStop();
      }
      if (e.key === "n" && e.ctrlKey && e.shiftKey) {
        e.preventDefault();
        handleNewChat();
      }
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [isStreaming]);

  const handleSend = (task: string, context?: any[]) => {
    runTask(task, "", context);
  };

  const handleStop = () => {
    abort();
    vscode.postMessage({ type: "cancelTask" });
  };

  const handleNewChat = () => {
    if (messages.length > 0) {
      saveCurrentConversation(messages);
    }
    clearChat();
    usePlanStore.getState().clearPlan();
  };

  // The plan card lives in a virtualized list and may be unmounted: scroll by message index
  const handleViewPlan = () => {
    for (let i = messages.length - 1; i >= 0; i--) {
      if (messages[i].segments.some((s) => s.type === "plan")) {
        setAutoScrollEnabled(false);
        virtualizer.scrollToIndex(i, { align: "start" });
        return;
      }
    }
  };

  // The plan card's "Execute" buttons send a message through here
  useEffect(() => {
    const onRun = (e: Event) => {
      const task = (e as CustomEvent<string>).detail;
      if (task && !useChatStore.getState().isStreaming) runTask(task, "");
    };
    window.addEventListener("ai-agent:run", onRun);
    return () => window.removeEventListener("ai-agent:run", onRun);
  }, [runTask]);

  const handleRetry = () => {
    clearError(null);
    const lastUserMsg = [...messages].reverse().find((m) => m.role === "user");
    if (lastUserMsg) {
      runTask(lastUserMsg.content, "");
    }
  };

  const handleAcceptAll = () => {
    pendingChanges.forEach((change) => acceptChange(change));
  };

  const handleRejectAll = () => {
    pendingChanges.forEach((change) => rejectChange(change));
  };

  // Only tools actually executing; a tool paused for approval is shown by its card
  const runningTools = Array.from(activeTools.values()).filter((t) => t.status === "running");
  const runningToolCount = runningTools.length;
  const runningLabel = runningTools.length === 1 ? describeTool(runningTools[0]) : null;

  // Alt+A / Alt+R accept or reject when exactly one card is waiting
  useEffect(() => {
    if (pendingChanges.length !== 1) return;
    const onKey = (e: KeyboardEvent) => {
      if (!e.altKey || e.ctrlKey || e.metaKey) return;
      const key = e.key.toLowerCase();
      if (key === "a") {
        e.preventDefault();
        acceptChange(pendingChanges[0]);
      } else if (key === "r") {
        e.preventDefault();
        rejectChange(pendingChanges[0]);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [pendingChanges]);

  // Re-evaluate the stall condition every few seconds while streaming
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!isStreaming) return;
    const timer = setInterval(() => setNow(Date.now()), 5000);
    return () => clearInterval(timer);
  }, [isStreaming]);
  const isStalled =
    isStreaming &&
    runningToolCount === 0 &&
    pendingChanges.length === 0 &&
    lastEventAt > 0 &&
    now - lastEventAt > STALL_SECONDS * 1000;

  return (
    <div className="flex flex-col h-full relative bg-bg-primary">
      {/* Header */}
      <ChatHeader
        onNewChat={handleNewChat}
        onToggleHistory={toggleHistory}
        onOpenSettings={() => setShowSettings(true)}
        showHistory={showHistory}
      />

      {/* Overlays */}
      {showHistory && <HistorySidebar />}
      {showSettings && <SettingsPanel onClose={() => setShowSettings(false)} />}

      {/* Messages area */}
      <div ref={scrollContainerRef} className="flex-1 overflow-y-auto">
        <div className="max-w-[740px] mx-auto px-4 py-2">
          {messages.length === 0 ? (
            <EmptyState onSelect={handleSend} />
          ) : (
            <div
              style={{
                height: `${virtualizer.getTotalSize()}px`,
                width: "100%",
                position: "relative",
              }}
            >
              {virtualizer.getVirtualItems().map((virtualRow) => (
                <div
                  key={messages[virtualRow.index].id}
                  data-index={virtualRow.index}
                  ref={virtualizer.measureElement}
                  style={{
                    position: "absolute",
                    top: 0,
                    left: 0,
                    width: "100%",
                    transform: `translateY(${virtualRow.start}px)`,
                  }}
                >
                  <MessageBubble
                    message={messages[virtualRow.index]}
                    isLast={virtualRow.index === messages.length - 1}
                    onRegenerate={handleRetry}
                  />
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Floating scroll-to-bottom */}
      {showScrollBtn && !autoScrollEnabled && (
        <button
          onClick={() => {
            scrollToBottom(true);
            setAutoScrollEnabled(true);
          }}
          className="absolute bottom-24 left-1/2 -translate-x-1/2
                     bg-bg-tertiary border border-border rounded-full
                     p-1.5 shadow-lg hover:bg-bg-active hover:border-border-light
                     transition-all animate-fade-in z-10 group"
        >
          <ArrowDown
            size={13}
            className="text-text-muted group-hover:text-text-primary"
          />
        </button>
      )}

      {/* ── Footer notices: never inside the (virtualized) message list ── */}

      {/* Extension rebuilt while this window runs old code */}
      {staleBundles.length > 0 && (
        <div
          className="mx-4 mb-1 px-3 py-2 rounded-lg bg-error-subtle border border-error/20
                        flex items-center gap-2 text-[12px] text-error animate-slide-up"
        >
          <AlertCircle size={14} className="shrink-0" />
          <span className="flex-1 leading-snug">
            The extension was rebuilt. Press Ctrl+Shift+F5 in the extension window (or run
            "Developer: Reload Window" here) to load the new version.
          </span>
        </div>
      )}

      {/* The agent is paused on a change/command the user must approve */}
      {isStreaming && pendingChanges.length > 0 && (
        <div
          className="mx-4 mb-1 flex items-center gap-2 px-3 py-1.5 rounded-md
                        bg-warning/10 border border-warning/30 animate-slide-up"
        >
          <PauseCircle size={12} className="text-warning" />
          <span className="text-[11px] text-warning font-medium flex-1">
            Waiting for your approval above
          </span>
          {pendingChanges.length === 1 && (
            <span className="text-[10px] text-warning/70">Alt+A accept · Alt+R reject</span>
          )}
        </div>
      )}

      {/* Active tool indicator (tools actually running, not waiting) */}
      {runningToolCount > 0 && pendingChanges.length === 0 && (
        <div
          className="mx-4 mb-1 flex items-center gap-2 px-3 py-1.5 rounded-md
                        bg-accent-subtle border border-accent/10 animate-slide-up"
        >
          <Loader2 size={12} className="text-accent animate-spin" />
          <span className="text-[11px] text-accent font-medium truncate">
            {runningLabel ?? `${runningToolCount} tools running`}
          </span>
        </div>
      )}

      {/* Step budget reached: one click continues with the same history (and plan) */}
      {limitReached !== null && !isStreaming && (
        <div
          className="mx-4 mb-1 flex items-center gap-2 px-3 py-1.5 rounded-md bg-bg-secondary
                        border border-border animate-slide-up text-[11px]"
        >
          <PauseCircle size={12} className="text-text-muted shrink-0" />
          <span className="text-text-secondary flex-1">
            Paused after {limitReached} steps. The work so far is kept.
          </span>
          <button
            onClick={() => runTask("continue", "")}
            className="flex items-center gap-1 px-2.5 py-0.5 rounded font-medium text-white bg-accent
                       hover:bg-accent/80 transition-colors"
          >
            Continue
          </button>
        </div>
      )}

      {/* Plan progress: run the next step without scrolling back to the plan card */}
      <PlanBar onView={handleViewPlan} />

      {/* Real stall: nothing received for a while, nothing running, nothing waiting on the user */}
      {isStalled && (
        <div
          className="mx-4 mb-1 px-3 py-2 rounded-lg bg-warning/10 border border-warning/20
                        flex items-center gap-2 text-[12px] text-warning animate-slide-up"
        >
          <Brain size={13} className="shrink-0" />
          <span className="flex-1 leading-snug">
            No response from the agent for {STALL_SECONDS}s. It may be stuck.
          </span>
          <button
            onClick={handleStop}
            className="shrink-0 px-2.5 py-1 rounded-md bg-warning/15 hover:bg-warning/25
                       text-[11px] font-medium transition-colors"
          >
            Stop
          </button>
        </div>
      )}

      {/* Stale backend banner: requests would silently lose history/context */}
      {backendStatus === "outdated" && (
        <div
          className="mx-4 mb-1 px-3 py-2 rounded-lg bg-error-subtle border border-error/20
                        flex items-center gap-2 text-[12px] text-error animate-slide-up"
        >
          <AlertCircle size={14} className="shrink-0" />
          <span className="flex-1 leading-snug">
            {backendDetail ?? "The backend is outdated. Restart it."}
          </span>
        </div>
      )}

      {/* Error banner */}
      {error && (
        <div
          className="mx-4 mb-1 px-3 py-2 rounded-lg bg-error-subtle border border-error/20
                        flex items-center gap-2 text-[12px] text-error animate-slide-up"
        >
          <AlertCircle size={14} className="shrink-0" />
          <span className="flex-1 leading-snug">{error}</span>
          <button
            onClick={handleRetry}
            className="shrink-0 flex items-center gap-1 px-2.5 py-1 rounded-md
                       bg-error/15 hover:bg-error/25 text-error transition-colors
                       text-[11px] font-medium"
          >
            <RefreshCw size={11} />
            Retry
          </button>
          <button
            onClick={() => clearError(null)}
            className="shrink-0 text-error/50 hover:text-error transition-colors px-1"
          >
            ×
          </button>
        </div>
      )}

      {/* Batch change actions */}
      {pendingChanges.length > 1 && (
        <div
          className="mx-4 mb-1 px-3 py-2 rounded-lg bg-bg-secondary border border-border
                        flex items-center justify-between text-[12px] animate-slide-up"
        >
          <span className="text-text-secondary font-medium">
            {pendingChanges.length} changes waiting for your approval
          </span>
          <div className="flex items-center gap-2">
            <button
              onClick={handleAcceptAll}
              className="flex items-center gap-1.5 px-3 py-1 rounded-md text-[11px] font-medium
                         text-white bg-success hover:bg-success/80 transition-colors"
            >
              <CheckCheck size={12} />
              Accept All
            </button>
            <button
              onClick={handleRejectAll}
              className="flex items-center gap-1.5 px-3 py-1 rounded-md text-[11px] font-medium
                         text-text-secondary border border-border hover:bg-error-subtle
                         hover:text-error hover:border-error/30 transition-colors"
            >
              <XCircle size={12} />
              Reject All
            </button>
          </div>
        </div>
      )}

      {/* Input area */}
      <div className="px-3 pb-3 pt-1.5 border-t border-border/50">
        <div className="flex items-center gap-2 mb-1.5">
          <ModeSelector />
          {mode === "auto" && (
            <span className="text-[10px] text-warning/80">edits and build/test commands apply without asking</span>
          )}
          {mode === "ask" && <span className="text-[10px] text-text-muted">read-only</span>}
          {mode === "plan" && <span className="text-[10px] text-text-muted">read-only, ends with a plan</span>}
        </div>
        <MessageInput
          onSend={handleSend}
          onStop={handleStop}
          disabled={isStreaming}
          isStreaming={isStreaming}
        />
      </div>
    </div>
  );
};
