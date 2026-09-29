import React, { useEffect, useMemo, useRef, useState } from 'react'
import { ChevronLeft, ExternalLink, KeyRound, Laptop, Loader2, Search, Check } from 'lucide-react'
import { vscode } from '../../services/vscodeApi'
import { useSetupStore, type SetupPreset } from '../../stores/setupStore'

interface ProviderSetupProps {
  /** Called once the provider is saved and the backend is up */
  onDone?: () => void
}

type Auth = 'bearer' | 'api-key' | 'none'

let nextRequestId = 1

/**
 * Add a model provider without leaving the chat: pick a provider, paste the key, choose models
 * (read live from the provider), Save & connect. The extension stores the key in secret storage
 * and restarts the backend.
 */
export const ProviderSetup: React.FC<ProviderSetupProps> = ({ onDone }) => {
  const presets = useSetupStore((s) => s.presets)
  const [preset, setPreset] = useState<SetupPreset | null>(null)

  if (!preset) {
    return <PresetPicker presets={presets} onPick={setPreset} />
  }
  return <ProviderForm key={preset.id} preset={preset} onBack={() => setPreset(null)} onDone={onDone} />
}

const PresetPicker: React.FC<{ presets: SetupPreset[]; onPick: (p: SetupPreset) => void }> = ({ presets, onPick }) => {
  if (presets.length === 0) {
    return (
      <div className="flex items-center gap-2 text-[12px] text-text-muted py-4 justify-center">
        <Loader2 size={13} className="animate-spin" /> Loading providers…
      </div>
    )
  }
  const cloud = presets.filter((p) => !p.local && p.kind !== 'custom')
  const local = presets.filter((p) => p.local)
  const custom = presets.filter((p) => p.kind === 'custom')

  const card = (p: SetupPreset) => (
    <button
      key={p.id}
      onClick={() => onPick(p)}
      className="text-left px-2.5 py-2 rounded-lg bg-bg-secondary border border-border
                 hover:bg-bg-tertiary hover:border-accent/40 transition-colors min-w-0"
    >
      <div className="text-[12px] font-medium text-text-primary truncate">{p.label}</div>
      <div className="text-[10px] text-text-muted leading-snug line-clamp-2">{p.detail}</div>
    </button>
  )

  return (
    <div className="space-y-3">
      <Section title="Cloud providers (API key)">
        <div className="grid grid-cols-2 gap-1.5">{cloud.map(card)}</div>
      </Section>
      <Section title="On your machine (free, no key)">
        <div className="grid grid-cols-2 gap-1.5">{local.map(card)}</div>
      </Section>
      <div className="grid grid-cols-1 gap-1.5">{custom.map(card)}</div>
    </div>
  )
}

const Section: React.FC<{ title: string; children: React.ReactNode }> = ({ title, children }) => (
  <div>
    <p className="text-[10px] font-medium text-text-muted uppercase tracking-wider mb-1.5 px-0.5">{title}</p>
    {children}
  </div>
)

const inputClass =
  'w-full px-2.5 py-1.5 rounded-md text-[12px] bg-bg-secondary border border-border text-text-primary ' +
  'placeholder:text-text-muted focus:outline-none focus:border-accent'

