import React, { useRef, useEffect } from 'react'
import { FileCode, Box, Braces, Hash, Loader2, FolderOpen, History } from 'lucide-react'
import type { MentionItem } from '../../stores/mentionStore'

interface MentionDropdownProps {
  results: MentionItem[]
  prioritizedResults: MentionItem[]
  activeIndex: number
  isSearching: boolean
  query: string
  onSelect: (item: MentionItem) => void
}

const symbolIcons: Record<string, React.ComponentType<{ size?: number; className?: string }>> = {
  class: Box,
  method: Braces,
  property: Hash,
}

export const MentionDropdown: React.FC<MentionDropdownProps> = ({
  results,
  prioritizedResults,
  activeIndex,
  isSearching,
  query,
  onSelect,
}) => {
  const listRef = useRef<HTMLDivElement>(null)

  // Use prioritized results when no query, otherwise use search results
  const displayResults = query.length > 0 ? results : prioritizedResults
  const hasOpenFiles = prioritizedResults.some(r => (r as any).isOpen)
  const hasRecentFiles = prioritizedResults.some(r => (r as any).isRecent)

  // Scroll active item into view
  useEffect(() => {
    const container = listRef.current
    if (!container) return
    const activeEl = container.querySelector(`[data-index="${activeIndex}"]`) as HTMLElement
    if (activeEl) {
      activeEl.scrollIntoView({ block: 'nearest' })
    }
  }, [activeIndex])

  return (
    <div className="absolute bottom-full left-0 right-0 mb-1 z-50 animate-slide-up">
      <div className="bg-bg-secondary border border-border-light rounded-lg shadow-lg 
                      max-h-[280px] overflow-y-auto" ref={listRef}>
        {/* Header */}
        <div className="px-3 py-1.5 border-b border-border text-[10px] text-text-muted uppercase tracking-wide flex items-center gap-1.5">
          {isSearching ? (
            <>
              <Loader2 size={10} className="animate-spin" />
              <span>Searching...</span>
            </>
          ) : query.length > 0 ? (
            <span>
              {results.length > 0
                ? `${results.length} result${results.length !== 1 ? 's' : ''}`
                : 'No results'
              }
            </span>
          ) : (
            <span>Suggested files</span>
          )}
        </div>

        {/* Section: Open Files */}
        {query.length === 0 && hasOpenFiles && (
          <div className="border-b border-border/50">
            <div className="px-3 py-1 bg-bg-tertiary/50 text-[9px] text-text-muted uppercase tracking-wide flex items-center gap-1">
              <FolderOpen size={9} />
              <span>Open Files</span>
            </div>
            {renderItems(prioritizedResults.filter(r => (r as any).isOpen), 0, onSelect)}
          </div>
        )}

        {/* Section: Recent Files */}
        {query.length === 0 && hasRecentFiles && (
          <div className={hasOpenFiles ? 'border-b border-border/50' : ''}>
            <div className="px-3 py-1 bg-bg-tertiary/50 text-[9px] text-text-muted uppercase tracking-wide flex items-center gap-1">
              <History size={9} />
              <span>Recent Files</span>
            </div>
            {renderItems(
              prioritizedResults.filter(r => (r as any).isRecent),
              prioritizedResults.filter(r => (r as any).isOpen).length,
              onSelect
            )}
          </div>
        )}

        {/* Search Results */}
        {query.length > 0 && renderItems(results, 0, onSelect)}

        {/* Empty state when query but no results and not searching */}
        {displayResults.length === 0 && query.length > 0 && !isSearching && (
          <div className="px-3 py-3 text-center text-[11px] text-text-muted">
            No files or symbols matching "{query}"
          </div>
        )}

        {/* Empty state when no query and no files */}
        {displayResults.length === 0 && query.length === 0 && !isSearching && (
          <div className="px-3 py-3 text-center text-[11px] text-text-muted">
            Type to search files & symbols
          </div>
        )}
      </div>
    </div>
  )
}

function renderItems(items: MentionItem[], startIndex: number, onSelect: (item: MentionItem) => void) {
  return items.map((item, idx) => {
    const index = startIndex + idx
    const fileName = item.filePath.split(/[/\\]/).pop() || item.filePath

    return (
      <button
        key={item.id}
        data-index={index}
        onClick={() => onSelect(item)}
        className="w-full flex items-center gap-2 px-3 py-1.5 text-left transition-colors hover:bg-bg-hover"
      >
        {/* Icon */}
        {item.kind === 'file' ? (
          <FileCode size={13} className="text-accent shrink-0" />
        ) : (
          <Hash size={13} className="text-warning shrink-0" />
        )}

        {/* Name + details */}
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-1.5">
            <span className="text-[12px] font-medium text-text-primary truncate">
              {item.name}
            </span>
          </div>
          <div className="text-[10px] text-text-muted truncate">
            {item.filePath}
          </div>
        </div>
      </button>
    )
  })
}
