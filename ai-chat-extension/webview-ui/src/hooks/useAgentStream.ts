import { useCallback, useRef, useEffect } from "react";
import {
  useChatStore,
  type ToolExecution,
  type Message,
} from "../stores/chatStore";
import { useChangeStore, type FileChange } from "../stores/changeStore";
import { useSettingsStore } from "../stores/settingsStore";
import { useMentionStore } from "../stores/mentionStore";
import { usePlanStore, type Plan } from "../stores/planStore";
import { vscode } from "../services/vscodeApi";

interface StreamHandlers {
  onComplete?: () => void;
  onError?: (error: string) => void;
}

interface SessionState {
  messages: Message[];
  activeTools: ToolExecution[];
  toolHistory: ToolExecution[];
  changes: FileChange[];
  plan?: Plan | null;
  timestamp: number;
}

// Auto-save subscription - call this in your main App component
export function useAutoSave() {
  const saveTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const debouncedSave = () => {
      if (saveTimeoutRef.current) {
        clearTimeout(saveTimeoutRef.current);
      }

      saveTimeoutRef.current = setTimeout(() => {
        const { messages, activeTools, toolHistory } = useChatStore.getState();
        const { changes } = useChangeStore.getState();

        // Only save if there are messages
        if (messages.length === 0) return;

        const session: SessionState = {
          messages,
          activeTools: Array.from(activeTools.values()),
          toolHistory,
          changes,
          plan: usePlanStore.getState().plan,
          timestamp: Date.now(),
        };

        vscode.postMessage({ type: "saveCurrentSession", session });
      }, 300); // 300ms debounce
    };

    // Subscribe to store changes
    const unsubChat = useChatStore.subscribe((state) => {
      if (state.messages.length > 0) {
        debouncedSave();
      }
    });
    // Plan progress (step ticks) is part of the session too
    const unsubPlan = usePlanStore.subscribe(() => debouncedSave());

    const unsubChange = useChangeStore.subscribe((state) => {
      if (state.changes.length > 0) {
        debouncedSave();
      }
    });

    return () => {
      unsubChat();
      unsubPlan();
      unsubChange();
      if (saveTimeoutRef.current) {
        clearTimeout(saveTimeoutRef.current);
      }
    };
  }, []);
}

// ── Stateful suppression ────────────────────────────────────────────────
const TOOL_MARKERS = [
  "TOOL_REQUEST",
  "END_TOOL_REQUEST",
  "_REQUEST TOOL",
  "_REQUEST\nTOOL",
  "PARAM:",
  "TOOL_EVENT",
  "tool_result",
  "tool_start",
  "END_TOOL",
  "END_",
];

