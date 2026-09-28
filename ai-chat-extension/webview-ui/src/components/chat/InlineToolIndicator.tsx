import React from 'react'
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
  Wrench
} from 'lucide-react'
import type { ToolExecution } from '../../stores/chatStore'

interface InlineToolIndicatorProps {
  tool: ToolExecution
}

const toolIcons: Record<string, React.ComponentType<{ size?: number }>> = {
  read_file: FileSearch,
  read_files: FileSearch,
  write_file: FileEdit,
  replace_lines: Edit3,
  search_code: Search,
  semantic_search: Brain,
  list_directory: FolderTree,
  run_terminal: Terminal
}

export const InlineToolIndicator: React.FC<InlineToolIndicatorProps> = ({ tool }) => {
  const ToolIcon = toolIcons[tool.tool] || Wrench

  // Format the description based on tool args
  const getDescription = () => {
    const { args } = tool
    if (args.path) {
      const path = String(args.path).split('/').pop() || String(args.path)
      return path
    }
    if (args.paths && Array.isArray(args.paths)) {
      return `${args.paths.length} files`
    }
    if (args.command) {
      const cmd = String(args.command).slice(0, 30)
      return cmd.length > 30 ? cmd + '...' : cmd
    }
    if (args.query) {
      const query = String(args.query).slice(0, 30)
      return query.length > 30 ? query + '...' : query
    }
    return tool.tool.replace(/_/g, ' ')
  }

  return (
    <div className={`inline-tool ${tool.status}`}>
      <div className="inline-tool-icon">
        {tool.status === 'running' ? (
          <Loader2 size={12} className="spin" />
        ) : tool.status === 'awaiting' ? (
          <PauseCircle size={12} />
        ) : tool.status === 'completed' ? (
          <CheckCircle2 size={12} />
        ) : tool.status === 'error' ? (
          <XCircle size={12} />
        ) : (
          <ToolIcon size={12} />
        )}
      </div>
      <span className="inline-tool-action">{tool.tool.replace(/_/g, ' ')}</span>
      <span className="inline-tool-target">{getDescription()}</span>
    </div>
  )
}
