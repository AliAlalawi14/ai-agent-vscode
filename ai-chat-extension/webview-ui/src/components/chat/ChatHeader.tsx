import React from "react";
import {
  Plus,
  History,
  Settings,
  MessageSquare,
  ChevronDown,
} from "lucide-react";
import { ModelSelector } from "../common/ModelSelector";
import { HealthIndicator } from "../common/HealthIndicator";
import { useConversationStore } from "../../stores/conversationStore";

interface ChatHeaderProps {
  onNewChat: () => void;
  onToggleHistory: () => void;
  onOpenSettings: () => void;
  showHistory: boolean;
}

export const ChatHeader: React.FC<ChatHeaderProps> = ({
  onNewChat,
  onToggleHistory,
  onOpenSettings,
  showHistory,
}) => {
  const activeId = useConversationStore((state) => state.activeConversationId);

  return (
    <div className="flex items-center justify-between px-3 py-2 border-b border-border/60 bg-bg-primary shrink-0">
      {/* Left: Brand + Model */}
      <div className="flex items-center gap-2">
        <div className="flex items-center gap-1.5">
          <div className="w-5 h-5 rounded-md bg-accent-subtle flex items-center justify-center">
            <MessageSquare size={11} className="text-accent" />
          </div>
          <span className="text-[12px] font-semibold text-text-primary tracking-tight">
            Stoat
          </span>
          {activeId && (
            <span className="text-[10px] text-text-muted ml-1">
              · Conversation
            </span>
          )}
        </div>
      </div>

      {/* Center: Status + Model */}
      <div className="flex items-center gap-2">
        <HealthIndicator />
        <ModelSelector />
      </div>

      {/* Right: Actions */}
      <div className="flex items-center gap-0.5">
        <button
          onClick={onNewChat}
          className="p-1.5 rounded-md text-text-secondary hover:text-text-primary
                     hover:bg-bg-tertiary transition-all duration-150 active:scale-95"
          title="New Chat (Ctrl+Shift+N)"
        >
          <Plus size={15} />
        </button>
        <button
          onClick={onToggleHistory}
          className={`p-1.5 rounded-md transition-all duration-150 active:scale-95
            ${
              showHistory
                ? "text-accent bg-accent-subtle"
                : "text-text-secondary hover:text-text-primary hover:bg-bg-tertiary"
            }
          `}
          title="Chat History"
        >
          <History size={15} />
        </button>
        <button
          onClick={onOpenSettings}
          className="p-1.5 rounded-md text-text-secondary hover:text-text-primary
                     hover:bg-bg-tertiary transition-all duration-150 active:scale-95"
          title="Settings"
        >
          <Settings size={15} />
        </button>
      </div>
    </div>
  );
};
