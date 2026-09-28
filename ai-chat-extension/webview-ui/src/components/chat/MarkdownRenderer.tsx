import React from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { CodeBlock, InlineCode } from './CodeBlock'
import { MermaidDiagram } from './MermaidDiagram'

interface MarkdownRendererProps {
  content: string
}

export const MarkdownRenderer: React.FC<MarkdownRendererProps> = ({ content }) => {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      components={{
        code({ node, inline, className, children, ...props }: any) {
          const match = /language-(\w+)/.exec(className || '')
          const language = match ? match[1] : 'text'
          const code = String(children).replace(/\n$/, '')

          if (!inline && language === 'mermaid') {
            return <MermaidDiagram code={code} />
          }
          if (!inline && code.includes('\n')) {
            return <CodeBlock code={code} language={language} />
          }
          return <InlineCode>{code}</InlineCode>
        },

        p({ children }) {
          return <p className="mb-2.5 last:mb-0 leading-relaxed">{children}</p>
        },

        h1({ children }) {
          return <h1 className="text-[16px] font-semibold mt-5 mb-2 text-text-primary">{children}</h1>
        },
        h2({ children }) {
          return <h2 className="text-[15px] font-semibold mt-4 mb-1.5 text-text-primary">{children}</h2>
        },
        h3({ children }) {
          return <h3 className="text-[14px] font-semibold mt-3 mb-1 text-text-primary">{children}</h3>
        },

        ul({ children }) {
          return <ul className="my-2 pl-4 list-disc">{children}</ul>
        },
        ol({ children }) {
          return <ol className="my-2 pl-4 list-decimal">{children}</ol>
        },
        li({ children }) {
          return <li className="my-0.5 leading-relaxed">{children}</li>
        },

        a({ href, children }) {
          return (
            <a
              href={href}
              className="text-accent hover:underline hover:opacity-80 transition-opacity"
              target="_blank"
              rel="noopener noreferrer"
            >
              {children}
            </a>
          )
        },

        blockquote({ children }) {
          return (
            <blockquote className="my-2 pl-3 border-l-2 border-accent bg-bg-tertiary rounded-r py-2 pr-3 text-text-secondary">
              {children}
            </blockquote>
          )
        },

        table({ children }) {
          return <table className="w-full my-2 border-collapse text-[12px]">{children}</table>
        },
        thead({ children }) {
          return <thead className="bg-bg-tertiary">{children}</thead>
        },
        tbody({ children }) {
          return <tbody>{children}</tbody>
        },
        tr({ children }) {
          return <tr className="hover:bg-bg-hover">{children}</tr>
        },
        th({ children }) {
          return <th className="px-2 py-1.5 text-left font-semibold border-b-2 border-border">{children}</th>
        },
        td({ children }) {
          return <td className="px-2 py-1.5 border-b border-border">{children}</td>
        },

        hr() {
          return <hr className="my-4 border-none border-t border-border" />
        },

        strong({ children }) {
          return <strong className="font-semibold text-text-primary">{children}</strong>
        },
        em({ children }) {
          return <em className="italic text-text-secondary">{children}</em>
        },
        del({ children }) {
          return <del className="line-through opacity-60">{children}</del>
        }
      }}
    >
      {content}
    </ReactMarkdown>
  )
}