const ProviderForm: React.FC<{ preset: SetupPreset; onBack: () => void; onDone?: () => void }> = ({
  preset,
  onBack,
  onDone,
}) => {
  const isBuiltin = preset.kind === 'builtin'
  const isCustom = preset.kind === 'custom'

  const [key, setKey] = useState('')
  const [resource, setResource] = useState('')
  const [baseUrl, setBaseUrl] = useState('')
  const [auth, setAuth] = useState<Auth>(preset.auth)

  const [models, setModels] = useState<string[] | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [filter, setFilter] = useState('')
  const [typed, setTyped] = useState('')
  const [manual, setManual] = useState(false)
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const pendingRequest = useRef(0)

  const needsKey = auth !== 'none'
  const request = () => ({
    presetId: preset.id,
    ...(needsKey && key.trim() ? { key: key.trim() } : {}),
    ...(preset.needsResource ? { resource: resource.trim() } : {}),
    ...(isCustom ? { baseUrl: baseUrl.trim(), auth } : {}),
  })

  // Replies from the extension
  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      const msg = event.data
      if (msg.type === 'providerModels' && msg.requestId === pendingRequest.current) {
        setLoading(false)
        setModels(msg.models)
        setError(msg.error ?? null)
        if (msg.models.length === 0) setManual(true)
        if (msg.models.length > 0 && msg.models.length <= 3) setSelected(new Set(msg.models))
      }
      if (msg.type === 'providerSaved') {
        setSaving(false)
        if (msg.ok) onDone?.()
        else setError(msg.error ?? 'Saving failed.')
      }
    }
    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
  }, [onDone])

  const loadModels = () => {
    setError(null)
    setLoading(true)
    pendingRequest.current = nextRequestId++
    vscode.postMessage({ type: 'listProviderModels', requestId: pendingRequest.current, provider: request() })
  }

  // Local servers need nothing typed: read their models right away
  useEffect(() => {
    if (preset.local) loadModels()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const chosenModels = manual
    ? typed.split(',').map((m) => m.trim()).filter(Boolean)
    : Array.from(selected)

  const canLoad =
    (!needsKey || key.trim().length > 0) &&
    (!preset.needsResource || resource.trim().length > 0) &&
    (!isCustom || /^https?:\/\/\S+$/.test(baseUrl.trim()))
  const canSave = isBuiltin ? key.trim().length > 0 : canLoad && chosenModels.length > 0

  const save = () => {
    if (!canSave || saving) return
    setError(null)
    setSaving(true)
    vscode.postMessage({ type: 'saveProvider', provider: request(), models: isBuiltin ? [] : chosenModels })
  }

  const visibleModels = useMemo(
    () => (models ?? []).filter((m) => m.toLowerCase().includes(filter.toLowerCase())),
    [models, filter],
  )

  const toggle = (m: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(m)) next.delete(m)
      else next.add(m)
      return next
    })
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-1.5">
        <button
          onClick={onBack}
          className="p-1 -ml-1 rounded hover:bg-bg-tertiary text-text-muted hover:text-text-primary"
          title="Other providers"
        >
          <ChevronLeft size={14} />
        </button>
        <div className="min-w-0">
          <div className="text-[13px] font-medium text-text-primary flex items-center gap-1.5">
            {preset.local && <Laptop size={12} className="text-text-muted" />}
            {preset.label}
          </div>
          <div className="text-[10px] text-text-muted truncate">{preset.detail}</div>
        </div>
      </div>

      {preset.needsResource && (
        <Field label="Azure resource name">
          <input
            className={inputClass}
            value={resource}
            onChange={(e) => setResource(e.target.value)}
            placeholder="my-resource (from my-resource.openai.azure.com)"
          />
        </Field>
      )}

      {isCustom && (
        <>
          <Field label="Base URL">
            <input
              className={inputClass}
              value={baseUrl}
              onChange={(e) => setBaseUrl(e.target.value)}
              placeholder="https://my-server.example.com/v1"
            />
          </Field>
          <Field label="Authentication">
            <select className={inputClass} value={auth} onChange={(e) => setAuth(e.target.value as Auth)}>
              <option value="bearer">Bearer token</option>
              <option value="api-key">api-key header (Azure style)</option>
              <option value="none">No key</option>
            </select>
          </Field>
        </>
      )}

      {needsKey && (
        <Field
          label="API key"
          action={
            preset.keyUrl ? (
              <button
                onClick={() => vscode.postMessage({ type: 'openExternal', url: preset.keyUrl })}
                className="flex items-center gap-1 text-[10px] text-accent hover:underline"
              >
                Get a key <ExternalLink size={9} />
              </button>
            ) : undefined
          }
        >
          <div className="relative">
            <KeyRound size={12} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-text-muted" />
            <input
              type="password"
              autoFocus
              className={`${inputClass} pl-7`}
              value={key}
              onChange={(e) => setKey(e.target.value)}
              onKeyDown={(e) => {
                if (e.key !== 'Enter') return
                if (isBuiltin) save()
                else if (canLoad) loadModels()
              }}
              placeholder="Paste your API key"
            />
          </div>
          <p className="text-[10px] text-text-muted mt-1">Kept in VS Code's secret storage, never in settings files.</p>
        </Field>
      )}

      {!isBuiltin && (
        <Field label="Models">
          {models === null && !manual && (
            <button
              onClick={loadModels}
              disabled={!canLoad || loading}
              className={`w-full flex items-center justify-center gap-1.5 px-2.5 py-1.5 rounded-md text-[12px] font-medium
                ${canLoad && !loading ? 'bg-bg-tertiary text-text-primary hover:bg-bg-active' : 'bg-bg-tertiary text-text-muted'}`}
            >
              {loading ? <Loader2 size={12} className="animate-spin" /> : <Search size={12} />}
              {loading ? 'Reading models…' : 'Load models'}
            </button>
          )}

          {models !== null && models.length > 0 && !manual && (
            <div className="rounded-md border border-border overflow-hidden">
              {models.length > 8 && (
                <input
                  className="w-full px-2.5 py-1.5 text-[12px] bg-bg-secondary border-b border-border text-text-primary
                             placeholder:text-text-muted focus:outline-none"
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                  placeholder={`Filter ${models.length} models…`}
                />
              )}
              <div className="max-h-44 overflow-y-auto">
                {visibleModels.map((m) => (
                  <label
                    key={m}
                    className="flex items-center gap-2 px-2.5 py-1 text-[12px] text-text-secondary hover:bg-bg-tertiary cursor-pointer"
                  >
                    <span
                      className={`w-3.5 h-3.5 shrink-0 rounded border flex items-center justify-center
                        ${selected.has(m) ? 'bg-accent border-accent' : 'border-border'}`}
                    >
                      {selected.has(m) && <Check size={10} className="text-white" />}
                    </span>
                    <input type="checkbox" className="hidden" checked={selected.has(m)} onChange={() => toggle(m)} />
                    <span className="truncate">{m}</span>
                  </label>
                ))}
                {visibleModels.length === 0 && (
                  <p className="px-2.5 py-2 text-[11px] text-text-muted">No model matches "{filter}".</p>
                )}
              </div>
            </div>
          )}

          {manual && (
            <input
              className={inputClass}
              value={typed}
              onChange={(e) => setTyped(e.target.value)}
              placeholder="Model ids, comma separated (e.g. qwen3:14b)"
            />
          )}

          <div className="flex items-center justify-between mt-1">
            <span className="text-[10px] text-text-muted">
              {chosenModels.length > 0
                ? `${chosenModels.length} selected · Agent and Plan modes need tool-calling models`
                : 'Agent and Plan modes need models with tool calling'}
            </span>
            {(models !== null || manual) && (
              <button
                onClick={() => (manual && models?.length ? setManual(false) : manual ? loadModels() : setManual(true))}
                className="text-[10px] text-accent hover:underline shrink-0 ml-2"
              >
                {manual ? (models?.length ? 'Pick from list' : 'Try loading again') : 'Type ids instead'}
              </button>
            )}
          </div>
        </Field>
      )}

      {error && <p className="text-[11px] text-error leading-snug">{error}</p>}

      <button
        onClick={save}
        disabled={!canSave || saving}
        className={`w-full flex items-center justify-center gap-1.5 px-3 py-2 rounded-md text-[12px] font-medium transition-colors
          ${canSave && !saving ? 'bg-accent text-white hover:bg-accent-hover' : 'bg-bg-tertiary text-text-muted'}`}
      >
        {saving && <Loader2 size={12} className="animate-spin" />}
        {saving ? 'Starting the agent…' : 'Save & connect'}
      </button>
    </div>
  )
}

const Field: React.FC<{ label: string; action?: React.ReactNode; children: React.ReactNode }> = ({
  label,
  action,
  children,
}) => (
  <div>
    <div className="flex items-center justify-between mb-1">
      <label className="text-[11px] font-medium text-text-secondary">{label}</label>
      {action}
    </div>
    {children}
  </div>
)
