import React, { useState, useCallback } from 'react'
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter'
import { vscDarkPlus, vs } from 'react-syntax-highlighter/dist/esm/styles/prism'
import { Copy, Check } from 'lucide-react'

interface CodeBlockProps {
  code: string
  language?: string
  filename?: string
  onApply?: () => void
  isApplying?: boolean
}

export const CodeBlock: React.FC<CodeBlockProps> = ({
  code,
  language = 'typescript',
  filename,
}) => {
  const [copied, setCopied] = useState(false)

  const handleCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(code)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch (err) {
      console.error('Failed to copy:', err)
    }
  }, [code])

  const displayLanguage = language?.toLowerCase() || 'text'
  const displayName = filename || displayLanguage

  return (
    <div className="my-2 rounded-lg border border-border overflow-hidden bg-bg-secondary">
      {/* Slim header */}
      <div className="flex items-center justify-between px-3 py-1.5 bg-bg-tertiary border-b border-border">
        <span className="text-[11px] text-text-muted font-mono">{displayName}</span>
        <button
          onClick={handleCopy}
          className="flex items-center gap-1 px-1.5 py-0.5 rounded text-[11px] 
                     text-text-muted hover:text-text-secondary hover:bg-bg-hover transition-colors"
          title="Copy code"
        >
          {copied ? (
            <>
              <Check size={11} />
              <span>Copied</span>
            </>
          ) : (
            <>
              <Copy size={11} />
              <span>Copy</span>
            </>
          )}
        </button>
      </div>

      {/* Code content */}
      <div className="overflow-x-auto">
        <SyntaxHighlighter
          language={displayLanguage}
          style={vscDarkPlus}
          showLineNumbers={true}
          lineNumberStyle={{
            minWidth: '2em',
            paddingRight: '0.8em',
            color: '#4e4e4e',
            fontSize: '11px'
          }}
          customStyle={{
            margin: 0,
            padding: '12px',
            fontSize: '12px',
            lineHeight: '1.5',
            background: 'transparent',
          }}
        >
          {code.trim()}
        </SyntaxHighlighter>
      </div>
    </div>
  )
}

// Inline code component
export const InlineCode: React.FC<{ children: string }> = ({ children }) => {
  return (
    <code className="px-1.5 py-0.5 text-[0.9em] rounded bg-bg-tertiary border border-border text-text-primary font-mono">
      {children}
    </code>
  )
}
