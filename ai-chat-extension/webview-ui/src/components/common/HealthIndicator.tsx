import React from 'react'
import { useSettingsStore, type BackendStatus } from '../../stores/settingsStore'

const colors: Record<BackendStatus, string> = {
  connected: 'bg-success',
  degraded: 'bg-warning',
  disconnected: 'bg-error',
  outdated: 'bg-error',
  setup: 'bg-warning',
  starting: 'bg-warning',
}

const labels: Record<BackendStatus, string> = {
  connected: 'Connected',
  degraded: 'Partly working',
  disconnected: 'Not running',
  outdated: 'Outdated: restart it',
  setup: 'Add a model',
  starting: 'Starting…',
}

/** The agent's state: a dot, plus words whenever it isn't simply "connected" (a color alone was confusing). */
export const HealthIndicator: React.FC = () => {
  const status = useSettingsStore(state => state.backendStatus)
  const detail = useSettingsStore(state => state.backendDetail)
  const lastCheck = useSettingsStore(state => state.lastHealthCheck)

  const timeAgo = lastCheck ? `checked ${Math.round((Date.now() - lastCheck) / 1000)}s ago` : 'not checked yet'
  const starting = status === 'starting'

  return (
    <div
      className="flex items-center gap-1.5"
      role="status"
      aria-live="polite"
      title={`${labels[status]}${detail ? `: ${detail}` : ''} · ${timeAgo}`}
    >
      <span className="relative flex w-2 h-2">
        {(status === 'connected' || starting) && (
          <span className={`absolute inset-0 rounded-full ${colors[status]} animate-ping opacity-40`} />
        )}
        <span className={`relative w-2 h-2 rounded-full ${colors[status]} transition-colors`} />
      </span>
      {status !== 'connected' && (
        <span className={`text-[10px] ${starting ? 'text-warning' : status === 'setup' ? 'text-warning' : 'text-error'}`}>
          {labels[status]}
        </span>
      )}
    </div>
  )
}
