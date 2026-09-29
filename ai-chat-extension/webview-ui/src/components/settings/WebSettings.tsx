import React, { useEffect, useState } from 'react'
import { ExternalLink, Globe, KeyRound } from 'lucide-react'
import { vscode } from '../../services/vscodeApi'

type Fetch = 'ask' | 'allow' | 'off'
type Provider = '' | 'brave' | 'tavily' | 'searxng'

interface WebState {
  fetch: Fetch
  searchProvider: Provider
  hasSearchKey: boolean
  searxngUrl: string
}

const keyPages: Record<string, string> = {
  brave: 'https://api-dashboard.search.brave.com/app/keys',
  tavily: 'https://app.tavily.com/home',
}

const inputClass =
  'w-full px-2.5 py-1.5 rounded-md text-[12px] bg-bg-secondary border border-border text-text-primary ' +
  'placeholder:text-text-muted focus:outline-none focus:border-accent'

/** Settings → Web: whether the agent may read web pages, and which search provider web_search uses. */
export const WebSettings: React.FC = () => {
  const [state, setState] = useState<WebState | null>(null)
  const [fetchMode, setFetchMode] = useState<Fetch>('ask')
  const [provider, setProvider] = useState<Provider>('')
  const [key, setKey] = useState('')
  const [searxngUrl, setSearxngUrl] = useState('')
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      if (event.data?.type !== 'webState') return
      const s = event.data.state as WebState
      setState(s)
      setFetchMode(s.fetch)
      setProvider(s.searchProvider)
      setSearxngUrl(s.searxngUrl)
      setKey('')
    }
    window.addEventListener('message', onMessage)
    vscode.postMessage({ type: 'getWeb' })
    return () => window.removeEventListener('message', onMessage)
  }, [])

  if (!state) return null

  const needsKey = provider === 'brave' || provider === 'tavily'
  const dirty =
    fetchMode !== state.fetch || provider !== state.searchProvider || key.trim().length > 0 ||
    (provider === 'searxng' && searxngUrl.trim() !== state.searxngUrl)
  const valid = !needsKey || state.hasSearchKey || key.trim().length > 0
  const validUrl = provider !== 'searxng' || /^https?:\/\/\S+$/.test(searxngUrl.trim())

  const save = () => {
    vscode.postMessage({
      type: 'saveWeb',
      fetch: fetchMode,
      searchProvider: provider,
      ...(key.trim() ? { searchKey: key.trim() } : {}),
      searxngUrl: provider === 'searxng' ? searxngUrl.trim() : state.searxngUrl,
    })
    setSaved(true)
    setTimeout(() => setSaved(false), 2500)
  }

  return (
    <div>
      <label className="flex items-center gap-1.5 text-[11px] font-medium text-text-secondary mb-1.5">
        <Globe size={12} />
        Web
      </label>

      <p className="text-[11px] text-text-secondary mb-1">Read web pages (web_fetch)</p>
      <div className="flex gap-1 mb-1">
        {([['ask', 'Ask each time'], ['allow', 'Allow'], ['off', 'Off']] as const).map(([value, label]) => (
          <button key={value} onClick={() => setFetchMode(value)}
            className={`px-2 py-0.5 rounded-md text-[11px] ${fetchMode === value ? 'bg-accent text-white' : 'bg-bg-tertiary text-text-secondary'}`}>
            {label}
          </button>
        ))}
      </div>
      <p className="text-[10px] text-text-muted leading-snug mb-3">
        Public pages only: your computer, your network and cloud metadata addresses are always refused.
        "Ask each time" shows every URL before it's opened.
      </p>

      <p className="text-[11px] text-text-secondary mb-1">Search the web (web_search)</p>
      <select className={inputClass} value={provider} onChange={(e) => setProvider(e.target.value as Provider)}>
        <option value="">Off</option>
        <option value="brave">Brave Search API (free tier, key)</option>
        <option value="tavily">Tavily (free tier, key)</option>
        <option value="searxng">SearXNG (your own server, no key)</option>
      </select>

      {needsKey && (
        <div className="mt-1.5">
          <div className="flex items-center justify-between mb-1">
            <span className="text-[10px] text-text-muted">{state.hasSearchKey && state.searchProvider === provider ? 'Key saved. Paste a new one to replace it.' : 'API key'}</span>
            <button onClick={() => vscode.postMessage({ type: 'openExternal', url: keyPages[provider] })} className="flex items-center gap-1 text-[10px] text-accent hover:underline">
              Get a key <ExternalLink size={9} />
            </button>
          </div>
          <div className="relative">
            <KeyRound size={11} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-text-muted" />
            <input type="password" className={`${inputClass} pl-7`} placeholder="Paste the key" value={key} onChange={(e) => setKey(e.target.value)} />
          </div>
        </div>
      )}
      {provider === 'searxng' && (
        <input className={`${inputClass} mt-1.5`} placeholder="http://localhost:8080" value={searxngUrl} onChange={(e) => setSearxngUrl(e.target.value)} />
      )}

      <button disabled={!dirty || !valid || !validUrl} onClick={save}
        className="mt-2 px-3 py-1 rounded-md text-[11px] font-medium bg-accent text-white disabled:opacity-40">
        {saved ? 'Saved' : 'Save'}
      </button>
    </div>
  )
}
