import React, { useEffect, useState } from 'react'
import { Blocks, ChevronDown, ChevronRight, Download, ExternalLink, KeyRound, Loader2, Plus, Trash2, X } from 'lucide-react'
import { vscode } from '../../services/vscodeApi'
import { useMcpStore, type McpServerView } from '../../stores/mcpStore'

const inputClass =
  'w-full px-2.5 py-1.5 rounded-md text-[12px] bg-bg-secondary border border-border text-text-primary ' +
  'placeholder:text-text-muted focus:outline-none focus:border-accent'

const dot: Record<string, string> = {
  connected: 'bg-success',
  starting: 'bg-warning animate-pulse',
  error: 'bg-error',
  disabled: 'bg-text-muted/40',
  stopped: 'bg-text-muted/40',
}

const stateLabel: Record<string, string> = {
  connected: 'Connected',
  starting: 'Starting…',
  error: 'Failed',
  disabled: 'Off',
  stopped: 'Starts with the agent',
}

/** Settings → MCP servers: presets, import, custom servers, per-server status, tools and trust. */
export const McpSettings: React.FC = () => {
  const state = useMcpStore((s) => s.state)
  const result = useMcpStore((s) => s.result)
  const setResult = useMcpStore((s) => s.setResult)
  const [adding, setAdding] = useState(false)
  const [open, setOpen] = useState<string | null>(null)

  useEffect(() => {
    vscode.postMessage({ type: 'getMcp' })
  }, [])

  // While a server is starting, refresh until it settles
  const starting = state?.servers.some((s) => s.state === 'starting') ?? false
  useEffect(() => {
    if (!starting) return
    const timer = setInterval(() => vscode.postMessage({ type: 'getMcp' }), 2000)
    return () => clearInterval(timer)
  }, [starting])

  return (
    <div>
      <div className="flex items-center justify-between mb-1.5">
        <label className="flex items-center gap-1.5 text-[11px] font-medium text-text-secondary">
          <Blocks size={12} />
          MCP servers
        </label>
        {!adding && (
          <button onClick={() => setAdding(true)} className="flex items-center gap-1 text-[11px] text-accent hover:underline">
            <Plus size={11} /> Add
          </button>
        )}
      </div>
      <p className="text-[10px] text-text-muted leading-snug mb-2">
        Give the agent more tools: a browser, GitHub, library docs, databases… Tools ask before they run unless you
        allow them.
      </p>

      {result && (
        <div
          className={`flex items-start gap-2 text-[11px] leading-snug rounded-md px-2.5 py-1.5 mb-2 border
            ${result.ok ? 'border-success/30 bg-success/10 text-success' : 'border-error/30 bg-error-subtle text-error'}`}
        >
          <span className="flex-1">{result.message}</span>
          <button onClick={() => setResult(null)} className="opacity-60 hover:opacity-100"><X size={11} /></button>
        </div>
      )}

      {state?.configErrors.map((e) => (
        <p key={e} className="text-[11px] text-error mb-1">{e}</p>
      ))}

      {adding && state && <AddServer state={state} onClose={() => setAdding(false)} />}

      {!state ? (
        <div className="flex items-center gap-2 text-[11px] text-text-muted"><Loader2 size={11} className="animate-spin" /> Loading…</div>
      ) : state.servers.length === 0 && !adding ? (
        <p className="text-[11px] text-text-muted">None yet.</p>
      ) : (
        <div className="rounded-lg border border-border divide-y divide-border">
          {state.servers.map((server) => (
            <ServerRow
              key={server.name}
              server={server}
              open={open === server.name}
              onToggleOpen={() => setOpen(open === server.name ? null : server.name)}
            />
          ))}
        </div>
      )}
    </div>
  )
}

