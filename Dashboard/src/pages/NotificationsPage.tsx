import { useState } from 'react'
import { useNavigate } from 'react-router'
import { useActivity, useSessions } from '../api/hooks'
import { PageHeader } from '../components/Layout'
import { Card, EmptyState, TimeAgo } from '../components/ui'
import { IconAlertTriangle, IconCheckCircle, IconXCircle } from '../components/Icons'
import { formatHumanActivity } from '../lib/translations'

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

export function NotificationsPage() {
  const navigate = useNavigate()
  const [filter, setFilter] = useState<SeverityFilter>('all')
  const [search, setSearch] = useState('')

  const { data: activity } = useActivity({ limit: 100 })
  const { data: sessions } = useSessions()

  const notifications: NotificationItemData[] = []

  // Synthesize from sessions that need attention
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

  // Synthesize from activity feed
  if (activity?.items) {
    for (const item of activity.items) {
      const severity = item.outcome === 'failed' ? 'error' : item.outcome === 'skipped' ? 'warning' : 'success'
      const readable = formatHumanActivity(item.kind, item.summary, item.detail)
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
    if (filter !== 'all' && n.severity !== filter) return false
    if (search.trim()) {
      const q = search.toLowerCase()
      return (
        n.title.toLowerCase().includes(q) ||
        n.description.toLowerCase().includes(q) ||
        (n.group && n.group.toLowerCase().includes(q))
      )
    }
    return true
  })

  const errorCount = notifications.filter((n) => n.severity === 'error').length
  const warningCount = notifications.filter((n) => n.severity === 'warning').length
  const successCount = notifications.filter((n) => n.severity === 'success').length

  return (
    <div className="space-y-6 pb-12">
      <PageHeader
        title="Notifications Center"
        description="Comprehensive alert history, automated stage outcomes, and operator action items across all class occurrences."
      />

      {/* Top Filter Bar */}
      <div className="flex flex-wrap items-center justify-between gap-4 rounded-2xl border border-slate-200/90 bg-white p-4 shadow-xs dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => setFilter('all')}
            className={`rounded-full px-3.5 py-1.5 text-xs font-bold transition-colors ${
              filter === 'all'
                ? 'bg-slate-900 text-white dark:bg-slate-100 dark:text-slate-900'
                : 'text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800'
            }`}
          >
            All Alerts ({notifications.length})
          </button>
          <button
            type="button"
            onClick={() => setFilter('error')}
            className={`rounded-full px-3.5 py-1.5 text-xs font-bold transition-colors ${
              filter === 'error'
                ? 'bg-rose-600 text-white'
                : 'bg-rose-50 text-rose-700 hover:bg-rose-100 dark:bg-rose-950/40 dark:text-rose-300'
            }`}
          >
            Errors ({errorCount})
          </button>
          <button
            type="button"
            onClick={() => setFilter('warning')}
            className={`rounded-full px-3.5 py-1.5 text-xs font-bold transition-colors ${
              filter === 'warning'
                ? 'bg-amber-600 text-white'
                : 'bg-amber-50 text-amber-700 hover:bg-amber-100 dark:bg-amber-950/40 dark:text-amber-300'
            }`}
          >
            Warnings ({warningCount})
          </button>
          <button
            type="button"
            onClick={() => setFilter('success')}
            className={`rounded-full px-3.5 py-1.5 text-xs font-bold transition-colors ${
              filter === 'success'
                ? 'bg-emerald-600 text-white'
                : 'bg-emerald-50 text-emerald-700 hover:bg-emerald-100 dark:bg-emerald-950/40 dark:text-emerald-300'
            }`}
          >
            Success ({successCount})
          </button>
        </div>

        {/* Search */}
        <div className="w-full sm:w-64">
          <input
            type="text"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search notifications…"
            className="w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-1.5 text-xs text-slate-800 dark:border-slate-800 dark:bg-slate-800/80 dark:text-slate-200"
          />
        </div>
      </div>

      {/* Notifications List Card */}
      <Card>
        {filtered.length === 0 ? (
          <EmptyState>No notifications found matching your filter criteria.</EmptyState>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {filtered.map((item) => (
              <div
                key={item.id}
                onClick={() => navigate(item.actionUrl)}
                className="group cursor-pointer p-4 transition-colors hover:bg-slate-50/80 dark:hover:bg-slate-800/50"
              >
                <div className="flex items-start justify-between gap-4">
                  <div className="flex items-start gap-3.5">
                    <span className="mt-0.5 shrink-0">
                      {item.severity === 'error' ? (
                        <span className="grid size-8 place-items-center rounded-xl bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-400">
                          <IconXCircle className="size-4" />
                        </span>
                      ) : item.severity === 'warning' ? (
                        <span className="grid size-8 place-items-center rounded-xl bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-400">
                          <IconAlertTriangle className="size-4" />
                        </span>
                      ) : (
                        <span className="grid size-8 place-items-center rounded-xl bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-400">
                          <IconCheckCircle className="size-4" />
                        </span>
                      )}
                    </span>

                    <div>
                      <div className="flex items-center gap-2">
                        <h3 className="text-sm font-bold text-slate-900 dark:text-slate-100">
                          {item.title}
                        </h3>
                        {item.group && (
                          <span className="rounded bg-slate-100 px-2 py-0.5 text-[10px] font-semibold text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                            {item.group}
                          </span>
                        )}
                      </div>
                      <p className="mt-1 text-xs text-slate-600 dark:text-slate-300 leading-relaxed">
                        {item.description}
                      </p>
                    </div>
                  </div>

                  <div className="flex flex-col items-end gap-1.5 shrink-0">
                    <span className="text-xs text-slate-400">
                      <TimeAgo iso={item.timestamp} />
                    </span>
                    <span className="text-xs font-semibold text-indigo-600 group-hover:underline dark:text-indigo-400">
                      {item.actionLabel}
                    </span>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </Card>
    </div>
  )
}
