import React from 'react'
import { X, MessageSquare, Trash2 } from 'lucide-react'
import { useConversationStore } from '../../stores/conversationStore'
import { useChatStore } from '../../stores/chatStore'

export const HistorySidebar: React.FC = () => {
  const conversations = useConversationStore(state => state.conversations)
  const activeConversationId = useConversationStore(state => state.activeConversationId)
  const deleteConversation = useConversationStore(state => state.deleteConversation)
  const setShowHistory = useConversationStore(state => state.setShowHistory)

  const handleLoad = (id: string) => {
    const conv = conversations.find(c => c.id === id)
    if (!conv) return

    // Load messages into chat store
    useChatStore.setState({
      messages: conv.messages,
      isStreaming: false,
      error: null,
      activeTools: new Map(),
      toolHistory: [],
    })

    useConversationStore.setState({
      activeConversationId: id,
      showHistory: false,
    })
  }

  const formatDate = (timestamp: number) => {
    const now = Date.now()
    const diff = now - timestamp
    const minutes = Math.floor(diff / 60000)
    const hours = Math.floor(diff / 3600000)
    const days = Math.floor(diff / 86400000)

    if (minutes < 1) return 'Just now'
    if (minutes < 60) return `${minutes}m ago`
    if (hours < 24) return `${hours}h ago`
    if (days < 7) return `${days}d ago`
    return new Date(timestamp).toLocaleDateString()
  }

  return (
    <div className="absolute inset-0 z-40 flex animate-fade-in">
      {/* Sidebar */}
      <div className="w-72 h-full bg-bg-primary border-r border-border flex flex-col shadow-xl">
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-border">
          <h3 className="text-[13px] font-medium text-text-primary">Chat History</h3>
          <button
            onClick={() => setShowHistory(false)}
            className="p-1 rounded hover:bg-bg-tertiary text-text-muted hover:text-text-primary transition-colors"
          >
            <X size={14} />
          </button>
        </div>

        {/* List */}
        <div className="flex-1 overflow-y-auto">
          {conversations.length === 0 ? (
            <div className="px-4 py-8 text-center">
              <MessageSquare size={24} className="mx-auto text-text-muted mb-2 opacity-40" />
              <p className="text-[12px] text-text-muted">No conversations yet</p>
            </div>
          ) : (
            <div className="py-1">
              {conversations.map(conv => (
                <div
                  key={conv.id}
                  className={`group flex items-center gap-2 px-4 py-2.5 cursor-pointer 
                    transition-colors hover:bg-bg-tertiary
                    ${conv.id === activeConversationId ? 'bg-bg-secondary border-l-2 border-accent' : ''}
                  `}
                  onClick={() => handleLoad(conv.id)}
                >
                  <div className="flex-1 min-w-0">
                    <p className="text-[12px] text-text-primary truncate font-medium">
                      {conv.title}
                    </p>
                    <p className="text-[10px] text-text-muted mt-0.5">
                      {conv.messages.length} messages · {formatDate(conv.updatedAt)}
                    </p>
                  </div>
                  <button
                    onClick={(e) => { e.stopPropagation(); deleteConversation(conv.id) }}
                    className="shrink-0 p-1 rounded opacity-0 group-hover:opacity-100 
                               text-text-muted hover:text-error hover:bg-error-subtle transition-all"
                    title="Delete conversation"
                  >
                    <Trash2 size={12} />
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Backdrop */}
      <div
        className="flex-1 bg-black/20"
        onClick={() => setShowHistory(false)}
      />
    </div>
  )
}
