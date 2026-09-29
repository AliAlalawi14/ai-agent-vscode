import { create } from 'zustand'

export interface LLMModel {
  id: string
  name: string
  provider: string
}

interface SettingsState {
  // Model selection
  selectedModel: string | null
  availableModels: LLMModel[]

  // Backend (its address and providers are in setupStore)
  backendStatus: BackendStatus
  /** Why the backend is outdated/degraded, shown in the banner */
  backendDetail: string | null
  /** Extension bundles rebuilt after this window loaded them */
  staleBundles: string[]
  setStaleBundles: (bundles: string[]) => void

  /** Ask / Plan / Agent / Auto: sent with every request; the backend enforces it */
  mode: AgentMode
  setMode: (mode: AgentMode) => void
  lastHealthCheck: number | null

  // Preferences
  showMetrics: boolean

  // Actions
  setSelectedModel: (modelId: string) => void
  setAvailableModels: (models: LLMModel[]) => void
  setBackendStatus: (status: BackendStatus, detail?: string | null) => void
  setLastHealthCheck: (timestamp: number) => void
  setShowMetrics: (show: boolean) => void
}

export type AgentMode = 'ask' | 'plan' | 'agent' | 'auto'

const MODE_KEY = 'aiChat.mode'
function loadMode(): AgentMode {
  try {
    const saved = localStorage.getItem(MODE_KEY)
    if (saved === 'ask' || saved === 'plan' || saved === 'agent' || saved === 'auto') return saved
  } catch {
    // storage unavailable: fall back to the default
  }
  return 'agent'
}

/** setup = no model provider configured yet (the chat shows the setup form); starting = the backend is starting */
export type BackendStatus = 'connected' | 'degraded' | 'disconnected' | 'outdated' | 'setup' | 'starting'

export const useSettingsStore = create<SettingsState>((set) => ({
  selectedModel: null,
  availableModels: [],
  backendStatus: 'starting',   // until the first check answers: never a red dot on open
  backendDetail: null,
  staleBundles: [],
  setStaleBundles: (bundles) => set({ staleBundles: bundles }),

  mode: loadMode(),
  setMode: (mode) => {
    try {
      localStorage.setItem(MODE_KEY, mode)
    } catch {
      // not persisted; still applies to this session
    }
    set({ mode })
  },
  lastHealthCheck: null,
  showMetrics: true,

  setSelectedModel: (modelId) => set({ selectedModel: modelId }),
  setAvailableModels: (models) => set({ availableModels: models }),
  setBackendStatus: (status, detail = null) => set({ backendStatus: status, backendDetail: detail }),
  setLastHealthCheck: (timestamp) => set({ lastHealthCheck: timestamp }),
  setShowMetrics: (show) => set({ showMetrics: show }),
}))
