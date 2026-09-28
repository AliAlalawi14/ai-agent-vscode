import React, { useState } from 'react'
import { 
  FileSearch, 
  FileEdit, 
  Edit3, 
  Search, 
  Brain,
  FolderTree,
  Terminal,
  Loader2,
  CheckCircle2,
  XCircle,
  PauseCircle,
  Wrench,
  ChevronDown,
  ChevronRight
} from 'lucide-react'
import type { ToolExecution } from '../../stores/chatStore'

interface ToolExecutionCardProps {
  tool: ToolExecution
}

const toolIcons: Record<string, React.ComponentType<{ size?: number; className?: string }>> = {
  read_file: FileSearch,
  read_files: FileSearch,
  write_file: FileEdit,
  replace_lines: Edit3,
  edit_file: Edit3,
  find_files: FileSearch,
  search_code: Search,
  semantic_search: Brain,
  list_directory: FolderTree,
  run_terminal: Terminal
}

const toolLabels: Record<string, string> = {
  edit_file: 'Editing',
  find_files: 'Finding',
  read_file: 'Reading',
  read_files: 'Reading',
  write_file: 'Writing',
  replace_lines: 'Editing',
  search_code: 'Searching',
  semantic_search: 'Searching',
  list_directory: 'Listing',
  run_terminal: 'Running'
}

// Finished rows read in the past tense ("Ran git status"), failed ones say "Failed"
const doneLabels: Record<string, string> = {
  edit_file: 'Edited',
  find_files: 'Found',
  read_file: 'Read',
  read_files: 'Read',
  write_file: 'Wrote',
  replace_lines: 'Edited',
  search_code: 'Searched',
  semantic_search: 'Searched',
  list_directory: 'Listed',
  run_terminal: 'Ran'
}

export const ToolExecutionCard: React.FC<ToolExecutionCardProps> = ({ tool }) => {
  const [expanded, setExpanded] = useState(false)
  const ToolIcon = toolIcons[tool.tool] || Wrench
  const isRunning = tool.status === 'running'
  const isAwaiting = tool.status === 'awaiting'   // paused until the user approves the card below
  const isCompleted = tool.status === 'completed'
  const isError = tool.status === 'error'

  const getTarget = () => {
    const { args } = tool
    if (args.path) {
      return String(args.path).split(/[/\\]/).pop() || String(args.path)
    }
    if (args.paths && Array.isArray(args.paths)) {
      return `${args.paths.length} files`
    }
    if (args.command) {
      const cmd = String(args.command)
      return cmd.length > 40 ? cmd.slice(0, 40) + '...' : cmd
    }
    if (args.pattern) {
      return String(args.pattern)
    }
    if (args.query) {
      const q = String(args.query)
      return q.length > 40 ? q.slice(0, 40) + '...' : q
    }
    return ''
  }

  const label = isError
    ? 'Failed'
    : isCompleted
      ? doneLabels[tool.tool] || tool.tool.replace(/_/g, ' ')
      : toolLabels[tool.tool] || tool.tool.replace(/_/g, ' ')
  const target = getTarget()

  return (
    <div className="animate-slide-up">
      <button
        onClick={() => tool.result && setExpanded(!expanded)}
        className={`w-full flex items-center gap-2 px-2.5 py-1.5 rounded-md text-[12px]
          transition-all text-left group
          ${isRunning ? 'bg-accent-subtle border border-accent/20' : ''}
          ${isAwaiting ? 'bg-warning/10 border border-warning/30' : ''}
          ${isCompleted ? 'bg-bg-secondary hover:bg-bg-tertiary' : ''}
          ${isError ? 'bg-error-subtle border border-error/20' : ''}
          ${!isRunning && !isAwaiting && !isError ? 'border border-transparent' : ''}
        `}
      >
        {/* Status icon */}
        <div className="shrink-0">
          {isRunning ? (
            <Loader2 size={13} className="text-accent animate-spin" />
          ) : isAwaiting ? (
            <PauseCircle size={13} className="text-warning" />
          ) : isError ? (
            <XCircle size={13} className="text-error" />
          ) : (
            <CheckCircle2 size={13} className="text-success" />
          )}
        </div>

        {/* Label + target */}
        <div className="flex items-center gap-1.5 min-w-0 flex-1">
          <ToolIcon size={12} className="text-text-muted shrink-0" />
          <span className="text-text-secondary font-medium">{label}</span>
          {target && (
            <span className="text-text-muted truncate font-mono text-[11px]">{target}</span>
          )}
          {isAwaiting && (
            <span className="text-warning text-[11px] shrink-0">· waiting for your approval</span>
          )}
        </div>

        {/* Expand indicator */}
        {tool.result && !isRunning && (
          <div className="shrink-0 text-text-muted opacity-0 group-hover:opacity-100 transition-opacity">
            {expanded ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
          </div>
        )}
      </button>

      {/* Expanded result preview */}
      {expanded && tool.result && (
        <div className="ml-5 mt-1 px-3 py-2 rounded bg-bg-secondary border border-border 
                        text-[11px] font-mono text-text-secondary max-h-[120px] overflow-y-auto
                        animate-fade-in">
          <pre className="whitespace-pre-wrap break-words">{tool.result}</pre>
        </div>
      )}
    </div>
  )
}
