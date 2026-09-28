import { create } from "zustand";
import type { Message } from "./chatStore";
import { useChatStore } from "./chatStore";
import { vscode } from "../services/vscodeApi";
import { usePlanStore, type Plan } from "./planStore";

export interface Conversation {
  id: string;
  title: string;
  messages: Message[];
  /** The conversation's plan (Plan mode), restored when the conversation is opened again */
  plan?: Plan | null;
  createdAt: number;
  updatedAt: number;
}

interface ConversationState {
  conversations: Conversation[];
  activeConversationId: string | null;
  showHistory: boolean;

  // Actions
  setConversations: (conversations: Conversation[]) => void;
  setActiveConversationId: (id: string | null) => void;
  saveCurrentConversation: (messages: Message[]) => void;
  loadConversation: (id: string) => void;
  deleteConversation: (id: string) => void;
  renameConversation: (id: string, title: string) => void;
  toggleHistory: () => void;
  setShowHistory: (show: boolean) => void;
  hydrateConversations: (
    conversations: Conversation[],
    activeId: string | null,
  ) => void;
}

function generateTitle(messages: Message[]): string {
  const firstUser = messages.find((m) => m.role === "user");
  if (!firstUser) return "New Conversation";
  const text = firstUser.content.slice(0, 50);
  return text.length < firstUser.content.length ? text + "..." : text;
}

export const useConversationStore = create<ConversationState>((set, get) => ({
  conversations: [],
  activeConversationId: null,
  showHistory: false,

  setConversations: (conversations) => set({ conversations }),

  setActiveConversationId: (id) => set({ activeConversationId: id }),

  hydrateConversations: (conversations, activeId) => {
    set({
      conversations,
      activeConversationId: activeId,
    });
  },

  saveCurrentConversation: (messages) => {
    if (messages.length === 0) return;

    const { activeConversationId, conversations } = get();
    const now = Date.now();

    if (activeConversationId) {
      // Update existing
      const updated = conversations.map((c) =>
        c.id === activeConversationId
          ? {
              ...c,
              messages,
              plan: usePlanStore.getState().plan,
              updatedAt: now,
              title: c.title || generateTitle(messages),
            }
          : c,
      );
      set({ conversations: updated });
    } else {
      // Create new
      const id = `conv-${now}-${Math.random().toString(36).substr(2, 6)}`;
      const newConv: Conversation = {
        id,
        title: generateTitle(messages),
        messages,
        plan: usePlanStore.getState().plan,
        createdAt: now,
        updatedAt: now,
      };
      set({
        conversations: [newConv, ...conversations].slice(0, 50),
        activeConversationId: id,
      });
    }

    // Persist via extension
    vscode.postMessage({
      type: "saveConversations",
      conversations: get().conversations,
    });
  },

  loadConversation: (id) => {
    const conv = get().conversations.find((c) => c.id === id);
    if (!conv) return;

    set({ activeConversationId: id, showHistory: false });

    // Hydrate chatStore directly — conversations are fully loaded
    // client-side from restoreState, no round-trip needed
    const { hydrateState, clearChat } = useChatStore.getState();
    clearChat();
    hydrateState(conv.messages, [], []);
    if (conv.plan) usePlanStore.getState().setPlan(conv.plan);
    else usePlanStore.getState().clearPlan();
  },

  deleteConversation: (id) => {
    const updated = get().conversations.filter((c) => c.id !== id);
    set({ conversations: updated });
    if (get().activeConversationId === id) {
      set({ activeConversationId: null });
    }
    vscode.postMessage({ type: "saveConversations", conversations: updated });
  },

  renameConversation: (id, title) => {
    const updated = get().conversations.map((c) =>
      c.id === id ? { ...c, title } : c,
    );
    set({ conversations: updated });
    vscode.postMessage({ type: "saveConversations", conversations: updated });
  },

  toggleHistory: () => set((state) => ({ showHistory: !state.showHistory })),
  setShowHistory: (show) => set({ showHistory: show }),
}));