const ServerRow: React.FC<{ server: McpServerView; open: boolean; onToggleOpen: () => void }> = ({ server, open, onToggleOpen }) => {
  const enabled = server.state !== 'disabled'
  const disabledTools = new Set(server.entry.disabledTools ?? [])
  const allowAll = server.entry.alwaysAllow === true
  const [secretValues, setSecretValues] = useState<Record<string, string>>({})

  const setTools = (next: Set<string>) =>
    vscode.postMessage({ type: 'setMcpServer', name: server.name, disabledTools: [...next] })

  return (
    <div>
      <div className="flex items-center gap-2 px-2.5 py-1.5">
        <button onClick={onToggleOpen} className="flex items-center gap-2 min-w-0 flex-1 text-left">
          {open ? <ChevronDown size={11} className="text-text-muted shrink-0" /> : <ChevronRight size={11} className="text-text-muted shrink-0" />}
          <span className={`w-2 h-2 rounded-full shrink-0 ${dot[server.state] ?? 'bg-text-muted/40'}`} title={stateLabel[server.state] ?? server.state} />
          <span className="min-w-0">
            <span className="block text-[12px] text-text-primary truncate">{server.name}</span>
            <span className="block text-[10px] text-text-muted truncate" title={server.target}>
              {server.state === 'connected'
                ? `${server.tools.filter((t) => t.enabled).length} tools · ~${server.promptTokens.toLocaleString()} tokens per request`
                : server.missingSecrets.length > 0
                  ? 'Needs a value'
                  : stateLabel[server.state] ?? server.state}
            </span>
          </span>
        </button>
        <Toggle
          on={enabled}
          title={enabled ? 'Turn off' : 'Turn on'}
          onChange={(on) => vscode.postMessage({ type: 'setMcpServer', name: server.name, disabled: !on })}
        />
      </div>

      {open && (
        <div className="px-2.5 pb-2.5 space-y-2">
          <p className="text-[10px] text-text-muted break-all font-mono">{server.target}</p>

          {server.error && (
            <pre className="text-[10px] text-error bg-error-subtle border border-error/20 rounded-md px-2 py-1.5 whitespace-pre-wrap break-words max-h-32 overflow-y-auto">
              {server.error}
            </pre>
          )}

          {server.missingSecrets.map((name) => (
            <div key={name} className="flex gap-1.5 items-center">
              <KeyRound size={11} className="text-warning shrink-0" />
              <input
                type="password"
                className={inputClass}
                placeholder={`Value for ${name}`}
                value={secretValues[name] ?? ''}
                onChange={(e) => setSecretValues({ ...secretValues, [name]: e.target.value })}
              />
              <button
                disabled={!secretValues[name]}
                onClick={() => vscode.postMessage({ type: 'setMcpSecret', name, value: secretValues[name] })}
                className="px-2 py-1 rounded-md text-[11px] bg-accent text-white disabled:opacity-40"
              >
                Save
              </button>
            </div>
          ))}

          <label className="flex items-center justify-between text-[11px] text-text-secondary">
            <span>
              Run its tools without asking
              <span className="block text-[10px] text-text-muted">Only for servers you trust. Otherwise each call shows Run / Reject.</span>
            </span>
            <Toggle on={allowAll} onChange={(on) => vscode.postMessage({ type: 'setMcpServer', name: server.name, alwaysAllow: on })} />
          </label>

          {server.tools.length > 0 && (
            <div>
              <p className="text-[10px] font-medium text-text-muted uppercase tracking-wider mb-1">Tools (untick to hide from the agent)</p>
              <div className="max-h-44 overflow-y-auto rounded-md border border-border">
                {server.tools.map((tool) => (
                  <label key={tool.name} className="flex items-start gap-2 px-2 py-1 text-[11px] hover:bg-bg-tertiary cursor-pointer" title={tool.description}>
                    <input
                      type="checkbox"
                      className="mt-0.5 accent-[var(--accent,#2dd4bf)]"
                      checked={!disabledTools.has(tool.name)}
                      onChange={(e) => {
                        const next = new Set(disabledTools)
                        if (e.target.checked) next.delete(tool.name)
                        else next.add(tool.name)
                        setTools(next)
                      }}
                    />
                    <span className="min-w-0">
                      <span className="text-text-primary">{tool.name}</span>
                      {tool.readOnly && <span className="ml-1 text-[9px] px-1 rounded bg-bg-tertiary text-text-muted">read-only</span>}
                      <span className="block text-[10px] text-text-muted truncate">{tool.description}</span>
                    </span>
                  </label>
                ))}
              </div>
            </div>
          )}

          <button
            onClick={() => vscode.postMessage({ type: 'removeMcpServer', name: server.name })}
            className="flex items-center gap-1 text-[11px] text-text-muted hover:text-error"
          >
            <Trash2 size={11} /> Remove server
          </button>
        </div>
      )}
    </div>
  )
}

interface Row { key: string; value: string; secret: boolean }

