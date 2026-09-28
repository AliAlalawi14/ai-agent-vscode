import React, { useState } from 'react'
import { X, Server, Brain, Eye } from 'lucide-react'
import { useSettingsStore } from '../../stores/settingsStore'
import { vscode } from '../../services/vscodeApi'

interface SettingsPanelProps {
  onClose: () => void
}

export const SettingsPanel: React.FC<SettingsPanelProps> = ({ onClose }) => {
  const backendUrl = useSettingsStore(state => state.backendUrl)
  const setBackendUrl = useSettingsStore(state => state.setBackendUrl)
  const showMetrics = useSettingsStore(state => state.showMetrics)
  const setShowMetrics = useSettingsStore(state => state.setShowMetrics)
  const selectedModel = useSettingsStore(state => state.selectedModel)

  const [urlInput, setUrlInput] = useState(backendUrl)

  const handleSaveUrl = () => {
    setBackendUrl(urlInput)
    vscode.postMessage({ type: 'updateSettings', settings: { backendUrl: urlInput } })
  }

  return (
    <div className="absolute inset-0 z-50 flex items-center justify-center animate-fade-in">
      {/* Backdrop */}
      <div className="absolute inset-0 bg-black/30" onClick={onClose} />

      {/* Panel */}
      <div className="relative w-80 max-h-[80%] bg-bg-primary border border-border rounded-xl shadow-2xl 
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
          {/* Backend URL */}
          <div>
            <label className="flex items-center gap-1.5 text-[11px] font-medium text-text-secondary mb-1.5">
              <Server size={12} />
              Backend URL
            </label>
            <div className="flex gap-1.5">
              <input
                type="text"
                value={urlInput}
                onChange={(e) => setUrlInput(e.target.value)}
                className="flex-1 px-2.5 py-1.5 rounded-md text-[12px] bg-bg-secondary border border-border
                           text-text-primary placeholder:text-text-muted focus:outline-none focus:border-accent"
                placeholder="http://localhost:5036"
              />
              <button
                onClick={handleSaveUrl}
                disabled={urlInput === backendUrl}
                className={`px-2.5 py-1.5 rounded-md text-[11px] font-medium transition-colors
                  ${urlInput !== backendUrl
                    ? 'bg-accent text-white hover:bg-accent-hover'
                    : 'bg-bg-tertiary text-text-muted'
                  }
                `}
              >
                Save
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
            Settings are synced with VS Code workspace configuration.
          </p>
        </div>
      </div>
    </div>
  )
}
