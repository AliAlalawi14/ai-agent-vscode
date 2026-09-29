import React, { useEffect, useState } from 'react'
import { ShieldCheck } from 'lucide-react'
import { vscode } from '../../services/vscodeApi'

interface ChecksState {
  verify: boolean
  commands: string[]
  budget: number
}

const inputClass =
  'w-full px-2.5 py-1.5 rounded-md text-[12px] bg-bg-secondary border border-border text-text-primary ' +
  'placeholder:text-text-muted focus:outline-none focus:border-accent'

/** Settings → Checks & budget: the verify loop (build + tests after edits) and the cost cap per task. */
export const ChecksSettings: React.FC = () => {
  const [state, setState] = useState<ChecksState | null>(null)
  const [verify, setVerify] = useState(true)
  const [commands, setCommands] = useState('')
  const [budget, setBudget] = useState('')
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      if (event.data?.type !== 'checksState') return
      const s = event.data.state as ChecksState
      setState(s)
      setVerify(s.verify)
      setCommands(s.commands.join('\n'))
      setBudget(s.budget > 0 ? String(s.budget) : '')
    }
    window.addEventListener('message', onMessage)
    vscode.postMessage({ type: 'getChecks' })
    return () => window.removeEventListener('message', onMessage)
  }, [])

  if (!state) return null

  const budgetNumber = budget.trim() === '' ? 0 : Number(budget)
  const validBudget = Number.isFinite(budgetNumber) && budgetNumber >= 0
  const commandList = commands.split('\n').map((c) => c.trim()).filter(Boolean)
  const dirty = verify !== state.verify || commandList.join('\n') !== state.commands.join('\n') || budgetNumber !== state.budget

  const save = () => {
    vscode.postMessage({ type: 'saveChecks', state: { verify, commands: commandList, budget: budgetNumber } })
    setSaved(true)
    setTimeout(() => setSaved(false), 2500)
  }

  return (
    <div>
      <label className="flex items-center gap-1.5 text-[11px] font-medium text-text-secondary mb-1.5">
        <ShieldCheck size={12} />
        Checks &amp; budget
      </label>

      <label className="flex items-center justify-between text-[11px] text-text-secondary">
        <span>
          Build and test after the agent's changes
          <span className="block text-[10px] text-text-muted leading-snug">
            The result shows in the review bar; failures go back to the agent to fix (up to 2 tries). Trusted workspaces only.
          </span>
        </span>
        <button
          onClick={() => setVerify(!verify)}
          className={`relative w-8 h-[18px] rounded-full transition-colors shrink-0 ml-2 ${verify ? 'bg-accent' : 'bg-bg-tertiary border border-border'}`}
        >
          <div className={`absolute top-[2px] w-3.5 h-3.5 rounded-full bg-white shadow transition-transform ${verify ? 'translate-x-[15px]' : 'translate-x-[2px]'}`} />
        </button>
      </label>

      {verify && (
        <div className="mt-1.5">
          <textarea
            className={`${inputClass} font-mono h-14 resize-y`}
            placeholder={'Detected from the project (dotnet, npm, cargo, go, pytest…)\nor one command per line, e.g. npm run lint'}
            value={commands}
            onChange={(e) => setCommands(e.target.value)}
          />
        </div>
      )}

      <div className="mt-3">
        <span className="text-[11px] text-text-secondary">Budget per task</span>
        <div className="flex items-center gap-1.5 mt-1">
          <span className="text-[12px] text-text-muted">$</span>
          <input
            className={`${inputClass} w-24`}
            inputMode="decimal"
            placeholder="no limit"
            value={budget}
            onChange={(e) => setBudget(e.target.value)}
          />
          <span className="text-[10px] text-text-muted leading-snug">
            Stops the task at this cost and offers Continue. For models with a known price.
          </span>
        </div>
        {!validBudget && <p className="text-[10px] text-error mt-1">Enter an amount like 0.25</p>}
      </div>

      <button disabled={!dirty || !validBudget} onClick={save}
        className="mt-2 px-3 py-1 rounded-md text-[11px] font-medium bg-accent text-white disabled:opacity-40">
        {saved ? 'Saved' : 'Save'}
      </button>
    </div>
  )
}
