import React, { useEffect, useState } from 'react'
import { CheckCircle2, Cpu, ExternalLink, Loader2 } from 'lucide-react'
import { vscode } from '../../services/vscodeApi'

interface LocalState {
  ollama: 'running' | 'stopped' | 'missing'
  version?: string
  ramGb: number
  recommended: { name: string; sizeGb: number; why: string }
  installed: boolean
  active: boolean
}

/**
 * "Run on this computer (free, offline)": one button from nothing to local ghost-text suggestions. Checks Ollama,
 * downloads the small coder model that fits this machine, switches autocomplete to it and times a first suggestion.
 */
export const LocalAutocompleteCard: React.FC = () => {
  const [state, setState] = useState<LocalState | null>(null)
  const [busy, setBusy] = useState<'start' | 'setup' | null>(null)
  const [progress, setProgress] = useState<{ status: string; percent?: number } | null>(null)
  const [done, setDone] = useState<{ ok: boolean; text?: string; ms?: number; error?: string } | null>(null)

  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      const msg = event.data
      if (msg?.type === 'localAutocomplete') { setState(msg.state); if (busy === 'start') setBusy(null) }
      if (msg?.type === 'localPull') setProgress({ status: msg.status, percent: msg.percent })
      if (msg?.type === 'localDone') { setBusy(null); setProgress(null); setDone(msg) }
    }
    window.addEventListener('message', onMessage)
    vscode.postMessage({ type: 'getLocalAutocomplete' })
    return () => window.removeEventListener('message', onMessage)
  }, [busy])

  if (!state) return null
  const model = state.recommended

  return (
    <div className="rounded-lg border border-accent/30 bg-accent-subtle/40 p-2.5 mb-2">
      <div className="flex items-center gap-1.5 text-[12px] font-medium text-text-primary">
        <Cpu size={12} className="text-accent" /> Run on this computer
        <span className="text-[10px] font-normal text-text-muted">free · private · works offline</span>
      </div>

      {state.active && !busy ? (
        <p className="flex items-center gap-1.5 text-[11px] text-success mt-1.5">
          <CheckCircle2 size={12} /> On: suggestions come from {model.name} on this computer.
        </p>
      ) : (
        <p className="text-[10px] text-text-muted leading-snug mt-1">
          Recommended for your {state.ramGb} GB machine: <code>{model.name}</code> ({model.sizeGb} GB). {model.why}
        </p>
      )}

      {state.ollama === 'missing' && (
        <div className="mt-2 flex flex-wrap items-center gap-1.5 text-[11px]">
          <span className="text-text-secondary">Needs Ollama (free):</span>
          <button onClick={() => vscode.postMessage({ type: 'openExternal', url: 'https://ollama.com/download' })}
            className="flex items-center gap-1 px-2 py-0.5 rounded-md bg-accent text-white">
            Install Ollama <ExternalLink size={9} />
          </button>
          <button onClick={() => vscode.postMessage({ type: 'getLocalAutocomplete' })} className="text-accent hover:underline">
            I installed it
          </button>
        </div>
      )}

      {state.ollama === 'stopped' && (
        <div className="mt-2 flex items-center gap-1.5 text-[11px]">
          <span className="text-text-secondary">Ollama is installed but not running.</span>
          <button disabled={busy !== null} onClick={() => { setBusy('start'); vscode.postMessage({ type: 'startOllama' }) }}
            className="flex items-center gap-1 px-2 py-0.5 rounded-md bg-accent text-white disabled:opacity-50">
            {busy === 'start' && <Loader2 size={10} className="animate-spin" />} Start it
          </button>
        </div>
      )}

      {state.ollama === 'running' && !state.active && busy === null && (
        <button onClick={() => { setDone(null); setBusy('setup'); vscode.postMessage({ type: 'setupLocalAutocomplete', model: model.name }) }}
          className="mt-2 px-3 py-1 rounded-md text-[11px] font-medium bg-accent text-white">
          {state.installed ? 'Use it for autocomplete' : `Download (${model.sizeGb} GB) and turn on`}
        </button>
      )}

      {busy === 'setup' && (
        <div className="mt-2">
          <div className="flex items-center justify-between text-[10px] text-text-muted mb-1">
            <span className="truncate">{progress?.status || 'Starting…'}</span>
            <button onClick={() => vscode.postMessage({ type: 'cancelLocalPull' })} className="hover:text-error">Cancel</button>
          </div>
          <div className="h-1.5 rounded-full bg-bg-tertiary overflow-hidden">
            <div className="h-full bg-accent transition-all" style={{ width: `${progress?.percent ?? 3}%` }} />
          </div>
        </div>
      )}

      {done && (
        <p className={`text-[11px] mt-2 ${done.ok ? 'text-success' : 'text-error'}`}>
          {done.ok
            ? `Ready. First suggestion in ${done.ms} ms: ${JSON.stringify(done.text ?? '').slice(0, 60)}`
            : done.error}
        </p>
      )}
    </div>
  )
}
