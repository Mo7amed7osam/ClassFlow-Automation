import { useState } from 'react'
import { useNavigate } from 'react-router'
import { useSystemHealth } from '../api/hooks'
import type { HealthCheckItem } from '../api/types'
import { IconAlertTriangle, IconCheckCircle, IconRefresh, IconXCircle } from './Icons'
import { Modal } from './Overlay'
import { Spinner } from './ui'

interface HealthModalProps {
  open: boolean
  onClose: () => void
}

export function HealthModal({ open, onClose }: HealthModalProps) {
  const { data: health, isLoading, error, refetch, isFetching } = useSystemHealth(open)
  const navigate = useNavigate()
  const [filter, setFilter] = useState<'all' | 'issues'>('all')

  if (!open) return null

  const checks: HealthCheckItem[] = health?.checks ?? []
  const issues = checks.filter((c) => c.status !== 'healthy')
  const displayChecks = filter === 'issues' ? issues : checks

  const overall = health?.overall ?? (issues.length > 0 ? 'warning' : 'healthy')

  const handleAction = (action: string | null) => {
    if (!action) return
    onClose()
    if (action.startsWith('/')) {
      navigate(action)
    } else {
      navigate(`/${action}`)
    }
  }

  return (
    <Modal open={open} onClose={onClose} title="System Operations Health Check">
      <div className="space-y-4">
        {/* Header Status Card */}
        <div
          className={`flex items-center justify-between rounded-xl p-4 border ${
            overall === 'healthy'
              ? 'bg-emerald-50/70 border-emerald-200 dark:bg-emerald-950/20 dark:border-emerald-800'
              : overall === 'warning'
              ? 'bg-amber-50/70 border-amber-200 dark:bg-amber-950/20 dark:border-amber-800'
              : 'bg-rose-50/70 border-rose-200 dark:bg-rose-950/20 dark:border-rose-800'
          }`}
        >
          <div className="flex items-center gap-3">
            <div
              className={`grid size-10 place-items-center rounded-lg ${
                overall === 'healthy'
                  ? 'bg-emerald-600 text-white'
                  : overall === 'warning'
                  ? 'bg-amber-600 text-white'
                  : 'bg-rose-600 text-white'
              }`}
            >
              {overall === 'healthy' ? (
                <IconCheckCircle className="size-6" />
              ) : overall === 'warning' ? (
                <IconAlertTriangle className="size-6" />
              ) : (
                <IconXCircle className="size-6" />
              )}
            </div>
            <div>
              <h3 className="font-semibold text-slate-900 dark:text-slate-100">
                {overall === 'healthy'
                  ? 'All Systems Fully Operational'
                  : overall === 'warning'
                  ? `${issues.length} System Warning${issues.length === 1 ? '' : 's'} Requiring Review`
                  : 'Critical Automation Failure Detected'}
              </h3>
              <p className="text-xs text-slate-600 dark:text-slate-300">
                Diagnostic across 11 core infrastructure and service checks
              </p>
            </div>
          </div>

          <button
            type="button"
            onClick={() => refetch()}
            disabled={isFetching}
            className="flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 shadow-sm hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700 disabled:opacity-50"
          >
            <IconRefresh className={`size-3.5 ${isFetching ? 'animate-spin' : ''}`} />
            Re-check
          </button>
        </div>

        {/* Filter Pills */}
        <div className="flex items-center justify-between border-b border-slate-100 pb-2 dark:border-slate-800">
          <div className="flex gap-2">
            <button
              type="button"
              onClick={() => setFilter('all')}
              className={`rounded-full px-3 py-1 text-xs font-medium transition-colors ${
                filter === 'all'
                  ? 'bg-slate-900 text-white dark:bg-slate-100 dark:text-slate-900'
                  : 'text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800'
              }`}
            >
              All Checks ({checks.length})
            </button>
            <button
              type="button"
              onClick={() => setFilter('issues')}
              className={`rounded-full px-3 py-1 text-xs font-medium transition-colors ${
                filter === 'issues'
                  ? 'bg-amber-600 text-white'
                  : 'text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800'
              }`}
            >
              Needs Attention ({issues.length})
            </button>
          </div>
          <span className="text-xs text-slate-500">Live VPS & Cloud Verification</span>
        </div>

        {/* Check Items List */}
        {isLoading ? (
          <div className="py-12 text-center">
            <Spinner label="Executing system diagnostics…" />
            <p className="mt-2 text-xs text-slate-500">Inspecting database, workers, Chromium, and OAuth tokens…</p>
          </div>
        ) : error ? (
          <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-xs text-rose-800 dark:border-rose-900 dark:bg-rose-950/30 dark:text-rose-200">
            Health check diagnostic failed to execute: {String(error)}
          </div>
        ) : displayChecks.length === 0 ? (
          <div className="py-8 text-center text-sm text-slate-500">
            No issues found. All systems within nominal tolerances.
          </div>
        ) : (
          <div className="max-h-[26rem] divide-y divide-slate-100 overflow-y-auto pr-1 dark:divide-slate-800">
            {displayChecks.map((item) => (
              <div key={item.name} className="py-3 first:pt-0 last:pb-0">
                <div className="flex items-start justify-between gap-3">
                  <div className="flex items-start gap-2.5">
                    <span className="mt-0.5 shrink-0">
                      {item.status === 'healthy' ? (
                        <span className="inline-flex size-5 items-center justify-center rounded-full bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-400 font-bold text-xs">
                          ✓
                        </span>
                      ) : item.status === 'warning' ? (
                        <span className="inline-flex size-5 items-center justify-center rounded-full bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-400 font-bold text-xs">
                          ⚠
                        </span>
                      ) : (
                        <span className="inline-flex size-5 items-center justify-center rounded-full bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-400 font-bold text-xs">
                          ✕
                        </span>
                      )}
                    </span>
                    <div>
                      <div className="flex items-center gap-2">
                        <h4 className="text-sm font-semibold text-slate-900 dark:text-slate-100">{item.name}</h4>
                        <span className="text-xs text-slate-500">· {item.summary}</span>
                      </div>
                      <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-300">{item.detail}</p>
                      {item.fix && (
                        <p className="mt-1.5 rounded bg-amber-50 px-2 py-1 text-xs text-amber-800 dark:bg-amber-950/40 dark:text-amber-300">
                          <strong className="font-semibold">Recommended Fix:</strong> {item.fix}
                        </p>
                      )}
                    </div>
                  </div>

                  {item.action && (
                    <button
                      type="button"
                      onClick={() => handleAction(item.action)}
                      className="shrink-0 rounded-md border border-slate-200 bg-white px-2.5 py-1 text-xs font-semibold text-indigo-600 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-indigo-400 dark:hover:bg-slate-700"
                    >
                      Resolve →
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}

        <div className="flex items-center justify-between border-t border-slate-100 pt-3 text-xs text-slate-500 dark:border-slate-800">
          <span>Non-destructive read health telemetry</span>
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg bg-slate-900 px-4 py-2 font-medium text-white hover:bg-slate-800 dark:bg-slate-100 dark:text-slate-900"
          >
            Close
          </button>
        </div>
      </div>
    </Modal>
  )
}
