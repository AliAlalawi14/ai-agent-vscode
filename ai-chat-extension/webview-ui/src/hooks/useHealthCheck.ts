import { useEffect, useRef } from 'react'
import { useSettingsStore } from '../stores/settingsStore'
import { vscode } from '../services/vscodeApi'

const HEALTH_CHECK_INTERVAL = 30000 // 30 seconds

export function useHealthCheck() {
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const setBackendStatus = useSettingsStore(state => state.setBackendStatus)
  const setLastHealthCheck = useSettingsStore(state => state.setLastHealthCheck)

  useEffect(() => {
    const checkHealth = () => {
      vscode.postMessage({ type: 'healthCheck' })
    }

    // Initial check
    checkHealth()

    // Periodic check
    intervalRef.current = setInterval(checkHealth, HEALTH_CHECK_INTERVAL)

    // Listen for health response
    const handleMessage = (event: MessageEvent) => {
      const msg = event.data
      if (msg.type === 'healthStatus') {
        setBackendStatus(msg.status, msg.detail ?? null)
        setLastHealthCheck(Date.now())
      }
    }

    window.addEventListener('message', handleMessage)

    return () => {
      if (intervalRef.current) clearInterval(intervalRef.current)
      window.removeEventListener('message', handleMessage)
    }
  }, [setBackendStatus, setLastHealthCheck])
}
