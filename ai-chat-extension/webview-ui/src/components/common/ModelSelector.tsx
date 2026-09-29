import React, { useState, useRef, useEffect } from 'react'
import { ChevronDown, Cpu, Plus } from 'lucide-react'
import { useSettingsStore } from '../../stores/settingsStore'
import { openProviderSettings } from '../../stores/setupStore'

export const ModelSelector: React.FC = () => {
  const [open, setOpen] = useState(false)
  const dropdownRef = useRef<HTMLDivElement>(null)

  const selectedModel = useSettingsStore(state => state.selectedModel)
  const availableModels = useSettingsStore(state => state.availableModels)
  const setSelectedModel = useSettingsStore(state => state.setSelectedModel)
  const backendStatus = useSettingsStore(state => state.backendStatus)

  const addProvider = () => {
    setOpen(false)
    // Backend down: open Settings on its status (restart, log) rather than the form
    openProviderSettings(backendStatus === 'setup' || backendStatus === 'connected')
  }

  // Close dropdown on outside click
  useEffect(() => {
    const handleClickOutside = (e: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    if (open) document.addEventListener('mousedown', handleClickOutside)
    return () => document.removeEventListener('mousedown', handleClickOutside)
  }, [open])

  const currentModel = availableModels.find(m => m.id === selectedModel)
  const displayName =
    currentModel?.name || selectedModel ||
    (backendStatus === 'setup' ? 'Add a model' : backendStatus === 'starting' ? 'Loading models…' : 'Select Model')

  return (
    <div className="relative" ref={dropdownRef}>
      <button
        onClick={() => setOpen(!open)}
        className="flex items-center gap-1.5 px-2 py-1 rounded-md text-[11px] 
                   text-text-secondary hover:text-text-primary hover:bg-bg-tertiary 
                   transition-colors border border-transparent hover:border-border"
      >
        <Cpu size={12} className="text-text-muted" />
        <span className="max-w-[120px] truncate">{displayName}</span>
        <ChevronDown size={11} className={`text-text-muted transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>

      {open && availableModels.length > 0 && (
        <div className="absolute top-full left-0 mt-1 w-56 rounded-lg border border-border 
                        bg-bg-primary shadow-xl z-50 py-1 animate-fade-in">
          {availableModels.map(model => (
            <button
              key={model.id}
              onClick={() => { setSelectedModel(model.id); setOpen(false) }}
              className={`w-full text-left px-3 py-2 text-[12px] flex items-center justify-between
                hover:bg-bg-tertiary transition-colors
                ${model.id === selectedModel ? 'text-accent font-medium' : 'text-text-secondary'}
              `}
            >
              <div className="flex flex-col">
                <span>{model.name}</span>
                <span className="text-[10px] text-text-muted">{model.provider}</span>
              </div>
              {model.id === selectedModel && (
                <div className="w-1.5 h-1.5 rounded-full bg-accent" />
              )}
            </button>
          ))}
          <div className="border-t border-border mt-1 pt-1">
            <button
              onClick={addProvider}
              className="w-full text-left px-3 py-1.5 text-[11px] flex items-center gap-1.5 text-text-muted
                         hover:text-text-primary hover:bg-bg-tertiary transition-colors"
            >
              <Plus size={11} /> Add provider…
            </button>
          </div>
        </div>
      )}

      {open && availableModels.length === 0 && (
        <div className="absolute top-full left-0 mt-1 w-56 rounded-lg border border-border 
                        bg-bg-primary shadow-xl z-50 p-3 animate-fade-in">
          <p className="text-[11px] text-text-muted text-center mb-2">
            {backendStatus === 'setup'
              ? 'No model provider yet.'
              : backendStatus === 'starting'
                ? 'The agent is starting; models appear in a moment.'
              : backendStatus === 'connected'
                ? 'The backend has no models configured.'
                : 'The backend is not running.'}
          </p>
          <button
            onClick={addProvider}
            className="w-full flex items-center justify-center gap-1.5 px-2.5 py-1.5 rounded-md text-[11px]
                       font-medium bg-accent text-white hover:bg-accent-hover"
          >
            <Plus size={11} /> {backendStatus === 'setup' ? 'Add a provider' : 'Open settings'}
          </button>
        </div>
      )}
    </div>
  )
}
