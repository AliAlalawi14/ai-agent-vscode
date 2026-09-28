import React, { useState } from 'react'
import { Copy, Check, RefreshCw } from 'lucide-react'

interface MessageActionsProps {
  content: string
  onRegenerate?: () => void
  showRegenerate?: boolean
}

export const MessageActions: React.FC<MessageActionsProps> = ({
  content,
  onRegenerate,
  showRegenerate = false,
}) => {
  const [copied, setCopied] = useState(false)

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(content)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      // Fallback for environments without clipboard API
      const textarea = document.createElement('textarea')
      textarea.value = content
      document.body.appendChild(textarea)
      textarea.select()
      document.execCommand('copy')
      document.body.removeChild(textarea)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    }
  }

  return (
    <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
      <button
        onClick={handleCopy}
        className="p-1 rounded text-text-muted hover:text-text-primary 
                   hover:bg-bg-tertiary transition-colors"
        title={copied ? 'Copied!' : 'Copy message'}
      >
        {copied ? <Check size={12} className="text-success" /> : <Copy size={12} />}
      </button>
      {showRegenerate && onRegenerate && (
        <button
          onClick={onRegenerate}
          className="p-1 rounded text-text-muted hover:text-text-primary 
                     hover:bg-bg-tertiary transition-colors"
          title="Regenerate response"
        >
          <RefreshCw size={12} />
        </button>
      )}
    </div>
  )
}
