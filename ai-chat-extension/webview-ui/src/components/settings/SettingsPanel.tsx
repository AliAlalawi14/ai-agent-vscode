import React, { useState } from 'react'
import { X, Server, Brain, Eye, Plug, Plus, Trash2, RotateCw, FileText, Loader2 } from 'lucide-react'
import { useSettingsStore } from '../../stores/settingsStore'
import { useSetupStore } from '../../stores/setupStore'
import { vscode } from '../../services/vscodeApi'
import { ProviderSetup } from '../setup/ProviderSetup'

interface SettingsPanelProps {
  onClose: () => void
  /** Open straight on the "add provider" form */
  startAdding?: boolean
}

/** Waits for one message of `type` from the extension, then stops listening. */
function once(type: string, callback: () => void): void {
  const onMessage = (event: MessageEvent) => {
    if (event.data?.type === type) {
      window.removeEventListener('message', onMessage)
      callback()
    }
  }
  window.addEventListener('message', onMessage)
}

export const SettingsPanel: React.FC<SettingsPanelProps> = ({ onClose, startAdding = false }) => {
  const showMetrics = useSettingsStore(state => state.showMetrics)
  const setShowMetrics = useSettingsStore(state => state.setShowMetrics)
  const selectedModel = useSettingsStore(state => state.selectedModel)
  const backendStatus = useSettingsStore(state => state.backendStatus)
  const providers = useSetupStore(state => state.providers)
  const external = useSetupStore(state => state.external)
  const backendUrl = useSetupStore(state => state.backendUrl)
  const problem = useSetupStore(state => state.problem)

  const [adding, setAdding] = useState(startAdding)
  const [removing, setRemoving] = useState<string | null>(null)
  const [restarting, setRestarting] = useState(false)

  const remove = (name: string) => {
    setRemoving(name)
    once('providerSaved', () => setRemoving(null))
    vscode.postMessage({ type: 'removeProvider', name })
  }

  const restart = () => {
    setRestarting(true)
    once('healthStatus', () => setRestarting(false))
    vscode.postMessage({ type: 'backendAction', action: 'restart' })
  }

  const backendLine = external
    ? `Your own backend at ${backendUrl ?? '?'} (aiChat.backendUrl)`
    : backendStatus === 'connected'
      ? `Started by the extension at ${backendUrl ?? '127.0.0.1'}`
      : backendStatus === 'setup'
        ? 'Starts once a model provider is added'
        : problem ?? 'Not running'

  return (
    <div className="absolute inset-0 z-50 flex items-center justify-center animate-fade-in">
      {/* Backdrop */}
      <div className="absolute inset-0 bg-black/30" onClick={onClose} />

      {/* Panel */}
      <div className="relative w-[min(420px,calc(100%-24px))] max-h-[88%] bg-bg-primary border border-border rounded-xl shadow-2xl
                      flex flex-col overflow-hidden">
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-border">
          <h3 className="text-[13px] font-medium text-text-primary">Settings</h3>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-bg-tertiary text-text-muted hover:text-text-primary transition-colors"
          >
            <X size={14} />
          </button>
        </div>

        {/* Content */}
        <div className="flex-1 overflow-y-auto p-4 space-y-5">
          {/* Model providers */}
          <div>
            <div className="flex items-center justify-between mb-1.5">
              <label className="flex items-center gap-1.5 text-[11px] font-medium text-text-secondary">
                <Plug size={12} />
                Model providers
              </label>
              {!adding && !external && (
                <button
                  onClick={() => setAdding(true)}
                  className="flex items-center gap-1 text-[11px] text-accent hover:underline"
                >
                  <Plus size={11} /> Add provider
                </button>
              )}
            </div>

            {external ? (
              <p className="text-[11px] text-text-muted leading-snug">
                Providers are configured in the backend you run yourself. Clear the aiChat.backendUrl setting
                to let the extension run its own.
              </p>
            ) : adding ? (
              <div className="rounded-lg border border-border p-3">
                <ProviderSetup onDone={() => setAdding(false)} />
                {providers.length > 0 && (
                  <button
                    onClick={() => setAdding(false)}
                    className="w-full mt-2 text-[11px] text-text-muted hover:text-text-primary"
                  >
                    Cancel
                  </button>
                )}
              </div>
            ) : providers.length === 0 ? (
              <p className="text-[11px] text-text-muted">None yet. Add one to start chatting.</p>
            ) : (
              <div className="rounded-lg border border-border divide-y divide-border">
                {providers.map((p) => (
                  <div key={p.name} className="flex items-center gap-2 px-2.5 py-1.5">
                    <div className="flex-1 min-w-0">
                      <div className="text-[12px] text-text-primary truncate">{p.label}</div>
                      <div className="text-[10px] text-text-muted truncate">
                        {p.models.length > 0 ? p.models.join(', ') : 'Models from the backend config'}
                      </div>
                    </div>
                    {p.removable ? (
                      <button
                        onClick={() => remove(p.name)}
                        disabled={removing !== null}
                        title={`Remove ${p.label} and its key`}
                        className="p-1 rounded text-text-muted hover:text-error hover:bg-error-subtle transition-colors"
                      >
                        {removing === p.name ? <Loader2 size={12} className="animate-spin" /> : <Trash2 size={12} />}
                      </button>
                    ) : (
                      <span className="text-[10px] text-text-muted" title="Set in the aiChat.openai settings">settings</span>
                    )}
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* Backend */}
          <div>
            <label className="flex items-center gap-1.5 text-[11px] font-medium text-text-secondary mb-1.5">
              <Server size={12} />
              Backend
            </label>
            <p className="text-[11px] text-text-muted leading-snug mb-2 break-all">{backendLine}</p>
            <div className="flex gap-1.5">
              {!external && (
                <button
                  onClick={restart}
                  disabled={restarting || backendStatus === 'setup'}
                  className="flex items-center gap-1 px-2.5 py-1 rounded-md text-[11px] bg-bg-tertiary
                             text-text-secondary hover:text-text-primary hover:bg-bg-active disabled:opacity-50"
                >
                  <RotateCw size={11} className={restarting ? 'animate-spin' : ''} /> Restart
                </button>
              )}
              <button
                onClick={() => vscode.postMessage({ type: 'backendAction', action: 'showLog' })}
                className="flex items-center gap-1 px-2.5 py-1 rounded-md text-[11px] bg-bg-tertiary
                           text-text-secondary hover:text-text-primary hover:bg-bg-active"
              >
                <FileText size={11} /> Show log
              </button>
            </div>
          </div>

          {/* Current Model (read-only info) */}
          <div>
            <label className="flex items-center gap-1.5 text-[11px] font-medium text-text-secondary mb-1.5">
              <Brain size={12} />
              Active Model
            </label>
            <div className="px-2.5 py-1.5 rounded-md text-[12px] bg-bg-secondary border border-border text-text-primary">
              {selectedModel || 'Default (set in header)'}
            </div>
            <p className="text-[10px] text-text-muted mt-1">
              Use the model selector in the header to change models per-request.
            </p>
          </div>

          {/* Show Metrics Toggle */}
          <div>
            <label className="flex items-center gap-1.5 text-[11px] font-medium text-text-secondary mb-1.5">
              <Eye size={12} />
              Show Metrics
            </label>
            <button
              onClick={() => setShowMetrics(!showMetrics)}
              className={`relative w-9 h-5 rounded-full transition-colors
                ${showMetrics ? 'bg-accent' : 'bg-bg-tertiary border border-border'}
              `}
            >
              <div
                className={`absolute top-0.5 w-4 h-4 rounded-full bg-white shadow transition-transform
                  ${showMetrics ? 'translate-x-4' : 'translate-x-0.5'}
                `}
              />
            </button>
            <p className="text-[10px] text-text-muted mt-1">
              Display token count, latency, and cost after each response.
            </p>
          </div>
        </div>

        {/* Footer */}
        <div className="px-4 py-3 border-t border-border">
          <p className="text-[10px] text-text-muted text-center">
            API keys are kept in VS Code's secret storage, never in settings files.
          </p>
        </div>
      </div>
    </div>
  )
}
