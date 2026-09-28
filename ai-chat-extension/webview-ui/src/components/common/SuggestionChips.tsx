import React from 'react'
import { Sparkles, Bug, RefreshCw, TestTube } from 'lucide-react'

interface SuggestionChipsProps {
  onSelect: (text: string) => void
}

const suggestions = [
  { icon: Sparkles, label: 'Explain this file', prompt: 'Explain this file and its purpose' },
  { icon: Bug, label: 'Find bugs', prompt: 'Review this code for bugs and potential issues' },
  { icon: RefreshCw, label: 'Refactor', prompt: 'Refactor this code to improve readability' },
  { icon: TestTube, label: 'Write tests', prompt: 'Write unit tests for this code' },
]

export const SuggestionChips: React.FC<SuggestionChipsProps> = ({ onSelect }) => {
  return (
    <div className="flex flex-col items-center justify-center h-full px-6 animate-fade-in">
      <div className="text-center mb-8">
        <h2 className="text-[15px] font-medium text-text-primary mb-1">
          How can I help?
        </h2>
        <p className="text-[12px] text-text-muted">
          Ask me anything about your code
        </p>
      </div>

      <div className="grid grid-cols-2 gap-2 w-full max-w-[320px]">
        {suggestions.map(({ icon: Icon, label, prompt }) => (
          <button
            key={label}
            onClick={() => onSelect(prompt)}
            className="flex items-center gap-2 px-3 py-2.5 rounded-lg
                       bg-bg-secondary border border-border text-left
                       hover:bg-bg-tertiary hover:border-border-light 
                       transition-all group"
          >
            <Icon size={14} className="text-text-muted group-hover:text-accent transition-colors shrink-0" />
            <span className="text-[12px] text-text-secondary group-hover:text-text-primary transition-colors">
              {label}
            </span>
          </button>
        ))}
      </div>
    </div>
  )
}
