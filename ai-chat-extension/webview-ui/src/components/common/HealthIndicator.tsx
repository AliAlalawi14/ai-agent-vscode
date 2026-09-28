import React from 'react'
import { useSettingsStore } from '../../stores/settingsStore'

export const HealthIndicator: React.FC = () => {
  const status = useSettingsStore(state => state.backendStatus)
  const lastCheck = useSettingsStore(state => state.lastHealthCheck)

  const colors = {
    connected: 'bg-success',
    degraded: 'bg-warning',
    disconnected: 'bg-error',
    outdated: 'bg-error',
  }

  const labels = {
    connected: 'Backend connected',
    degraded: 'Backend degraded',
    disconnected: 'Backend disconnected',
    outdated: 'Backend outdated — restart it',
  }

  const timeAgo = lastCheck
    ? `Last checked ${Math.round((Date.now() - lastCheck) / 1000)}s ago`
    : 'Not checked yet'

  return (
    <div className="relative group" title={`${labels[status]} · ${timeAgo}`}>
      <div className={`w-2 h-2 rounded-full ${colors[status]} transition-colors`} />
      {status === 'connected' && (
        <div className={`absolute inset-0 w-2 h-2 rounded-full ${colors[status]} animate-ping opacity-30`} />
      )}
    </div>
  )
}
