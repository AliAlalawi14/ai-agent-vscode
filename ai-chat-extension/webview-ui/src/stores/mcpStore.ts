import { create } from 'zustand'

/** Mirrors McpState in src/shared/protocol.ts (sent by the extension as "mcpState"). */
export interface McpServerEntryView {
  command?: string
  args?: string[]
  env?: Record<string, string>
  url?: string
  headers?: Record<string, string>
  disabled?: boolean
  alwaysAllow?: boolean | string[]
  disabledTools?: string[]
}

export interface McpToolView {
  name: string
  description: string
  readOnly: boolean
  enabled: boolean
  alwaysAllowed: boolean
}

export interface McpServerView {
  name: string
  entry: McpServerEntryView
  transport: 'stdio' | 'http'
  target: string
  state: string
  error?: string
  tools: McpToolView[]
  promptTokens: number
  missingSecrets: string[]
}

export interface McpState {
  servers: McpServerView[]
  configErrors: string[]
  importSources: Array<{ id: string; label: string; available: boolean }>
  presets: Array<{ id: string; label: string; detail: string; added: boolean; needs?: Array<{ key: string; label: string; url?: string }> }>
  backendRunning: boolean
}

interface McpStore {
  state: McpState | null
  result: { ok: boolean; message: string } | null
  setState: (state: McpState) => void
  setResult: (result: { ok: boolean; message: string } | null) => void
}

export const useMcpStore = create<McpStore>((set) => ({
  state: null,
  result: null,
  setState: (state) => set({ state }),
  setResult: (result) => set({ result }),
}))

/** "mcp__github__create_issue" → { server: "github", tool: "create_issue" } */
export function parseMcpToolName(name: string): { server: string; tool: string } | null {
  const m = /^mcp__(.+?)__(.+)$/.exec(name)
  return m ? { server: m[1], tool: m[2] } : null
}

/** How a tool is named in the chat: "create issue (github)" for MCP tools, "read file" for built-in ones. */
export function friendlyToolName(name: string): string {
  const mcp = parseMcpToolName(name)
  return mcp ? `${mcp.tool.replace(/[_-]/g, ' ')} (${mcp.server})` : name.replace(/_/g, ' ')
}