const looksSecret = (key: string) =>
  /(token|secret|password|api[_-]?key|authorization|credential)/i.test(key) || /(^|[_-])(pat|auth|key)([_-]|$)/i.test(key)

const AddServer: React.FC<{ state: NonNullable<ReturnType<typeof useMcpStore.getState>['state']>; onClose: () => void }> = ({ state, onClose }) => {
  const [preset, setPreset] = useState<string | null>(null)
  const [presetValues, setPresetValues] = useState<Record<string, string>>({})
  const [name, setName] = useState('')
  const [kind, setKind] = useState<'command' | 'url'>('command')
  const [commandLine, setCommandLine] = useState('')
  const [url, setUrl] = useState('')
  const [rows, setRows] = useState<Row[]>([])

  const chosen = state.presets.find((p) => p.id === preset)
  const available = state.importSources.filter((s) => s.available)

  const addPreset = (id: string) => {
    const p = state.presets.find((x) => x.id === id)
    if (!p) return
    if (p.needs?.length) {
      setPreset(id)
      return
    }
    vscode.postMessage({ type: 'addMcpPreset', presetId: id, values: {} })
    onClose()
  }

  const saveCustom = () => {
    const parts = commandLine.match(/"[^"]*"|'[^']*'|\S+/g)?.map((p) => p.replace(/^["']|["']$/g, '')) ?? []
    const map = Object.fromEntries(rows.filter((r) => r.key.trim()).map((r) => [r.key.trim(), r.value]))
    vscode.postMessage({
      type: 'saveMcpServer',
      name,
      entry: kind === 'url' ? { url, headers: map } : { command: parts[0] ?? '', args: parts.slice(1), env: map },
      secretKeys: rows.filter((r) => r.secret && r.key.trim()).map((r) => `${kind === 'url' ? 'header' : 'env'}:${r.key.trim()}`),
    })
    onClose()
  }

  const canSave = name.trim().length > 0 && (kind === 'url' ? /^https?:\/\/\S+$/.test(url.trim()) : commandLine.trim().length > 0)

  return (
    <div className="rounded-lg border border-border p-3 mb-2 space-y-3">
      <div className="flex items-center justify-between">
        <span className="text-[12px] font-medium text-text-primary">Add an MCP server</span>
        <button onClick={onClose} className="text-text-muted hover:text-text-primary"><X size={12} /></button>
      </div>

      {chosen ? (
        <div className="space-y-2">
          <p className="text-[12px] text-text-primary">{chosen.label}</p>
          {chosen.needs?.map((need) => (
            <div key={need.key}>
              <div className="flex items-center justify-between mb-1">
                <span className="text-[11px] text-text-secondary">{need.label}</span>
                {need.url && (
                  <button onClick={() => vscode.postMessage({ type: 'openExternal', url: need.url })} className="flex items-center gap-1 text-[10px] text-accent hover:underline">
                    Get one <ExternalLink size={9} />
                  </button>
                )}
              </div>
              <input type="password" className={inputClass} value={presetValues[need.key] ?? ''}
                onChange={(e) => setPresetValues({ ...presetValues, [need.key]: e.target.value })} />
            </div>
          ))}
          <div className="flex gap-2">
            <button
              disabled={chosen.needs?.some((n) => !presetValues[n.key]?.trim())}
              onClick={() => { vscode.postMessage({ type: 'addMcpPreset', presetId: chosen.id, values: presetValues }); onClose() }}
              className="px-3 py-1.5 rounded-md text-[12px] font-medium bg-accent text-white disabled:opacity-40"
            >
              Add
            </button>
            <button onClick={() => setPreset(null)} className="text-[11px] text-text-muted hover:text-text-primary">Back</button>
          </div>
        </div>
      ) : (
        <>
          <div>
            <p className="text-[10px] font-medium text-text-muted uppercase tracking-wider mb-1.5">Quick add</p>
            <div className="grid grid-cols-1 gap-1.5">
              {state.presets.map((p) => (
                <button
                  key={p.id}
                  disabled={p.added}
                  onClick={() => addPreset(p.id)}
                  className="text-left px-2.5 py-1.5 rounded-lg bg-bg-secondary border border-border hover:border-accent/40 disabled:opacity-50"
                >
                  <span className="block text-[12px] text-text-primary">{p.label}{p.added ? ' · added' : ''}</span>
                  <span className="block text-[10px] text-text-muted leading-snug">{p.detail}</span>
                </button>
              ))}
            </div>
          </div>

          {available.length > 0 && (
            <div>
              <p className="text-[10px] font-medium text-text-muted uppercase tracking-wider mb-1.5">Import your servers from</p>
              <div className="flex flex-wrap gap-1.5">
                {available.map((s) => (
                  <button key={s.id}
                    onClick={() => { vscode.postMessage({ type: 'importMcp', sourceId: s.id }); onClose() }}
                    className="flex items-center gap-1 px-2 py-1 rounded-md text-[11px] bg-bg-tertiary text-text-secondary hover:text-text-primary">
                    <Download size={10} /> {s.label}
                  </button>
                ))}
              </div>
            </div>
          )}

          <div className="space-y-2">
            <p className="text-[10px] font-medium text-text-muted uppercase tracking-wider">Or any server</p>
            <input className={inputClass} placeholder="Name, e.g. postgres" value={name} onChange={(e) => setName(e.target.value)} />
            <div className="flex gap-1 text-[11px]">
              {(['command', 'url'] as const).map((k) => (
                <button key={k} onClick={() => setKind(k)}
                  className={`px-2 py-0.5 rounded-md ${kind === k ? 'bg-accent text-white' : 'bg-bg-tertiary text-text-secondary'}`}>
                  {k === 'command' ? 'Local command' : 'Remote URL'}
                </button>
              ))}
            </div>
            {kind === 'command' ? (
              <input className={`${inputClass} font-mono`} placeholder="npx -y @modelcontextprotocol/server-postgres postgresql://…"
                value={commandLine} onChange={(e) => setCommandLine(e.target.value)} />
            ) : (
              <input className={inputClass} placeholder="https://example.com/mcp" value={url} onChange={(e) => setUrl(e.target.value)} />
            )}
            <div className="space-y-1">
              {rows.map((row, i) => (
                <div key={i} className="flex gap-1 items-center">
                  <input className={`${inputClass} w-2/5`} placeholder={kind === 'url' ? 'Header' : 'Variable'} value={row.key}
                    onChange={(e) => {
                      const next = [...rows]
                      next[i] = { ...row, key: e.target.value, secret: row.secret || looksSecret(e.target.value) }
                      setRows(next)
                    }} />
                  <input className={inputClass} type={row.secret ? 'password' : 'text'} placeholder="Value" value={row.value}
                    onChange={(e) => { const next = [...rows]; next[i] = { ...row, value: e.target.value }; setRows(next) }} />
                  <button title={row.secret ? 'Stored in secret storage' : 'Mark as secret'}
                    onClick={() => { const next = [...rows]; next[i] = { ...row, secret: !row.secret }; setRows(next) }}
                    className={`p-1 rounded ${row.secret ? 'text-accent' : 'text-text-muted'}`}>
                    <KeyRound size={11} />
                  </button>
                  <button onClick={() => setRows(rows.filter((_, j) => j !== i))} className="p-1 text-text-muted hover:text-error"><X size={11} /></button>
                </div>
              ))}
              <button onClick={() => setRows([...rows, { key: '', value: '', secret: false }])} className="text-[11px] text-accent hover:underline">
                + {kind === 'url' ? 'Header' : 'Environment variable'}
              </button>
            </div>
            <button disabled={!canSave} onClick={saveCustom}
              className="w-full px-3 py-1.5 rounded-md text-[12px] font-medium bg-accent text-white disabled:opacity-40">
              Save server
            </button>
            <p className="text-[10px] text-text-muted leading-snug">
              Local servers run on your computer with your permissions: add only ones you trust. Secret values are kept in
              VS Code's secret storage, never in settings files.
            </p>
          </div>
        </>
      )}
    </div>
  )
}

const Toggle: React.FC<{ on: boolean; onChange: (on: boolean) => void; title?: string }> = ({ on, onChange, title }) => (
  <button
    title={title}
    onClick={() => onChange(!on)}
    className={`relative w-8 h-[18px] rounded-full transition-colors shrink-0 ${on ? 'bg-accent' : 'bg-bg-tertiary border border-border'}`}
  >
    <div className={`absolute top-[2px] w-3.5 h-3.5 rounded-full bg-white shadow transition-transform ${on ? 'translate-x-[15px]' : 'translate-x-[2px]'}`} />
  </button>
)
