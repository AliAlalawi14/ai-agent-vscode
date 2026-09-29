import { create } from 'zustand'
import { vscode } from '../services/vscodeApi'

/** Mirrors SetupState in src/shared/protocol.ts (sent by the extension as "setupState"). */
export interface SetupPreset {
  id: string
  label: string
  detail: string
  kind: 'builtin' | 'openai' | 'custom'
  auth: 'bearer' | 'api-key' | 'none'
  baseUrl: string
  keyUrl?: string
  needsResource?: boolean
  local?: boolean
}

export interface ConfiguredProvider {
  name: string
  label: string
  models: string[]
  removable: boolean
}

export interface SetupState {
  needsSetup: boolean
  external: boolean
  backendUrl: string | null
  problem: string | null
  /** Edits apply at once and are reviewed afterwards (aiChat.reviewEdits) */
  reviewEdits: boolean
  presets: SetupPreset[]
  providers: ConfiguredProvider[]
}

interface SetupStore extends SetupState {
  loaded: boolean
  setSetup: (setup: SetupState) => void
  /** Saves aiChat.reviewEdits (user settings) */
  setReviewEdits: (on: boolean) => void
}

export const useSetupStore = create<SetupStore>((set) => ({
  loaded: false,
  needsSetup: false,
  external: false,
  backendUrl: null,
  problem: null,
  reviewEdits: true,
  presets: [],
  providers: [],
  setSetup: (setup) => set({ ...setup, loaded: true }),
  setReviewEdits: (on) => {
    set({ reviewEdits: on })
    vscode.postMessage({ type: 'updateSettings', settings: { reviewEdits: on } })
  },
}))

/** Opens the Settings panel (from the model menu, banners...); add = straight on the "add provider" form. */
export function openProviderSettings(add = true): void {
  window.dispatchEvent(new CustomEvent<{ add: boolean }>('ai-agent:open-settings', { detail: { add } }))
}
