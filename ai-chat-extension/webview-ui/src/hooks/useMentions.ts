import { useState, useCallback, useRef, useEffect } from 'react'
import { useMentionStore, type MentionItem } from '../stores/mentionStore'
import { vscode } from '../services/vscodeApi'

interface UseMentionsOptions {
  textareaRef: React.RefObject<HTMLTextAreaElement | null>
  value: string
  setValue: (val: string) => void
}

export function useMentions({ textareaRef, value, setValue }: UseMentionsOptions) {
  const [showDropdown, setShowDropdown] = useState(false)
  const [activeIndex, setActiveIndex] = useState(0)
  const [mentionStart, setMentionStart] = useState<number>(-1)
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const {
    query,
    isSearching,
    symbolResults,
    fileResults,
    openFiles,
    recentFiles,
    setQuery,
    setSearching,
    setOpenFiles,
    setRecentFiles,
    addContext,
    clearResults,
  } = useMentionStore()

  // Combined results for dropdown - prioritize open/recent files when no search query
  const results: MentionItem[] = [
    ...fileResults.map(f => ({
      id: `file:${f}`,
      kind: 'file' as const,
      name: f.split(/[/\\]/).pop() || f,
      filePath: f,
    })),
    ...symbolResults.map(s => ({
      id: `symbol:${s.className}.${s.name}`,
      kind: 'symbol' as const,
      name: s.name,
      filePath: s.filePath,
      className: s.className,
      symbolType: s.type,
    })),
  ]

  // Get prioritized results for empty @ mention (open files, then recent)
  const prioritizedResults: MentionItem[] = [
    ...openFiles.map(f => ({
      id: `open:${f}`,
      kind: 'file' as const,
      name: f.split(/[/\\]/).pop() || f,
      filePath: f,
      isOpen: true,
    })),
    ...recentFiles.filter(f => !openFiles.includes(f)).map(f => ({
      id: `recent:${f}`,
      kind: 'file' as const,
      name: f.split(/[/\\]/).pop() || f,
      filePath: f,
      isRecent: true,
    })),
  ]

  // Detect @ in text and extract query
  const detectMention = useCallback((text: string, cursorPos: number) => {
    // Walk backward from cursor to find @
    let i = cursorPos - 1
    while (i >= 0 && text[i] !== '@' && text[i] !== ' ' && text[i] !== '\n') {
      i--
    }

    if (i >= 0 && text[i] === '@') {
      // Found @, check it's at start or preceded by whitespace
      if (i === 0 || text[i - 1] === ' ' || text[i - 1] === '\n') {
        const mentionQuery = text.slice(i + 1, cursorPos)
        return { start: i, query: mentionQuery }
      }
    }

    return null
  }, [])

  // Trigger search with debounce
  const triggerSearch = useCallback((searchQuery: string) => {
    if (debounceRef.current) clearTimeout(debounceRef.current)

    setQuery(searchQuery)
    setSearching(true)
    setActiveIndex(0)

    debounceRef.current = setTimeout(() => {
      // Search both files and symbols
      vscode.postMessage({ type: 'searchFiles', query: searchQuery })
      vscode.postMessage({ type: 'searchSymbols', query: searchQuery })
    }, 200) // 200ms debounce
  }, [setQuery, setSearching])

  // Handle text changes — detect @ mentions
  const handleMentionDetection = useCallback((text: string) => {
    const textarea = textareaRef.current
    if (!textarea) return

    const cursorPos = textarea.selectionStart
    const mention = detectMention(text, cursorPos)

    if (mention) {
      setMentionStart(mention.start)
      setShowDropdown(true)
      triggerSearch(mention.query)
    } else {
      setShowDropdown(false)
      clearResults()
      setMentionStart(-1)
    }
  }, [textareaRef, detectMention, triggerSearch, clearResults])

  // Select a result from the dropdown
  const selectResult = useCallback((item: MentionItem) => {
    addContext(item)

    // Replace @query with just @ + name, or remove the @mention entirely
    if (mentionStart >= 0) {
      const textarea = textareaRef.current
      const cursorPos = textarea?.selectionStart || value.length
      const before = value.slice(0, mentionStart)
      const after = value.slice(cursorPos)
      setValue(before + after)

      // Focus textarea after state update
      setTimeout(() => {
        if (textarea) {
          const newPos = before.length
          textarea.focus()
          textarea.setSelectionRange(newPos, newPos)
        }
      }, 0)
    }

    setShowDropdown(false)
    clearResults()
    setMentionStart(-1)
  }, [addContext, mentionStart, textareaRef, value, setValue, clearResults])

  // Keyboard navigation for dropdown
  const handleMentionKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (!showDropdown || results.length === 0) return false

    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault()
        setActiveIndex(i => (i + 1) % results.length)
        return true
      case 'ArrowUp':
        e.preventDefault()
        setActiveIndex(i => (i - 1 + results.length) % results.length)
        return true
      case 'Enter':
      case 'Tab':
        e.preventDefault()
        selectResult(results[activeIndex])
        return true
      case 'Escape':
        e.preventDefault()
        setShowDropdown(false)
        clearResults()
        setMentionStart(-1)
        return true
    }

    return false
  }, [showDropdown, results, activeIndex, selectResult, clearResults])

  // Open dropdown manually (@ button click)
  const openMentionPicker = useCallback(() => {
    const textarea = textareaRef.current
    if (!textarea) return

    // Insert @ at cursor position
    const cursorPos = textarea.selectionStart
    const before = value.slice(0, cursorPos)
    const after = value.slice(cursorPos)
    const newVal = before + '@' + after
    setValue(newVal)

    setTimeout(() => {
      textarea.focus()
      const newPos = cursorPos + 1
      textarea.setSelectionRange(newPos, newPos)
      // Trigger mention detection
      handleMentionDetection(newVal)
    }, 0)
  }, [textareaRef, value, setValue, handleMentionDetection])

  // Listen for search results from extension
  useEffect(() => {
    const handler = (event: MessageEvent) => {
      const msg = event.data
      if (msg.type === 'symbolResults') {
        useMentionStore.getState().setSymbolResults(msg.query, msg.symbols)
      } else if (msg.type === 'fileResults') {
        useMentionStore.getState().setFileResults(msg.query, msg.files)
      }
    }
    window.addEventListener('message', handler)
    return () => window.removeEventListener('message', handler)
  }, [])

  // Cleanup debounce on unmount
  useEffect(() => {
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current)
    }
  }, [])

  return {
    showDropdown,
    results,
    prioritizedResults,
    activeIndex,
    isSearching,
    query,
    handleMentionDetection,
    handleMentionKeyDown,
    selectResult,
    openMentionPicker,
    closeDropdown: () => {
      setShowDropdown(false)
      clearResults()
      setMentionStart(-1)
    },
  }
}
