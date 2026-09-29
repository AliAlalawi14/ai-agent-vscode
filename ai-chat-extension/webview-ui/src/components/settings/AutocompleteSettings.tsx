import React, { useEffect, useState } from 'react'
import { Loader2, Sparkles } from 'lucide-react'
import { vscode } from '../../services/vscodeApi'
import { openProviderSettings } from '../../stores/setupStore'
import { LocalAutocompleteCard } from './LocalAutocompleteCard'

type Style = 'auto' | 'completions' | 'mistral' | 'ollama' | 'chat'

interface CompletionState {
  enabled: boolean
  provider: string
  model: string
  style: Style
  providers: Array<{ id: string; label: string; models: string[]; detectedStyle: string }>
}

const styleNames: Record<string, string> = {
  completions: 'Fill-in-the-middle (/completions)',
  mistral: 'Mistral FIM (Codestral)',
  ollama: 'Ollama FIM',
  chat: 'Chat model (slower)',
}

const inputClass =
  'w-full px-2.5 py-1.5 rounded-md text-[12px] bg-bg-secondary border border-border text-text-primary ' +
  'placeholder:text-text-muted focus:outline-none focus:border-accent'

/** Settings → Autocomplete: ghost-text suggestions as you type, from a fill-in-the-middle model. */
export const AutocompleteSettings: React.FC = () => {
  const [state, setState] = useState<CompletionState | null>(null)
  const [enabled, setEnabled] = useState(false)
  const [provider, setProvider] = useState('')
  const [model, setModel] = useState('')
  const [style, setStyle] = useState<Style>('auto')
  const [testing, setTesting] = useState(false)
  const [test, setTest] = useState<{ ok: boolean; text?: string; ms?: number; error?: string } | null>(null)

  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      const msg = event.data
      if (msg?.type === 'completionState') {
        const s = msg.state as CompletionState
        setState(s)
        setEnabled(s.enabled)
        setProvider(s.provider)
        setModel(s.model)
        setStyle(s.style)
      }
      if (msg?.type === 'completionTest') {
        setTesting(false)
        setTest(msg)
      }
    }
    window.addEventListener('message', onMessage)
    vscode.postMessage({ type: 'getCompletion' })
    return () => window.removeEventListener('message', onMessage)
  }, [])

  if (!state) return null

  const chosen = state.providers.find((p) => p.id === provider)
  const settings = { enabled, provider, model, style }
  const dirty = enabled !== state.enabled || provider !== state.provider || model !== state.model || style !== state.style
  const ready = provider.length > 0 && model.trim().length > 0

  return (
    <div>
      <div className="flex items-center justify-between mb-1.5">
        <label className="flex items-center gap-1.5 text-[11px] font-medium text-text-secondary">
          <Sparkles size={12} />
          Autocomplete
        </label>
        <button
          disabled={!ready && !enabled}
          onClick={() => setEnabled(!enabled)}
          title={ready ? '' : 'Pick a provider and model first'}
          className={`relative w-8 h-[18px] rounded-full transition-colors disabled:opacity-40 ${enabled ? 'bg-accent' : 'bg-bg-tertiary border border-border'}`}
        >
          <div className={`absolute top-[2px] w-3.5 h-3.5 rounded-full bg-white shadow transition-transform ${enabled ? 'translate-x-[15px]' : 'translate-x-[2px]'}`} />
        </button>
      </div>
      <LocalAutocompleteCard />

      <p className="text-[10px] text-text-muted leading-snug mb-2">
        Ghost-text suggestions as you type; Tab accepts. Code-completion models work best: a small local coder model
        on Ollama (e.g. <code>qwen2.5-coder:1.5b-base</code>), Codestral, or DeepSeek. The code around your cursor is
        sent to this provider.
      </p>

      {state.providers.length <= 1 && !provider ? (
        <p className="text-[11px] text-text-muted">
          Or use a cloud provider: add DeepSeek, Mistral (Codestral) or another OpenAI-compatible provider first.{' '}
          <button onClick={() => openProviderSettings()} className="text-accent hover:underline">Add provider</button>
        </p>
      ) : state.providers.length === 0 ? (
        <p className="text-[11px] text-text-muted">
          Add DeepSeek, Mistral, Ollama or another OpenAI-compatible provider first.{' '}
          <button onClick={() => openProviderSettings()} className="text-accent hover:underline">Add provider</button>
        </p>
      ) : (
        <div className="space-y-1.5">
          <select className={inputClass} value={provider} onChange={(e) => {
            setProvider(e.target.value)
            const p = state.providers.find((x) => x.id === e.target.value)
            setModel(p?.models[0] ?? '')
            setTest(null)
          }}>
            <option value="">Choose a provider…</option>
            {state.providers.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
          </select>

          {provider && (
            <>
              <input className={inputClass} list="stoat-completion-models" placeholder="Model id" value={model}
                onChange={(e) => { setModel(e.target.value); setTest(null) }} />
              <datalist id="stoat-completion-models">
                {chosen?.models.map((m) => <option key={m} value={m} />)}
              </datalist>
              <select className={inputClass} value={style} onChange={(e) => { setStyle(e.target.value as Style); setTest(null) }}>
                <option value="auto">Auto: {styleNames[chosen?.detectedStyle ?? 'chat'] ?? 'detect'}</option>
                <option value="completions">{styleNames.completions}</option>
                <option value="mistral">{styleNames.mistral}</option>
                <option value="ollama">{styleNames.ollama}</option>
                <option value="chat">{styleNames.chat}</option>
              </select>
            </>
          )}

          <div className="flex gap-1.5">
            <button disabled={!ready || testing} onClick={() => { setTesting(true); setTest(null); vscode.postMessage({ type: 'testCompletion', settings }) }}
              className="flex items-center gap-1 px-2.5 py-1 rounded-md text-[11px] bg-bg-tertiary text-text-secondary hover:text-text-primary disabled:opacity-40">
              {testing && <Loader2 size={11} className="animate-spin" />} Try it
            </button>
            <button disabled={!dirty} onClick={() => vscode.postMessage({ type: 'saveCompletion', settings })}
              className="px-3 py-1 rounded-md text-[11px] font-medium bg-accent text-white disabled:opacity-40">
              Save
            </button>
          </div>

          {test && (
            <div className={`text-[11px] rounded-md px-2.5 py-1.5 border ${test.ok ? 'border-success/30 bg-success/10' : 'border-error/30 bg-error-subtle text-error'}`}>
              {test.ok ? (
                <>
                  <span className="text-success">Works ({test.ms} ms).</span> Suggested for <code>return ▮</code>:
                  <pre className="mt-1 text-[11px] text-text-primary whitespace-pre-wrap">{test.text}</pre>
                </>
              ) : (
                test.error
              )}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
