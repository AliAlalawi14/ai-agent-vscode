import React, { useEffect, useState } from 'react'
import { ChatContainer } from './components/chat/ChatContainer'
import { ErrorBoundary } from './components/common/ErrorBoundary'
import { vscode } from './services/vscodeApi'
import { useChatStore } from './stores/chatStore'
import { useConversationStore } from './stores/conversationStore'
import { useChangeStore } from './stores/changeStore'
import { usePlanStore } from './stores/planStore'
import { useMentionStore } from './stores/mentionStore'
import { useSetupStore } from './stores/setupStore'
import { useAutoSave } from './hooks/useAgentStream'

const App: React.FC = () => {
  // Enable auto-save
  useAutoSave()
  const [theme, setTheme] = useState<'light' | 'dark'>('dark')
  const [isRestored, setIsRestored] = useState(false)

  useEffect(() => {
    const handleMessage = (event: MessageEvent) => {
      const message = event.data

      if (message.type === 'init') {
        setTheme(message.theme)
      }

      if (message.type === 'restoreState') {
        // Hydrate stores with saved state
        const { conversations, activeConversationId, currentSession } = message

        // Restore conversations
        useConversationStore.getState().hydrateConversations(conversations || [], activeConversationId || null)

        // Restore current session if exists
        if (currentSession) {
          useChatStore.getState().hydrateState(
            currentSession.messages || [],
            currentSession.activeTools || [],
            currentSession.toolHistory || []
          )
          useChangeStore.getState().hydrateChanges(currentSession.changes || [])
          if (currentSession.plan) usePlanStore.getState().setPlan(currentSession.plan)
        }

        setIsRestored(true)
      }

      if (message.type === 'openFilesUpdate') {
        useMentionStore.getState().setOpenFiles(message.files || [])
      }

      if (message.type === 'setupState') {
        useSetupStore.getState().setSetup(message.setup)
      }

      if (message.type === 'recentFilesUpdate') {
        useMentionStore.getState().setRecentFiles(message.files || [])
      }
    }

    window.addEventListener('message', handleMessage)

    // Request init and restoration
    vscode.postMessage({ type: 'init' })
    vscode.postMessage({ type: 'loadConversations' })
    vscode.postMessage({ type: 'getSetup' })

    // Request open and recent files for @-mention dropdown
    vscode.postMessage({ type: 'getOpenFiles' })
    vscode.postMessage({ type: 'getRecentFiles' })

    return () => window.removeEventListener('message', handleMessage)
  }, [])

  return (
    <ErrorBoundary>
      <div className={`h-full flex flex-col bg-bg-primary text-text-primary ${theme}`}>
        <ChatContainer />
      </div>
    </ErrorBoundary>
  )
}

export default App
