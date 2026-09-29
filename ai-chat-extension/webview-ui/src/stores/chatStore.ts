import type { VerifyData } from '../components/changes/VerifyCard'
import { create } from 'zustand'

// ── Segment types for inline Windsurf-style flow ────────────────────────
export type MessageSegment =
  | { type: 'text'; content: string }
  | { type: 'tool'; toolId: string }
  | { type: 'change'; changeId: string }
  | { type: 'plan' }
  /** Plan mode's clarifying questions; `answers` is set once the user submitted them */
  | { type: 'questions'; questions: PlanQuestion[]; answers?: string[] }
  /** The verify loop's result (build + tests after the agent's changes); updated in place */
  | { type: 'verify'; verify: VerifyData }

export interface PlanQuestion {
  question: string
  options: string[]
  multiple?: boolean
}

export interface MessageMetrics {
  tokens?: number
  cost?: number
  latencyMs?: number
}

export interface Message {
  id: string
  role: 'user' | 'assistant'
  content: string              // plain text aggregate (for cleanup / compat)
  segments: MessageSegment[]   // ordered timeline of text, tools, changes
  timestamp: number
  isStreaming?: boolean
  metrics?: MessageMetrics
  context?: any[]              // context files/symbols sent with message
}

export interface ToolExecution {
  id: string
  tool: string
  args: Record<string, unknown>
  /** 'awaiting' = the agent is paused on this call until the user approves it */
  status: 'running' | 'awaiting' | 'completed' | 'error'
  result?: string
}

interface ChatState {
  messages: Message[]
  activeTools: Map<string, ToolExecution>
  toolHistory: ToolExecution[]
  isStreaming: boolean
  error: string | null

  // Actions
  addUserMessage: (content: string, context?: any[]) => void
  startAssistantMessage: () => string
  appendToMessage: (id: string, content: string) => void
  addToolSegment: (messageId: string, toolId: string) => void
  addChangeSegment: (messageId: string, changeId: string) => void
  addPlanSegment: (messageId: string) => void
  addQuestionsSegment: (messageId: string, questions: PlanQuestion[]) => void
  /** Records the answers on the questions card (so it shows them and can't be submitted twice) */
  answerQuestions: (messageId: string, answers: string[]) => void
  finalizeMessage: (id: string) => void
  addToolStart: (id: string, tool: string, args: Record<string, unknown>) => void
  addToolResult: (id: string, result: string, status: string) => void
  setMessageMetrics: (id: string, metrics: MessageMetrics) => void
  setStreaming: (streaming: boolean) => void
  setToolStatus: (id: string, status: ToolExecution['status']) => void
  /** Time of the last stream event (token/tool/approval/change), for real stall detection */
  lastEventAt: number
  touchStream: () => void
  /** Steps used when the last run hit its step budget (null = not paused) */
  limitReached: number | null
  setLimitReached: (steps: number | null) => void
  /** The task stopped at the user's budget: what it cost and the cap */
  budgetStop: { spent: number; budget: number } | null
  setBudgetStop: (stop: { spent: number; budget: number } | null) => void
  /** Adds or updates the verify card of a message */
  setVerify: (messageId: string, verify: VerifyData) => void
  /** Removes an assistant message that never received any content (e.g. the request failed) */
  removeIfEmpty: (id: string) => void
  /** Stop: ends the run in the UI at once (no waiting for the stream): the reply is closed with a note, tools stop spinning */
  stopRun: () => void
  setError: (error: string | null) => void
  clearChat: () => void
  hydrateState: (messages: Message[], activeTools: ToolExecution[], toolHistory: ToolExecution[]) => void
}

