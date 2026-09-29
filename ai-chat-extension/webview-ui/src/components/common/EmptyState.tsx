import React from 'react'
import {
  Sparkles, Bug, RefreshCw, TestTube, FileSearch,
  Wand2, Terminal, Command, ArrowUp, ArrowDown, CornerDownLeft
} from 'lucide-react'

interface SuggestionChipsProps {
  onSelect: (text: string) => void
}

interface Suggestion {
  icon: React.ComponentType<{ size?: number; className?: string }>
  label: string
  prompt: string
  shortcut?: string
}

const quickActions: Suggestion[] = [
  { icon: FileSearch, label: 'Explain code', prompt: 'Explain what this code does in detail', shortcut: 'E' },
  { icon: Bug, label: 'Find issues', prompt: 'Review this code for bugs, security issues, and edge cases', shortcut: 'B' },
  { icon: Sparkles, label: 'Improve code', prompt: 'Refactor this code to improve readability and performance', shortcut: 'I' },
  { icon: TestTube, label: 'Add tests', prompt: 'Write comprehensive unit tests for this code', shortcut: 'T' },
  { icon: Wand2, label: 'Add feature', prompt: 'Add a new feature: ', shortcut: 'F' },
  { icon: Terminal, label: 'Run command', prompt: 'Run a build and check for errors', shortcut: 'R' },
]

const examplePrompts = [
  "How does dependency injection work in this project?",
  "Find all the places where error handling is missing",
  "Create a new REST endpoint for user preferences",
  "What's the database schema and how do the models relate?",
]

export const EmptyState: React.FC<SuggestionChipsProps> = ({ onSelect }) => {
  return (
    <div className="flex flex-col items-center justify-center h-full px-4 py-8 animate-fade-in">
      {/* Hero */}
      <div className="text-center mb-8">
        <div className="inline-flex items-center justify-center w-12 h-12 rounded-2xl
                        bg-accent-subtle border border-accent/10 mb-4">
          <Sparkles size={22} className="text-accent" />
        </div>
        <h2 className="text-[16px] font-semibold text-text-primary mb-1">
          Stoat
        </h2>
        <p className="text-[12px] text-text-muted max-w-[280px] leading-relaxed">
          Ask me to explain, refactor, debug, or build anything in your codebase.
          I can read files, search code, and make changes.
        </p>
      </div>

      {/* Quick actions grid */}
      <div className="w-full max-w-[400px] mb-6">
        <p className="text-[10px] font-medium text-text-muted uppercase tracking-wider mb-2 px-1">
          Quick Actions
        </p>
        <div className="grid grid-cols-3 gap-1.5">
          {quickActions.map(({ icon: Icon, label, prompt }) => (
            <button
              key={label}
              onClick={() => onSelect(prompt)}
              className="flex flex-col items-center gap-1.5 p-3 rounded-xl
                         bg-bg-secondary border border-border
                         hover:bg-bg-tertiary hover:border-border-light
                         active:scale-[0.98] transition-all duration-150 group"
            >
              <div className="w-8 h-8 rounded-lg bg-bg-tertiary flex items-center justify-center
                              group-hover:bg-accent-subtle transition-colors">
                <Icon size={15} className="text-text-secondary group-hover:text-accent transition-colors" />
              </div>
              <span className="text-[11px] text-text-secondary group-hover:text-text-primary
                               transition-colors font-medium text-center leading-tight">
                {label}
              </span>
            </button>
          ))}
        </div>
      </div>

      {/* Example prompts */}
      <div className="w-full max-w-[400px] mb-6">
        <p className="text-[10px] font-medium text-text-muted uppercase tracking-wider mb-2 px-1">
          Try asking
        </p>
        <div className="flex flex-col gap-1">
          {examplePrompts.map((prompt, i) => (
            <button
              key={i}
              onClick={() => onSelect(prompt)}
              className="text-left px-3 py-2 rounded-lg text-[12px] text-text-secondary
                         bg-bg-secondary border border-border
                         hover:bg-bg-tertiary hover:text-text-primary hover:border-border-light
                         transition-all duration-150 truncate"
            >
              {prompt}
            </button>
          ))}
        </div>
      </div>

      {/* Keyboard shortcut hints */}
      <div className="flex items-center gap-4 text-[10px] text-text-muted">
        <span className="flex items-center gap-1">
          <span className="inline-flex items-center justify-center w-4 h-4 rounded
                           bg-bg-tertiary border border-border text-[9px] font-mono">⌘</span>
          <span className="inline-flex items-center justify-center w-4 h-4 rounded
                           bg-bg-tertiary border border-border text-[9px] font-mono">K</span>
          <span className="ml-0.5">Shortcuts</span>
        </span>
        <span className="flex items-center gap-1">
          <span className="inline-flex items-center justify-center px-1 h-4 rounded
                           bg-bg-tertiary border border-border text-[9px] font-mono">@</span>
          <span>Mention files</span>
        </span>
        <span className="flex items-center gap-1">
          <CornerDownLeft size={10} />
          <span>Submit</span>
        </span>
      </div>
    </div>
  )
}
