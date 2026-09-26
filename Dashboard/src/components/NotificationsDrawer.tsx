import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useActivity, useSessions } from '../api/hooks'
import { formatHumanActivity } from '../lib/translations'
import { TimeAgo } from './ui'
import { IconAlertTriangle, IconBell, IconCheckCircle, IconXCircle } from './Icons'

interface NotificationsDrawerProps {
  open: boolean
  onClose: () => void
}

type SeverityFilter = 'all' | 'error' | 'warning' | 'success'

interface NotificationItemData {
  id: string
  title: string
  description: string
  severity: 'error' | 'warning' | 'success'
  timestamp: string
  group?: string | null
  classPlanId?: string | null
  actionUrl: string
  actionLabel: string
}

export function NotificationsDrawer({ open, onClose }: NotificationsDrawerProps) {
  const navigate = useNavigate()
  const [filter, setFilter] = useState<SeverityFilter>('all')
  const { data: activity } = useActivity({ limit: 50 })
  const { data: sessions } = useSessions()

  if (!open) return null

  // Synthesize rich notifications from operational states (failed/needsAttention sessions, activity events)
  const notifications: NotificationItemData[] = []

  // Check sessions with failures or attention requirements
  if (sessions?.classes) {
    for (const c of sessions.classes) {
      if (c.headline === 'needsAttention') {
        const failedStage = c.stages.find((s) => s.state === 'failed')
        notifications.push({
          id: `session-fail-${c.classPlanId}`,
          title: `${c.group}: Stage ${failedStage?.label ?? 'Failed'}`,
          description: failedStage?.detail || 'An automated stage failed and requires review.',
          severity: 'error',
          timestamp: c.date,
          group: c.group,
          classPlanId: c.classPlanId,
          actionUrl: `/classes/${c.classPlanId}`,
          actionLabel: 'Triage Class →',
        })
      } else if (c.headline === 'blocked') {
        const blockedStage = c.stages.find((s) => s.state === 'blocked')
        notifications.push({
          id: `session-blocked-${c.classPlanId}`,
          title: `${c.group}: Execution Blocked`,
          description: blockedStage?.detail || 'Missing Zoom meeting link or required coordinator account.',
          severity: 'warning',
          timestamp: c.date,
          group: c.group,
          classPlanId: c.classPlanId,
          actionUrl: `/classes/${c.classPlanId}`,
          actionLabel: 'Fix Link/Account →',
        })
      }
    }
  }

  // Add recent activity events
  if (activity?.items) {
    for (const item of activity.items) {
      const severity = item.outcome === 'failed' ? 'error' : item.outcome === 'skipped' ? 'warning' : 'success'
      const readable = formatHumanActivity(item.kind, item.summary, item.detail, item.outcome)
      notifications.push({
        id: `act-${item.id}`,
        title: item.group ? `${item.group}: ${item.kind}` : item.kind,
        description: readable,
        severity,
        timestamp: item.at,
        group: item.group,
        actionUrl: item.group ? `/sessions?group=${encodeURIComponent(item.group)}` : '/activity',
        actionLabel: 'View in Logs →',
      })
    }
  }

  const filtered = notifications.filter((n) => {
    if (filter === 'all') return true
    return n.severity === filter
  })

  const errorCount = notifications.filter((n) => n.severity === 'error').length
  const warningCount = notifications.filter((n) => n.severity === 'warning').length
  const successCount = notifications.filter((n) => n.severity === 'success').length

  const handleOpenItem = (url: string) => {
    onClose()
    navigate(url)
  }

  return (
    <div className="fixed inset-0 z-50 overflow-hidden" role="dialog" aria-modal="true" aria-label="Notifications Drawer">
      {/* Backdrop */}
      <div className="absolute inset-0 bg-slate-900/50 backdrop-blur-xs transition-opacity" onClick={onClose} />

      <div className="fixed inset-y-0 right-0 flex max-w-full pl-10">
        <div className="w-screen max-w-md bg-white shadow-2xl flex flex-col dark:bg-slate-900 border-l border-slate-200 dark:border-slate-800">
          {/* Header */}
          <div className="flex items-center justify-between border-b border-slate-100 p-5 dark:border-slate-800">
            <div className="flex items-center gap-2.5">
              <div className="grid size-9 place-items-center rounded-lg bg-indigo-50 text-indigo-600 dark:bg-indigo-950/60 dark:text-indigo-400">
                <IconBell className="size-5" />
              </div>
              <div>
                <h2 className="text-base font-semibold text-slate-900 dark:text-slate-100">Operations Feed</h2>
                <p className="text-xs text-slate-500">Live alerts, stage failures & automation updates</p>
              </div>
            </div>
            <button
              type="button"
              onClick={onClose}
              className="rounded-lg p-2 text-slate-400 hover:bg-slate-100 hover:text-slate-600 dark:hover:bg-slate-800 dark:hover:text-slate-300"
            >
              <span className="sr-only">Close</span>
              ✕
            </button>
          </div>

          {/* Filter Bar */}
          <div className="flex gap-1.5 border-b border-slate-100 px-5 py-3 dark:border-slate-800 overflow-x-auto">
            <button
              type="button"
              onClick={() => setFilter('all')}
              className={`rounded-full px-3 py-1 text-xs font-semibold whitespace-nowrap transition-colors ${
                filter === 'all'
                  ? 'bg-slate-900 text-white dark:bg-slate-100 dark:text-slate-900'
                  : 'text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800'
              }`}
            >
              All ({notifications.length})
            </button>
            <button
              type="button"
              onClick={() => setFilter('error')}
              className={`rounded-full px-3 py-1 text-xs font-semibold whitespace-nowrap transition-colors ${
                filter === 'error'
                  ? 'bg-rose-600 text-white'
                  : 'text-rose-700 bg-rose-50 hover:bg-rose-100 dark:bg-rose-950/40 dark:text-rose-300'
              }`}
            >
              Errors ({errorCount})
            </button>
            <button
              type="button"
              onClick={() => setFilter('warning')}
              className={`rounded-full px-3 py-1 text-xs font-semibold whitespace-nowrap transition-colors ${
                filter === 'warning'
                  ? 'bg-amber-600 text-white'
                  : 'text-amber-700 bg-amber-50 hover:bg-amber-100 dark:bg-amber-950/40 dark:text-amber-300'
              }`}
            >
              Warnings ({warningCount})
            </button>
            <button
              type="button"
              onClick={() => setFilter('success')}
              className={`rounded-full px-3 py-1 text-xs font-semibold whitespace-nowrap transition-colors ${
                filter === 'success'
                  ? 'bg-emerald-600 text-white'
                  : 'text-emerald-700 bg-emerald-50 hover:bg-emerald-100 dark:bg-emerald-950/40 dark:text-emerald-300'
              }`}
            >
              Success ({successCount})
            </button>
          </div>

          {/* List */}
          <div className="flex-1 overflow-y-auto divide-y divide-slate-100 dark:divide-slate-800 p-2">
            {filtered.length === 0 ? (
              <div className="py-16 text-center text-sm text-slate-500">
                No notifications in this category.
              </div>
            ) : (
              filtered.map((item) => (
                <div
                  key={item.id}
                  onClick={() => handleOpenItem(item.actionUrl)}
                  className="group cursor-pointer rounded-xl p-3.5 transition-colors hover:bg-slate-50 dark:hover:bg-slate-800/60"
                >
                  <div className="flex items-start gap-3">
                    <span className="mt-0.5 shrink-0">
                      {item.severity === 'error' ? (
                        <IconXCircle className="size-5 text-rose-600" />
                      ) : item.severity === 'warning' ? (
                        <IconAlertTriangle className="size-5 text-amber-600" />
                      ) : (
                        <IconCheckCircle className="size-5 text-emerald-600" />
                      )}
                    </span>
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center justify-between gap-2">
                        <h4 className="text-xs font-semibold text-slate-900 truncate dark:text-slate-100">
                          {item.title}
                        </h4>
                        <span className="text-[11px] text-slate-400 shrink-0">
                          <TimeAgo iso={item.timestamp} />
                        </span>
                      </div>
                      <p className="mt-1 text-xs text-slate-600 leading-relaxed dark:text-slate-300">
                        {item.description}
                      </p>
                      <div className="mt-2 flex items-center justify-between">
                        {item.group && (
                          <span className="inline-block rounded bg-slate-100 px-1.5 py-0.5 text-[10px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                            {item.group}
                          </span>
                        )}
                        <span className="text-xs font-semibold text-indigo-600 group-hover:underline dark:text-indigo-400">
                          {item.actionLabel}
                        </span>
                      </div>
                    </div>
                  </div>
                </div>
              ))
            )}
          </div>

          {/* Footer */}
          <div className="border-t border-slate-100 p-4 dark:border-slate-800 flex justify-between items-center text-xs text-slate-500">
            <Link to="/notifications" onClick={onClose} className="font-medium text-indigo-600 hover:underline dark:text-indigo-400">
              Full Notifications Center →
            </Link>
            <button
              type="button"
              onClick={onClose}
              className="rounded-lg border border-slate-200 px-3 py-1.5 font-medium hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800"
            >
              Close
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