function tokenContainsToolMarker(content: string): boolean {
  const t = content.trim();
  if (!t) return false;
  for (const marker of TOOL_MARKERS) {
    if (t.includes(marker)) return true;
  }
  if ((t.includes('"tool":') || t.includes("'tool':")) && t.length < 300)
    return true;
  if (/^\s*\[?\s*TOOL\b/i.test(t) || /^\s*\[?\s*tool_/i.test(t)) return true;
  // Catch "TOOL_REQUEST TOOL:" with any whitespace
  if (/TOOL_REQUEST\s+TOOL/i.test(t)) return true;
  return false;
}

// ── Post-processing: clean leaked text from every text segment ──────────
function cleanTextSegment(text: string, hasChanges: boolean): string {
  let c = text;

  // Strip TOOL_REQUEST blocks (with all variations of spacing)
  c = c.replace(/TOOL_REQUEST[\s\S]*?END_TOOL_REQUEST/g, "");
  c = c.replace(/_REQUEST\s+TOOL[\s\S]*?END_TOOL_REQUEST/g, "");
  c = c.replace(/_REQUEST\s+TOOL[\s\S]*?END_/g, "");
  c = c.replace(/TOOL_REQUEST\s+TOOL:[\s\S]*?END_/g, "");

  // Strip orphaned lines containing tool markers
  c = c.replace(/^.*TOOL_REQUEST.*$/gm, "");
  c = c.replace(/^.*END_TOOL_REQUEST.*$/gm, "");
  c = c.replace(/^.*_REQUEST\s+TOOL.*$/gm, "");
  c = c.replace(/^.*PARAM:.*$/gm, "");
  c = c.replace(/^.*END_TOOL.*$/gm, "");
  c = c.replace(/^.*TOOL_EVENT.*$/gm, "");

  // Strip inline fragments glued to text (e.g. "END_TOOL_REQUESTIt seems")
  c = c.replace(/END_TOOL_REQUEST/g, "");
  c = c.replace(/TOOL_REQUEST/g, "");
  c = c.replace(/_REQUEST TOOL/g, "");

  // Strip large code blocks (>30 lines) when file changes exist
  if (hasChanges) {
    c = c.replace(/```[\w]*\n([\s\S]*?)```/g, (match, inner) => {
      const lineCount = inner.split("\n").length;
      if (lineCount > 30) return "";
      return match;
    });
  }

  // Collapse excessive blank lines
  c = c.replace(/\n{4,}/g, "\n\n");
  return c.trim();
}

// Helper: get current assistant message id
function getCurrentAssistantId(): string | undefined {
  return useChatStore
    .getState()
    .messages.filter((m) => m.role === "assistant")
    .pop()?.id;
}

/** Max previous messages sent with each request (the backend caps this too). */
const MAX_HISTORY_MESSAGES = 12;

/** The finished text of recent turns; streaming/empty messages (e.g. failed runs) are skipped. */
function buildHistory(): { role: "user" | "assistant"; content: string }[] {
  return useChatStore
    .getState()
    .messages.filter((m) => !m.isStreaming && m.content.trim().length > 0)
    .slice(-MAX_HISTORY_MESSAGES)
    .map((m) => ({ role: m.role, content: m.content }));
}

export function useAgentStream() {
  const abortRef = useRef<boolean>(false);
  const suppressingRef = useRef<boolean>(false);

  const {
    startAssistantMessage,
    appendToMessage,
    addToolSegment,
    addChangeSegment,
    finalizeMessage,
    addToolStart,
    addToolResult,
    setMessageMetrics,
    setError,
  } = useChatStore();


  const runTask = useCallback(
    async (
      task: string,
      workspace: string,
      context?: any[],
      handlers?: StreamHandlers,
    ) => {
      abortRef.current = false;
      suppressingRef.current = false;

      // Auto-inject context from open/recent files if no explicit context provided
      let finalContext = context;
      if (!context || context.length === 0) {
        const mentionState = useMentionStore.getState();
        const { openFiles, recentFiles } = mentionState;

        // Build auto-context: active file + up to 3 recent files (deduplicated)
        const autoContext: any[] = [];

        // Add open files first (these are currently visible in editor)
        openFiles.slice(0, 1).forEach((filePath) => {
          autoContext.push({
            type: "file",
            name: filePath.split(/[/\\]/).pop() || filePath,
            filePath,
            autoContext: true,
          });
        });

        // Add recent files not already included (limit to 3 total including open files)
        const remainingSlots = 3 - autoContext.length;
        recentFiles
          .filter((f) => !openFiles.includes(f))
          .slice(0, remainingSlots)
          .forEach((filePath) => {
            autoContext.push({
              type: "file",
              name: filePath.split(/[/\\]/).pop() || filePath,
              filePath,
              autoContext: true,
            });
          });

        if (autoContext.length > 0) {
          finalContext = autoContext;
        }
      }

      // Store context for this message (for display in MessageBubble)
      const messageContext = finalContext;

      // Snapshot the conversation BEFORE adding this message, so follow-ups have a referent
      const history = buildHistory();

      useChatStore.getState().addUserMessage(task, messageContext);
      useChatStore.getState().touchStream(); // stall timer starts with the request
      useChatStore.getState().setLimitReached(null); // a new request clears the Continue bar
      const messageId = startAssistantMessage();

      try {
        const model = useSettingsStore.getState().selectedModel;
        const mode = useSettingsStore.getState().mode;
        const activePlan = usePlanStore.getState().plan;
        vscode.postMessage({
          // The plan FILE is the source of truth (the user may have edited it); the JSON is the fallback
          ...(activePlan?.path ? { planPath: activePlan.path } : {}),
          type: "runTask",
          task,
          workspace,
          ...(finalContext && finalContext.length > 0
            ? { context: finalContext }
            : {}),
          ...(history.length > 0 ? { history } : {}),
          ...(model ? { model } : {}),
          mode,
          ...(activePlan ? { activePlan } : {}),
        });
      } catch (error) {
        const errorMsg =
          error instanceof Error ? error.message : "Failed to send task";
        setError(errorMsg);
        handlers?.onError?.(errorMsg);
      }
    },
    [startAssistantMessage, setError],
  );

  const handleMessage = useCallback(
    (message: any) => {
      // After Stop only file changes still count: they are on disk and belong in the review
      if (abortRef.current && message.type !== "changeEvent") return;

      // Any stream activity resets the stall timer
      if (
        ["token", "toolStart", "toolResult", "approvalEvent", "changeEvent"].includes(
          message.type,
        )
      ) {
        useChatStore.getState().touchStream();
      }

      switch (message.type) {
        case "token": {
          if (suppressingRef.current) break;

          const content = message.content || "";
          if (tokenContainsToolMarker(content)) {
            suppressingRef.current = true;
            break;
          }

          const currentId = getCurrentAssistantId();
          if (currentId) {
            appendToMessage(currentId, content);
          }
          break;
        }

        case "toolStart": {
          suppressingRef.current = true;
          // Use backend-provided toolCallId if available, otherwise generate one
          const toolId =
            message.toolCallId ||
            `tool-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
          addToolStart(toolId, message.tool, message.args);

          // Push a tool segment into the current message timeline
          const msgId = getCurrentAssistantId();
          if (msgId) {
            addToolSegment(msgId, toolId);
          }
          break;
        }

        case "toolResult": {
          suppressingRef.current = false;
          // A command card for this call becomes Succeeded/Failed with its output
          if (message.toolCallId) {
            useChangeStore
              .getState()
              .finishCommand(message.toolCallId, message.status !== "error", message.output ?? message.result ?? "");
          }
          // Match by explicit toolCallId from the backend, not insertion order
          const targetId = message.toolCallId;
          if (targetId) {
            addToolResult(targetId, message.result, message.status);
          } else {
            // Fallback: match the most recently started tool (legacy backends)
            const lastTool = Array.from(
              useChatStore.getState().activeTools.values(),
            ).pop();
            if (lastTool) {
              addToolResult(lastTool.id, message.result, message.status);
            }
          }
          break;
        }

        case "approvalEvent": {
          const approval = message.approval;
          const store = useChangeStore.getState();
          if (approval.decision) {
            // The paused tool card resumes (tool_result will complete it)
            if (approval.toolCallId) {
              useChatStore.getState().setToolStatus(approval.toolCallId, "running");
            } else {
              for (const t of useChatStore.getState().activeTools.values()) {
                if (t.status === "awaiting") useChatStore.getState().setToolStatus(t.id, "running");
              }
            }
            // Decision recorded by the backend. "approved" stays "accepted" until the
            // change event arrives (commands have none, so they are done now).
            const card = store.changes.find((c) => c.approvalId === approval.approvalId);
            if (card) {
              if (approval.decision === "approved") {
                // Commands are "running" until their tool_result says succeeded/failed
                store.setStatus(card.id, card.kind === "command" ? "running" : "accepted");
              } else if (approval.decision === "rejected") {
                store.setStatus(card.id, "rejected");
              } else {
                store.setStatus(card.id, "expired");
              }
            }
            break;
          }

          // The agent proposed a change/command and is waiting for Accept/Reject
          const card = store.addApproval(approval);
          // Its tool card shows "Waiting for your approval" instead of a spinner
          // (auto-approved commands are already running: nothing to wait for)
          const toolId =
            approval.toolCallId ||
            Array.from(useChatStore.getState().activeTools.values()).pop()?.id;
          if (toolId && !approval.autoApproved) useChatStore.getState().setToolStatus(toolId, "awaiting");
          const msgId = getCurrentAssistantId();
          if (msgId) {
            addChangeSegment(msgId, card.id);
          }
          break;
        }

        case "questionsEvent": {
          // Plan mode asks before planning: a card with options; the answers are the next message
          const msgId = getCurrentAssistantId();
          if (msgId && Array.isArray(message.questions)) {
            useChatStore.getState().addQuestionsSegment(msgId, message.questions);
          }
          break;
        }

        case "planUpdated": {
          // The plan file changed on disk (edited by the user, or ticked by the agent)
          if (message.plan) usePlanStore.getState().applyFileUpdate(message.plan);
          break;
        }

        case "limitEvent": {
          // The step budget ran out: the footer offers Continue
          useChatStore.getState().setLimitReached(message.limit?.steps ?? 0);
          break;
        }

        case "planEvent": {
          const ev = message.plan;
          if (ev.type === "plan" && ev.plan) {
            // A new plan replaces the old one; its checklist card appears in this message
            usePlanStore.getState().setPlan(ev.plan);
            const msgId = getCurrentAssistantId();
            if (msgId) useChatStore.getState().addPlanSegment(msgId);
          } else if (ev.type === "update") {
            usePlanStore.getState().updateStep(ev.step, ev.status);
          }
          break;
        }

        case "extensionStatus": {
          useSettingsStore.getState().setStaleBundles(message.staleBundles ?? []);
          break;
        }

        case "revertFailed": {
          // The file was not touched; show the change as still applied
          const store = useChangeStore.getState();
          const card = store.changes.find((c) => c.changeId === message.changeId);
          if (card) store.setStatus(card.id, "applied");
          break;
        }

        case "changeEvent": {
          const c = message.change;
          const applied = {
            filePath: c.filePath,
            before: c.before ?? undefined,
            after: c.after ?? undefined,
            toolUsed: c.toolUsed,
            isNewFile: c.isNewFile,
            isDeletion: c.isDeletion,
            changeId: c.changeId,
            sessionId: c.sessionId,
            patch: c.patch,
            summary: c.summary,
            patchSize: c.patchSize,
          };

          // Normal path: the approved card becomes the applied change (same card, now revertible)
          if (c.approvalId && useChangeStore.getState().completeApproval(c.approvalId, applied)) {
            break;
          }

          const card = useChangeStore.getState().addAppliedChange(applied);
          const msgId = getCurrentAssistantId();
          if (msgId) {
            addChangeSegment(msgId, card.id);
          }
          break;
        }

        case "metrics": {
          const msgId = getCurrentAssistantId();
          if (msgId && message.metrics) {
            setMessageMetrics(msgId, message.metrics);
          }
          break;
        }

        case "streamDone": {
          suppressingRef.current = false;

          const currentMsg = useChatStore
            .getState()
            .messages.filter((m) => m.role === "assistant")
            .pop();

          if (currentMsg) {
            // Post-processing: clean leaked tool text from all text segments
            const hasChanges = useChangeStore.getState().changes.length > 0;
            const cleanedSegments = currentMsg.segments
              .map((seg) => {
                if (seg.type === "text") {
                  const cleaned = cleanTextSegment(seg.content, hasChanges);
                  return { ...seg, content: cleaned };
                }
                return seg;
              })
              .filter((seg) => !(seg.type === "text" && !seg.content)); // remove empty text segments

            // Rebuild content from cleaned text segments
            const cleanedContent = cleanedSegments
              .filter((s) => s.type === "text")
              .map((s) => (s as { type: "text"; content: string }).content)
              .join("\n\n");

            useChatStore.setState((prev) => ({
              messages: prev.messages.map((m) =>
                m.id === currentMsg.id
                  ? { ...m, content: cleanedContent, segments: cleanedSegments }
                  : m,
              ),
            }));

            finalizeMessage(currentMsg.id);
          }
          break;
        }

        case "error": {
          suppressingRef.current = false;
          // A failed request leaves no empty "ASSISTANT" bubble behind; the error banner explains it
          const failedId = getCurrentAssistantId();
          if (failedId) useChatStore.getState().removeIfEmpty(failedId);
          setError(message.message);
          break;
        }

        case "clearChat": {
          suppressingRef.current = false;
          abortRef.current = true;
          useChatStore.getState().clearChat();
          useChangeStore.getState().clearChanges();
          usePlanStore.getState().clearPlan();
          break;
        }

        case "sendSelection": {
          // Auto-send a task with the selected code as context
          // The code travels as a "selection" context item; the task stays a short, readable request
          const { filePath, code, language } = message;
          const task = `Explain the selected ${language} code in ${filePath}.`;
          const history = buildHistory();
          useChatStore.getState().addUserMessage(task);
          const msgId = startAssistantMessage();

          vscode.postMessage({
            type: "runTask",
            task,
            workspace: "",
            context: [
              {
                type: "selection",
                name: filePath.split(/[/\\]/).pop() || filePath,
                filePath,
                code,
              },
            ],
            ...(history.length > 0 ? { history } : {}),
          });
          break;
        }

        case "healthStatus": {
          useSettingsStore.getState().setBackendStatus(message.status, message.detail ?? null);
          useSettingsStore.getState().setLastHealthCheck(Date.now());
          break;
        }

        case "modelsAvailable": {
          useSettingsStore.getState().setAvailableModels(message.models);
          // Auto-select first model if none selected
          if (
            !useSettingsStore.getState().selectedModel &&
            message.models.length > 0
          ) {
            useSettingsStore.getState().setSelectedModel(message.models[0].id);
          }
          break;
        }
      }
    },
    [
      appendToMessage,
      addToolSegment,
      addChangeSegment,
      finalizeMessage,
      addToolStart,
      addToolResult,
      setMessageMetrics,
      setError,
      startAssistantMessage,
    ],
  );

  /**
   * Stop: end the run here right away. Late messages from the stopped run are ignored (abortRef) until the
   * next run starts; the extension aborts the request, which stops the backend.
   */
  const abort = useCallback(() => {
    abortRef.current = true;
    suppressingRef.current = false;
    useChatStore.getState().stopRun();
    useChangeStore.getState().expireOpen();
  }, []);

  return { runTask, handleMessage, abort };
}