export const useChatStore = create<ChatState>((set, get) => ({
  messages: [],
  activeTools: new Map(),
  toolHistory: [],
  isStreaming: false,
  error: null,

  addUserMessage: (content: string, context?: any[]) => {
    const message: Message = {
      id: `user-${Date.now()}`,
      role: 'user',
      content,
      segments: [{ type: 'text', content }],
      timestamp: Date.now(),
      context
    }
    set(state => ({ messages: [...state.messages, message] }))
  },

  startAssistantMessage: () => {
    const id = `assistant-${Date.now()}`
    const message: Message = {
      id,
      role: 'assistant',
      content: '',
      segments: [],
      timestamp: Date.now(),
      isStreaming: true
    }
    set(state => ({
      messages: [...state.messages, message],
      isStreaming: true,
      error: null
    }))
    return id
  },

  // Append text to the last text segment, or create a new one
  appendToMessage: (id: string, content: string) => {
    set(state => ({
      messages: state.messages.map(msg => {
        if (msg.id !== id) return msg

        const segs = [...msg.segments]
        const last = segs[segs.length - 1]

        if (last && last.type === 'text') {
          // Append to existing text segment
          segs[segs.length - 1] = { ...last, content: last.content + content }
        } else {
          // Create new text segment
          segs.push({ type: 'text', content })
        }

        return { ...msg, content: msg.content + content, segments: segs }
      })
    }))
  },

  // Insert a tool segment into the current message timeline
  addToolSegment: (messageId: string, toolId: string) => {
    set(state => ({
      messages: state.messages.map(msg =>
        msg.id === messageId
          ? { ...msg, segments: [...msg.segments, { type: 'tool' as const, toolId }] }
          : msg
      )
    }))
  },

  addQuestionsSegment: (messageId: string, questions: PlanQuestion[]) => {
    set(state => ({
      messages: state.messages.map(msg =>
        msg.id === messageId
          ? { ...msg, segments: [...msg.segments, { type: 'questions' as const, questions }] }
          : msg
      )
    }))
  },

  answerQuestions: (messageId: string, answers: string[]) => {
    set(state => ({
      messages: state.messages.map(msg =>
        msg.id === messageId
          ? {
              ...msg,
              segments: msg.segments.map(seg => (seg.type === 'questions' ? { ...seg, answers } : seg))
            }
          : msg
      )
    }))
  },

  // Insert a plan segment into the current message timeline
  addPlanSegment: (messageId: string) => {
    set(state => ({
      messages: state.messages.map(msg =>
        msg.id === messageId
          ? { ...msg, segments: [...msg.segments, { type: 'plan' as const }] }
          : msg
      )
    }))
  },

  addChangeSegment: (messageId: string, changeId: string) => {
    set(state => ({
      messages: state.messages.map(msg =>
        msg.id === messageId
          ? { ...msg, segments: [...msg.segments, { type: 'change' as const, changeId }] }
          : msg
      )
    }))
  },

  finalizeMessage: (id: string) => {
    set(state => ({
      messages: state.messages.map(msg =>
        msg.id === id ? { ...msg, isStreaming: false } : msg
      ),
      isStreaming: false
    }))
  },

  addToolStart: (id: string, tool: string, args: Record<string, unknown>) => {
    const execution: ToolExecution = {
      id,
      tool,
      args,
      status: 'running'
    }
    set(state => {
      const newActiveTools = new Map(state.activeTools)
      newActiveTools.set(id, execution)
      return { activeTools: newActiveTools }
    })
  },

  addToolResult: (id: string, result: string, status: string) => {
    set(state => {
      const tool = state.activeTools.get(id)
      if (!tool) return state

      const updatedTool: ToolExecution = {
        ...tool,
        status: status === 'error' ? 'error' : 'completed',
        result
      }

      const newActiveTools = new Map(state.activeTools)
      newActiveTools.delete(id)

      return {
        activeTools: newActiveTools,
        toolHistory: [...state.toolHistory, updatedTool]
      }
    })
  },

  setMessageMetrics: (id: string, metrics: MessageMetrics) => {
    set(state => ({
      messages: state.messages.map(msg =>
        msg.id === id ? { ...msg, metrics } : msg
      )
    }))
  },

  setStreaming: (streaming: boolean) => set({ isStreaming: streaming }),

  setToolStatus: (id, status) => {
    set(state => {
      const tool = state.activeTools.get(id)
      if (!tool) return state
      const newActiveTools = new Map(state.activeTools)
      newActiveTools.set(id, { ...tool, status })
      return { activeTools: newActiveTools }
    })
  },

  lastEventAt: 0,
  touchStream: () => set({ lastEventAt: Date.now() }),

  limitReached: null,
  setLimitReached: (steps) => set({ limitReached: steps }),

  budgetStop: null,
  setBudgetStop: (stop) => set({ budgetStop: stop }),

  setVerify: (messageId, verify) =>
    set(state => ({
      messages: state.messages.map(msg => {
        if (msg.id !== messageId) return msg
        // Each check attempt has its own card; "running" becomes that attempt's result
        const at = msg.segments.findIndex(s => s.type === 'verify' && (s.verify.attempt ?? 0) === (verify.attempt ?? 0))
        const segments = at >= 0
          ? msg.segments.map((s, i) => (i === at ? { type: 'verify' as const, verify } : s))
          : [...msg.segments, { type: 'verify' as const, verify }]
        return { ...msg, segments }
      })
    })),

  removeIfEmpty: (id) =>
    set(state => ({
      messages: state.messages.filter(m =>
        m.id !== id || m.segments.some(s => s.type !== 'text' || s.content.trim().length > 0)
      ),
      isStreaming: false,
    })),

  stopRun: () =>
    set(state => {
      const stoppedTools = Array.from(state.activeTools.values()).map(t => ({
        ...t,
        status: 'error' as const,
        result: 'Stopped',
      }))
      return {
        messages: state.messages.map(m =>
          m.isStreaming
            ? {
                ...m,
                isStreaming: false,
                content: m.content ? `${m.content}

_Stopped._` : '_Stopped._',
                segments: [...m.segments, { type: 'text' as const, content: '_Stopped._' }],
              }
            : m
        ),
        activeTools: new Map(),
        toolHistory: [...state.toolHistory, ...stoppedTools],
        isStreaming: false,
      }
    }),

  setError: (error: string | null) => set({ error, isStreaming: false }),

  clearChat: () => set({
    messages: [],
    activeTools: new Map(),
    toolHistory: [],
    isStreaming: false,
    error: null
  }),

  hydrateState: (messages: Message[], activeTools: ToolExecution[], toolHistory: ToolExecution[]) => {
    // Restore streaming state to false (can't resume streams)
    const restoredMessages = messages.map(m => ({
      ...m,
      isStreaming: false
    }))
    // Rebuild activeTools Map from array
    const toolsMap = new Map<string, ToolExecution>()
    activeTools.forEach(t => toolsMap.set(t.id, t))
    set({
      messages: restoredMessages,
      activeTools: toolsMap,
      toolHistory,
      isStreaming: false,
      error: null
    })
  }
}))
