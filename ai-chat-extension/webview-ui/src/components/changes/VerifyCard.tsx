import React, { useState } from 'react'
import { CheckCircle2, ChevronDown, ChevronRight, Info, Loader2, Wrench, XCircle } from 'lucide-react'
import { openProviderSettings } from '../../stores/setupStore'

export interface VerifyStep {
  kind: string
  command: string
  ok?: boolean
  summary?: string
  output?: string
  durationMs?: number
  skipped?: boolean
  timedOut?: boolean
  tests?: { passed: number; failed: number; skipped: number; total: number } | null
}

/** A verify-loop event: running → passed | failed (willFix: the agent is fixing it) | unavailable. */
export interface VerifyData {
  status: 'running' | 'passed' | 'failed' | 'unavailable'
  attempt?: number
  steps?: VerifyStep[]
  willFix?: boolean
  reason?: string
}

/** "Build passed · 12/12 tests passed" from the steps that ran. */
export function verifySummary(v: VerifyData): string {
  if (v.status === 'running') return `Checking: ${(v.steps ?? []).map((s) => s.command).join(' → ')}…`
  if (v.status === 'unavailable') return v.reason ?? 'No build or test command found.'
  const ran = (v.steps ?? []).filter((s) => !s.skipped)
  return ran.map((s) => s.summary).filter(Boolean).join(' · ')
}

const seconds = (ms?: number) => (ms === undefined ? '' : ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`)

/** The verify loop's result in the chat: what ran, what passed, and the output of what failed. */
export const VerifyCard: React.FC<{ verify: VerifyData }> = ({ verify }) => {
  const failedStep = verify.steps?.find((s) => s.ok === false && !s.skipped)
  const [open, setOpen] = useState(verify.status === 'failed' && !verify.willFix)

  const tone =
    verify.status === 'passed' ? 'border-success/30 bg-success/5'
      : verify.status === 'failed' ? 'border-error/30 bg-error-subtle'
        : 'border-border bg-bg-secondary'

  const icon =
    verify.status === 'running' ? <Loader2 size={13} className="text-accent animate-spin shrink-0" />
      : verify.status === 'passed' ? <CheckCircle2 size={13} className="text-success shrink-0" />
        : verify.status === 'failed' ? <XCircle size={13} className="text-error shrink-0" />
          : <Info size={13} className="text-text-muted shrink-0" />

  return (
    <div className={`my-1 rounded-lg border ${tone} animate-slide-up overflow-hidden`}>
      <button
        onClick={() => failedStep && setOpen(!open)}
        className={`w-full flex items-center gap-2 px-3 py-1.5 text-left ${failedStep ? 'cursor-pointer' : 'cursor-default'}`}
      >
        {icon}
        <span className="flex-1 min-w-0 text-[12px] text-text-primary truncate" title={verifySummary(verify)}>
          {verify.status === 'passed' && <span className="font-medium">Checked: </span>}
          {verifySummary(verify)}
        </span>
        {(verify.attempt ?? 0) > 1 && <span className="text-[10px] text-text-muted shrink-0">attempt {verify.attempt}</span>}
        {failedStep && (open ? <ChevronDown size={12} className="text-text-muted shrink-0" /> : <ChevronRight size={12} className="text-text-muted shrink-0" />)}
      </button>

      {verify.status === 'failed' && verify.willFix && (
        <div className="flex items-center gap-1.5 px-3 pb-1.5 text-[11px] text-text-secondary">
          <Wrench size={11} /> The agent is fixing it and will check again.
        </div>
      )}
      {verify.status === 'unavailable' && (
        <div className="px-3 pb-1.5 text-[11px]">
          <button onClick={() => openProviderSettings(false)} className="text-accent hover:underline">Set a check command</button>
        </div>
      )}

      {open && failedStep?.output && (
        <div className="border-t border-border/60">
          <div className="px-3 pt-1.5 text-[10px] text-text-muted">
            <code>{failedStep.command}</code> · {seconds(failedStep.durationMs)}
          </div>
          <pre className="font-mono text-[11px] leading-[1.5] text-text-secondary px-3 py-2 max-h-[240px] overflow-auto whitespace-pre-wrap">
            {failedStep.output}
          </pre>
        </div>
      )}
    </div>
  )
}
