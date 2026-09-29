import React, { useState } from "react";
import {
  User,
  Sparkles,
  FileCode,
  ChevronDown,
  ChevronUp,
  Zap,
  Bot,
  Copy,
  Check,
  RefreshCw,
} from "lucide-react";
import type { Message } from "../../stores/chatStore";
import { useChatStore } from "../../stores/chatStore";
import { useChangeStore } from "../../stores/changeStore";
import { MarkdownRenderer } from "./MarkdownRenderer";
import { ToolExecutionCard } from "../tools/ToolExecutionCard";
import { FileChangeCard } from "../changes/FileChangeCard";
import { PlanCard } from "./PlanCard";
import { QuestionsCard } from "./QuestionsCard";
import { TokenCostBadge } from "./TokenCostBadge";
import {
  acceptChange,
  rejectChange,
  revertAppliedChange,
  keepChanges,
} from "../../stores/changeActions";
import { vscode } from "../../services/vscodeApi";

interface MessageBubbleProps {
  message: Message;
  isLast?: boolean;
  onRegenerate?: () => void;
}

export const MessageBubble: React.FC<MessageBubbleProps> = ({
  message,
  isLast,
  onRegenerate,
}) => {
  const isUser = message.role === "user";
  const [showContext, setShowContext] = useState(false);
  const [copied, setCopied] = useState(false);
  const toolHistory = useChatStore((state) => state.toolHistory);
  const activeTools = useChatStore((state) => state.activeTools);
  const changes = useChangeStore((state) => state.changes);

  const findTool = (toolId: string) => {
    return activeTools.get(toolId) || toolHistory.find((t) => t.id === toolId);
  };

  const findChange = (changeId: string) => {
    return changes.find((c) => c.id === changeId);
  };

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(message.content);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      const textarea = document.createElement("textarea");
      textarea.value = message.content;
      document.body.appendChild(textarea);
      textarea.select();
      document.execCommand("copy");
      document.body.removeChild(textarea);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }
  };

  return (
    <div className={`animate-fade-in group ${isUser ? "mb-3" : "mb-4"}`}>
      {/* Role row */}
      <div className={`flex items-center gap-2.5 ${isUser ? "mb-1" : "mb-2"}`}>
        {/* Avatar */}
        <div
          className={`w-6 h-6 rounded-lg flex items-center justify-center shrink-0
          ${
            isUser
              ? "bg-accent/15 border border-accent/20"
              : "bg-bg-tertiary border border-border"
          }`}
        >
          {isUser ? (
            <User size={12} className="text-accent" />
          ) : (
            <Bot size={12} className="text-text-secondary" />
          )}
        </div>

        {/* Label */}
        <span className="text-[11px] font-semibold text-text-secondary uppercase tracking-wider select-none">
          {isUser ? "You" : "Assistant"}
        </span>

        {/* Timestamp */}
        <span className="text-[10px] text-text-muted select-none">
          {formatTime(message.timestamp)}
        </span>

        {/* Action buttons on hover */}
        {!isUser && !message.isStreaming && message.content && (
          <div className="flex items-center gap-0.5 ml-auto opacity-0 group-hover:opacity-100 transition-opacity duration-200">
            <button
              onClick={handleCopy}
              className="p-1 rounded-md text-text-muted hover:text-text-primary
                         hover:bg-bg-tertiary transition-colors"
              title={copied ? "Copied!" : "Copy message"}
            >
              {copied ? (
                <Check size={12} className="text-success" />
              ) : (
                <Copy size={12} />
              )}
            </button>
            {isLast && onRegenerate && (
              <button
                onClick={onRegenerate}
                className="p-1 rounded-md text-text-muted hover:text-text-primary
                           hover:bg-bg-tertiary transition-colors"
                title="Regenerate response"
              >
                <RefreshCw size={12} />
              </button>
            )}
          </div>
        )}
      </div>

      {/* Content */}
      <div className="ml-[34px]">
        {isUser ? (
          /* User message — subtle card */
          <div className="inline-block max-w-[90%]">
            <div
              className="px-3 py-2 rounded-2xl rounded-tl-sm bg-bg-secondary
                            border border-border/60 text-[13px] leading-relaxed
                            text-text-primary shadow-sm"
            >
              {message.content}
            </div>
          </div>
        ) : (
          /* Assistant message — segments */
          <div className="flex flex-col gap-1.5">
            {message.segments.length === 0 && message.isStreaming ? (
              <SkeletonLoader />
            ) : (
              message.segments.map((seg, idx) => {
                if (seg.type === "text") {
                  if (!seg.content) return null;
                  return (
                    <div
                      key={idx}
                      className="text-[13px] leading-relaxed text-text-primary
                                                break-words overflow-hidden"
                    >
                      <MarkdownRenderer content={seg.content} />
                    </div>
                  );
                }

                if (seg.type === "tool") {
                  // A change/command card for this call already shows its status: don't show it twice
                  if (changes.some((c) => c.toolCallId === seg.toolId)) return null;
                  const tool = findTool(seg.toolId);
                  if (!tool) return null;
                  return <ToolExecutionCard key={seg.toolId} tool={tool} />;
                }

                if (seg.type === "plan") {
                  return <PlanCard key={`plan-${idx}`} />;
                }

                if (seg.type === "questions") {
                  return (
                    <QuestionsCard
                      key={`questions-${idx}`}
                      messageId={message.id}
                      questions={seg.questions}
                      answers={seg.answers}
                    />
                  );
                }

                if (seg.type === "change") {
                  const change = findChange(seg.changeId);
                  if (!change) return null;
                  return (
                    <FileChangeCard
                      key={seg.changeId}
                      change={change}
                      onAccept={() => acceptChange(change)}
                      onReject={() => rejectChange(change)}
                      onRevert={() => revertAppliedChange(change)}
                      onKeep={() => keepChanges([change])}
                    />
                  );
                }

                return null;
              })
            )}

            {/* Tokens / time / cost of this answer */}
            {!isUser && !message.isStreaming && message.metrics && (
              <TokenCostBadge metrics={message.metrics} />
            )}

            {/* Streaming cursor */}
            {message.isStreaming &&
              message.segments.length > 0 &&
              message.segments[message.segments.length - 1].type === "text" && (
              <span
                className="inline-block w-[2px] h-[15px] bg-accent ml-0.5 align-middle
                               animate-pulse-soft rounded-full"
              />
            )}

            {/* Streaming indicator when waiting for first content */}
            {message.isStreaming && message.segments.length === 0 && (
              <div className="flex items-center gap-2 text-[12px] text-text-muted animate-fade-in py-1">
                <div className="flex gap-1">
                  <span
                    className="w-1.5 h-1.5 rounded-full bg-accent/60 animate-bounce"
                    style={{ animationDelay: "0ms" }}
                  />
                  <span
                    className="w-1.5 h-1.5 rounded-full bg-accent/60 animate-bounce"
                    style={{ animationDelay: "150ms" }}
                  />
                  <span
                    className="w-1.5 h-1.5 rounded-full bg-accent/60 animate-bounce"
                    style={{ animationDelay: "300ms" }}
                  />
                </div>
                <span>Thinking...</span>
              </div>
            )}

            {/* Context chips for user messages */}
            {isUser && message.context && message.context.length > 0 && (
              <div className="mt-1.5">
                <button
                  onClick={() => setShowContext(!showContext)}
                  className="flex items-center gap-1 text-[10px] text-text-muted hover:text-text-secondary transition-colors"
                >
                  {showContext ? (
                    <ChevronUp size={10} />
                  ) : (
                    <ChevronDown size={10} />
                  )}
                  <span>
                    {message.context.length} file
                    {message.context.length !== 1 ? "s" : ""} as context
                    {message.context.some((c: any) => c.autoContext) && (
                      <span className="ml-1 inline-flex items-center gap-0.5 text-accent">
                        <Zap size={8} />
                        auto
                      </span>
                    )}
                  </span>
                </button>
                {showContext && (
                  <div className="mt-1.5 flex flex-wrap gap-1.5">
                    {message.context.map((ctx: any, idx: number) => (
                      <div
                        key={idx}
                        className={`flex items-center gap-1 px-2 py-1 rounded-md text-[10px]
                          ${
                            ctx.autoContext
                              ? "bg-accent-subtle border border-accent/10 text-accent"
                              : "bg-bg-tertiary border border-border text-text-secondary"
                          }`}
                        title={ctx.filePath || ctx.name}
                      >
                        <FileCode size={10} />
                        <span className="truncate max-w-[140px]">
                          {ctx.name ||
                            ctx.filePath?.split(/[/\\]/).pop() ||
                            "File"}
                        </span>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
};

const SkeletonLoader: React.FC = () => (
  <div className="flex flex-col gap-2 py-1 animate-fade-in">
    <div className="h-3 w-[88%] rounded-full bg-bg-tertiary animate-shimmer" />
    <div
      className="h-3 w-[62%] rounded-full bg-bg-tertiary animate-shimmer"
      style={{ animationDelay: "0.2s" }}
    />
    <div
      className="h-3 w-[48%] rounded-full bg-bg-tertiary animate-shimmer"
      style={{ animationDelay: "0.4s" }}
    />
    <div
      className="h-3 w-[72%] rounded-full bg-bg-tertiary animate-shimmer"
      style={{ animationDelay: "0.6s" }}
    />
  </div>
);

function formatTime(ts: number): string {
  const d = new Date(ts);
  const now = new Date();
  const diffMs = now.getTime() - d.getTime();
  const diffMin = Math.floor(diffMs / 60000);
  if (diffMin < 1) return "Just now";
  if (diffMin < 60) return `${diffMin}m ago`;
  const diffHr = Math.floor(diffMin / 60);
  if (diffHr < 24) return `${diffHr}h ago`;
  return d.toLocaleDateString();
}
