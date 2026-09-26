import type { SessionStage } from '../api/types'
import { IconAlertTriangle, IconCheck, IconClock, IconRefresh, IconXCircle } from './Icons'

interface LifecycleTimelineProps {
  stages: SessionStage[]
  onRetryStage?: (stageKey: string) => void
  retryingKey?: string | null
}

interface StepDef {
  key: string
  label: string
  jobType: string
}

const LIFECYCLE_STEPS: StepDef[] = [
  { key: 'preflight', label: 'Pre-flight', jobType: 'preflight' },
  { key: 'zoom', label: 'Zoom Started', jobType: 'class.run' },
  { key: 'run', label: 'Run Session', jobType: 'lms.run_session' },
  { key: 'attendance', label: 'Attendance', jobType: 'lms.attendance' },
  { key: 'lateJoiners', label: 'Correction', jobType: 'lms.late_joiners' },
  { key: 'complete', label: 'Complete Session', jobType: 'lms.complete' },
  { key: 'zoomRecording', label: 'Zoom Recording', jobType: 'zoom.recording' },
  { key: 'drive', label: 'Drive Recording', jobType: 'recording.process' },
]

export function LifecycleTimeline({ stages, onRetryStage, retryingKey }: LifecycleTimelineProps) {
  // Map stages by key
  const stageMap = new Map<string, SessionStage>()
  for (const s of stages) {
    stageMap.set(s.key, s)
  }

  // Preflight is considered done if Zoom has opened, or if meeting URL exists and plan is not blocked
  const zoomStage = stageMap.get('zoom')
  const preflightDone = zoomStage?.state === 'done' || zoomStage?.state === 'running' || zoomStage?.state === 'due'

  return (
    <div className="rounded-2xl border border-slate-200/80 bg-white p-5 shadow-xs dark:border-slate-800/80 dark:bg-[#111726] dark:shadow-none ring-1 ring-slate-900/5 dark:ring-white/[0.03]">
      <div className="flex items-center justify-between pb-4 border-b border-slate-100 dark:border-slate-800/80">
        <div>
          <h3 className="text-sm font-bold text-slate-900 dark:text-slate-100">Automation Lifecycle Pipeline</h3>
          <p className="text-xs text-slate-500 dark:text-slate-400">Autonomous execution progression from pre-flight to recording archival</p>
        </div>
        <span className="text-xs font-semibold text-slate-400">8 Orchestrated Steps</span>
      </div>

      <div className="mt-6">
        <div className="relative">
          {/* Progress bar background line */}
          <div className="absolute left-4 top-4 bottom-4 w-0.5 bg-slate-200 dark:bg-slate-800 md:left-auto md:top-5 md:h-0.5 md:w-full md:bottom-auto" />

          {/* Steps container */}
          <div className="grid grid-cols-1 gap-6 md:grid-cols-8 md:gap-2">
            {LIFECYCLE_STEPS.map((step, idx) => {
              const stage = stageMap.get(step.key)
              const isPreflight = step.key === 'preflight'
              
              const isDone = isPreflight ? preflightDone : stage?.state === 'done'
              const isRunning = stage?.state === 'running'
              const isFailed = stage?.state === 'failed'
              const isBlocked = stage?.state === 'blocked'
              const isDue = stage?.state === 'due'
              const isWaiting = stage?.state === 'waiting'

              const statusColor = isDone
                ? 'bg-emerald-600 text-white ring-4 ring-emerald-500/20 dark:ring-emerald-500/20'
                : isRunning
                ? 'bg-sky-500 text-white ring-4 ring-sky-500/20 dark:ring-sky-500/20 animate-pulse'
                : isFailed
                ? 'bg-rose-600 text-white ring-4 ring-rose-500/20 dark:ring-rose-500/20'
                : isBlocked
                ? 'bg-amber-500 text-white ring-4 ring-amber-500/20 dark:ring-amber-500/20'
                : isDue
                ? 'bg-amber-400 text-white ring-4 ring-amber-500/20 dark:ring-amber-500/20'
                : isWaiting
                ? 'bg-slate-400 text-white ring-4 ring-slate-500/20 dark:ring-slate-700/40'
                : 'bg-slate-200 text-slate-600 dark:bg-[#161d2f] dark:text-slate-400'

              return (
                <div key={step.key} className="relative flex md:flex-col items-start md:items-center gap-3 md:gap-2 group">
                  {/* Step icon / node */}
                  <div className={`relative z-10 grid size-8 md:size-10 place-items-center rounded-full text-xs font-bold transition-transform ${statusColor}`}>
                    {isDone ? (
                      <IconCheck className="size-4 md:size-5" />
                    ) : isRunning ? (
                      <IconRefresh className="size-4 md:size-5 animate-spin" />
                    ) : isFailed ? (
                      <IconXCircle className="size-4 md:size-5" />
                    ) : isBlocked ? (
                      <IconAlertTriangle className="size-4 md:size-5" />
                    ) : isDue || isWaiting ? (
                      <IconClock className="size-4 md:size-5" />
                    ) : (
                      <span>{idx + 1}</span>
                    )}
                  </div>

                  {/* Step Info */}
                  <div className="min-w-0 md:text-center">
                    <p className="text-xs font-bold text-slate-900 dark:text-slate-100 truncate">
                      {step.label}
                    </p>
                    <p className="text-[11px] text-slate-500 dark:text-slate-400">
                      {isDone
                        ? stage?.detail || 'Completed'
                        : isRunning
                        ? 'In progress…'
                        : isFailed
                        ? 'Failed'
                        : isBlocked
                        ? 'Blocked'
                        : isDue
                        ? 'Due now'
                        : stage?.caption || 'Pending'}
                    </p>

                    {isFailed && onRetryStage && (
                      <button
                        type="button"
                        onClick={() => onRetryStage(step.key)}
                        disabled={retryingKey === step.key}
                        className="mt-1 inline-flex items-center gap-1 rounded bg-rose-50 px-2 py-0.5 text-[11px] font-semibold text-rose-700 hover:bg-rose-100 dark:bg-rose-950/50 dark:text-rose-300 disabled:opacity-50"
                      >
                        {retryingKey === step.key ? 'Retrying…' : 'Retry'}
                      </button>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      </div>
    </div>
  )
}
