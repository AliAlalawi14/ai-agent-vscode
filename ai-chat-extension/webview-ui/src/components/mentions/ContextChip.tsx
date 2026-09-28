import React from 'react'
import { FileCode, Box, Braces, Hash, X } from 'lucide-react'
import type { MentionItem } from '../../stores/mentionStore'

interface ContextChipProps {
  item: MentionItem
  onRemove: () => void
}

const symbolIcons: Record<string, React.ComponentType<{ size?: number; className?: string }>> = {
  class: Box,
  method: Braces,
  property: Hash,
}

export const ContextChip: React.FC<ContextChipProps> = ({ item, onRemove }) => {
  const isFile = item.kind === 'file'
  const Icon = isFile
    ? FileCode
    : symbolIcons[item.symbolType || ''] || Hash

  const displayName = item.kind === 'symbol' && item.className
    ? `${item.className}.${item.name}`
    : item.name

  return (
    <span className="inline-flex items-center gap-1 pl-1.5 pr-1 py-0.5 rounded-md
                     bg-accent-subtle border border-accent/20 text-[11px] text-accent 
                     animate-fade-in group">
      <Icon size={11} className="shrink-0" />
      <span className="font-medium max-w-[120px] truncate">{displayName}</span>
      <button
        onClick={onRemove}
        className="shrink-0 p-0.5 rounded hover:bg-accent/20 transition-colors 
                   opacity-60 group-hover:opacity-100"
        title="Remove"
      >
        <X size={10} />
      </button>
    </span>
  )
}
