import { create } from 'zustand'

export interface SymbolItem {
  type: 'class' | 'method' | 'property'
  name: string
  className: string
  filePath: string
}

export interface MentionItem {
  id: string
  kind: 'file' | 'symbol'
  name: string
  filePath: string
  className?: string
  symbolType?: string
}

interface MentionState {
  // Search state
  isSearching: boolean
  query: string
  symbolResults: SymbolItem[]
  fileResults: string[]

  // Selected context (pills above input)
  context: MentionItem[]

  // Open and recent files for quick access
  openFiles: string[]
  recentFiles: string[]

  // Actions
  setQuery: (query: string) => void
  setSearching: (isSearching: boolean) => void
  setSymbolResults: (query: string, symbols: SymbolItem[]) => void
  setFileResults: (query: string, files: string[]) => void
  setOpenFiles: (files: string[]) => void
  setRecentFiles: (files: string[]) => void
  addContext: (item: MentionItem) => void
  removeContext: (id: string) => void
  clearContext: () => void
  clearResults: () => void
}

export const useMentionStore = create<MentionState>((set, get) => ({
  isSearching: false,
  query: '',
  symbolResults: [],
  fileResults: [],
  context: [],
  openFiles: [],
  recentFiles: [],

  setQuery: (query) => set({ query }),

  setSearching: (isSearching) => set({ isSearching }),

  setOpenFiles: (files) => set({ openFiles: files }),

  setRecentFiles: (files) => set({ recentFiles: files }),

  setSymbolResults: (query, symbols) => {
    // Only update if this response matches the current query
    if (get().query === query) {
      set({ symbolResults: symbols, isSearching: false })
    }
  },

  setFileResults: (query, files) => {
    if (get().query === query) {
      set({ fileResults: files, isSearching: false })
    }
  },

  addContext: (item) => {
    const existing = get().context
    // Prevent duplicates
    if (existing.some(c => c.id === item.id)) return
    set({ context: [...existing, item] })
  },

  removeContext: (id) => {
    set(state => ({ context: state.context.filter(c => c.id !== id) }))
  },

  clearContext: () => set({ context: [] }),

  clearResults: () => set({ symbolResults: [], fileResults: [], query: '', isSearching: false }),
}))
