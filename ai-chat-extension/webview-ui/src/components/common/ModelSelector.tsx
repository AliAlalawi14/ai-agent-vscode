import React, { useState, useRef, useEffect } from 'react'
import { ChevronDown, Cpu } from 'lucide-react'
import { useSettingsStore } from '../../stores/settingsStore'

export const ModelSelector: React.FC = () => {
  const [open, setOpen] = useState(false)
  const dropdownRef = useRef<HTMLDivElement>(null)

  const selectedModel = useSettingsStore(state => state.selectedModel)
  const availableModels = useSettingsStore(state => state.availableModels)
  const setSelectedModel = useSettingsStore(state => state.setSelectedModel)

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
  const displayName = currentModel?.name || selectedModel || 'Select Model'

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
        </div>
      )}

      {open && availableModels.length === 0 && (
        <div className="absolute top-full left-0 mt-1 w-48 rounded-lg border border-border 
                        bg-bg-primary shadow-xl z-50 p-3 animate-fade-in">
          <p className="text-[11px] text-text-muted text-center">
            No models available. Check backend connection.
          </p>
        </div>
      )}
    </div>
  )
}
