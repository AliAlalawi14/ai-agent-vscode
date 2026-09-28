import React from 'react'
import { Zap, Clock, DollarSign } from 'lucide-react'
import { useSettingsStore } from '../../stores/settingsStore'

export interface MessageMetrics {
  tokens?: number
  cost?: number
  latencyMs?: number
}

interface TokenCostBadgeProps {
  metrics: MessageMetrics
}

export const TokenCostBadge: React.FC<TokenCostBadgeProps> = ({ metrics }) => {
  const showMetrics = useSettingsStore(state => state.showMetrics)

  if (!showMetrics) return null
  if (!metrics.tokens && !metrics.cost && !metrics.latencyMs) return null

  const formatCost = (cost: number) => {
    if (cost < 0.01) return `$${cost.toFixed(4)}`
    return `$${cost.toFixed(3)}`
  }

  const formatLatency = (ms: number) => {
    if (ms < 1000) return `${ms}ms`
    return `${(ms / 1000).toFixed(1)}s`
  }

  return (
    <div className="flex items-center gap-3 mt-1.5 text-[10px] text-text-muted">
      {metrics.tokens !== undefined && (
        <span className="flex items-center gap-0.5">
          <Zap size={10} />
          {metrics.tokens} tokens
        </span>
      )}
      {metrics.latencyMs !== undefined && (
        <span className="flex items-center gap-0.5">
          <Clock size={10} />
          {formatLatency(metrics.latencyMs)}
        </span>
      )}
      {metrics.cost !== undefined && (
        <span className="flex items-center gap-0.5">
          <DollarSign size={10} />
          {formatCost(metrics.cost)}
        </span>
      )}
    </div>
  )
}
