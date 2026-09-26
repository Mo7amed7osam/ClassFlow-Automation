import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import {
  useActivity,
  useGoogleSheetsStatus,
  useMe,
  useOverview,
  useRecordings,
  useSessions,
  useSystemHealth,
} from '../api/hooks'
import type { SessionClass } from '../api/types'
import { HealthModal } from '../components/HealthModal'
import { PageHeader } from '../components/Layout'
import { RecordingsTable } from '../components/RecordingsTable'
import { Card, EmptyState, ErrorBanner, Pill, TimeAgo, td, th } from '../components/ui'
import { formatHumanActivity } from '../lib/translations'

/** Today in Africa/Cairo ISO format */
function todayCairo(): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Africa/Cairo' }).format(new Date())
}

/** Tomorrow in Africa/Cairo ISO format */
function tomorrowCairo(): string {
  const d = new Date()
  d.setDate(d.getDate() + 1)
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Africa/Cairo' }).format(d)
}

/** What a class is doing right now, in the words of the stage that is doing it. */
function doingNow(item: SessionClass): { label: string; tone: 'green' | 'amber' | 'red' | 'slate' } {
  const running = item.stages.find((stage) => stage.state === 'running')
  if (running) return { label: running.label, tone: 'green' }
  const failed = item.stages.find((stage) => stage.state === 'failed')
  if (failed) return { label: `${failed.label} failed`, tone: 'red' }
  const blocked = item.stages.find((stage) => stage.state === 'blocked')
  if (blocked) return { label: blocked.detail ?? `${blocked.label} cannot run`, tone: 'amber' }
  const due = item.stages.find((stage) => stage.state === 'due')
  return due ? { label: `${due.label} is due`, tone: 'amber' } : { label: 'waiting for its time', tone: 'slate' }
}

function RunningNow() {
  const sessions = useSessions({ from: todayCairo(), to: todayCairo() })
  const classes = sessions.data?.classes ?? []
  const live = classes.filter((item) => item.headline === 'running' || item.headline === 'needsAttention' || item.headline === 'blocked')

  return (
    <Card
      title="Today, right now"
      action={<Link to="/sessions" className="text-xs font-semibold text-indigo-600 hover:underline dark:text-indigo-400">Every class →</Link>}
    >
      {sessions.error ? <ErrorBanner error={sessions.error} /> : live.length === 0 ? (
        <EmptyState>
          {classes.length === 0 ? 'No class today.' : `${classes.length} class(es) today, none of them running or waiting for anybody.`}
        </EmptyState>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[34rem]">
            <thead className="border-b border-slate-100 bg-slate-50/60 dark:border-slate-800/80 dark:bg-[#0c111d]">
              <tr>
                <th className={th}>Class</th>
                <th className={th}>Starts</th>
                <th className={th}>Doing now</th>
                <th className={th}>How it is going</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
              {live.map((item) => {
                const now = doingNow(item)
                return (
                  <tr key={item.classPlanId} className="hover:bg-slate-50/60 dark:hover:bg-[#161d2f]/40 transition-colors">
                    <td className={td}>
                      <Link to={`/classes/${item.classPlanId}`} className="font-semibold text-slate-900 hover:text-indigo-600 dark:text-slate-100 dark:hover:text-indigo-400">
                        {item.group}
                      </Link>
                      {item.title && <p className="text-xs text-slate-500 dark:text-slate-400">{item.title}</p>}
                    </td>
                    <td className={`${td} tabular-nums font-mono text-slate-700 dark:text-slate-300`}>{item.startTime ?? '—'}</td>
                    <td className={`${td} text-slate-700 dark:text-slate-300 font-medium`}>{now.label}</td>
                    <td className={td}>
                      <Pill tone={now.tone}>
                        {item.headline === 'running' ? 'Running' : item.headline === 'blocked' ? 'Blocked' : 'Needs somebody'}
                      </Pill>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}

export function OverviewPage() {
  const overview = useOverview()
  const sessions = useSessions({ from: todayCairo(), to: tomorrowCairo() })
  const latest = useRecordings({ sort: 'updated', pageSize: 10 })
  const activity = useActivity({ limit: 8 })
  const google = useGoogleSheetsStatus()
  const systemHealth = useSystemHealth()
  const navigate = useNavigate()
  const { data: me } = useMe()
  const isAdmin = me?.role === 'admin'
  const [healthModalOpen, setHealthModalOpen] = useState(false)

  const o = overview.data
  const agents = o?.agents
  const jobs = o?.jobs
  const classes = sessions.data?.classes ?? []
  const checks = systemHealth.data?.checks ?? []
  const findCheck = (name: string) => checks.find((item) => item.name === name)

  // Operational categorizations
  const runningClasses = classes.filter((c) => c.headline === 'running')
  const attentionClasses = classes.filter(
    (c) => c.headline === 'needsAttention' || c.headline === 'blocked' || c.stages.some((s) => s.state === 'failed')
  )

  const classesTodayCount = classes.filter((c) => c.date === todayCairo()).length
  const attendancePendingCount = classes.filter((c) =>
    c.stages.some((s) => s.key === 'attendance' && (s.state === 'due' || s.state === 'later'))
  ).length
  const recordingsPending = o?.recordings.pending ?? 0

  const isHealthy = attentionClasses.length === 0 && systemHealth.data?.overall === 'healthy'
  const healthTitle = systemHealth.error ? 'Unavailable' : systemHealth.isLoading || !systemHealth.data
    ? 'Checking services'
    : isHealthy ? 'All clear' : 'Needs attention'
  const healthColor = isHealthy ? 'green' : !systemHealth.data || systemHealth.error ? 'slate'
    : systemHealth.data.overall === 'failed' || attentionClasses.length > 0 ? 'red' : 'amber'
  const healthStyles = {
    green: { header: 'bg-emerald-500/10 text-emerald-700 border-emerald-500/20 dark:text-emerald-400 hover:bg-emerald-500/20', dot: 'bg-emerald-500', accent: 'border-emerald-500', title: 'text-emerald-700 dark:text-emerald-400' },
    amber: { header: 'bg-amber-500/10 text-amber-700 border-amber-500/20 dark:text-amber-400 hover:bg-amber-500/20', dot: 'bg-amber-500', accent: 'border-amber-500', title: 'text-amber-700 dark:text-amber-400' },
    red: { header: 'bg-rose-500/10 text-rose-700 border-rose-500/20 dark:text-rose-400 hover:bg-rose-500/20', dot: 'bg-rose-500', accent: 'border-rose-500', title: 'text-rose-700 dark:text-rose-400' },
    slate: { header: 'bg-slate-500/10 text-slate-700 border-slate-500/20 dark:text-slate-300 hover:bg-slate-500/20', dot: 'bg-slate-400', accent: 'border-slate-400', title: 'text-slate-700 dark:text-slate-300' },
  }[healthColor]
  const healthHint = systemHealth.error
    ? 'Diagnostics could not be loaded'
    : systemHealth.data
      ? `${checks.filter((item) => item.status === 'healthy').length} of ${checks.length} checks healthy`
      : 'Checking service health'

  const metrics = isAdmin
    ? [
        { label: 'Classes today', value: String(classesTodayCount) },
        { label: 'Live now', value: String(runningClasses.length) },
        { label: 'Needs attention', value: String(attentionClasses.length), alert: attentionClasses.length > 0 },
        { label: 'Workers online', value: agents ? `${agents.online} / ${agents.total}` : overview.isLoading ? '…' : '—' },
        { label: 'Jobs running', value: jobs ? String(jobs.running) : overview.isLoading ? '…' : '—' },
      ]
    : [
        { label: 'My groups', value: o ? String(o.groups) : overview.isLoading ? '…' : '—' },
        { label: 'Classes today', value: String(classesTodayCount) },
        { label: 'Live now', value: String(runningClasses.length) },
        { label: 'Attendance due', value: String(attendancePendingCount) },
        { label: 'Drive links pending', value: String(recordingsPending) },
      ]

  const integrationStatus = (name: string) => systemHealth.error ? 'failed'
    : findCheck(name)?.status ?? (systemHealth.isLoading ? 'checking' : 'warning')

  const integrations = [
    {
      name: 'Google Sheets',
      to: '/settings',
      summary: google.isLoading ? 'Checking connection' : google.error ? 'Connection could not be checked'
        : google.data?.configured ? 'Read-only · daily sync at 08:00 Cairo' : findCheck('Sheet')?.summary ?? 'Not connected',
      status: google.isLoading ? 'checking' : google.error ? 'failed' : google.data?.configured ? 'healthy' : 'warning',
    },
    {
      name: 'LMS portal',
      to: '/lms-accounts',
      summary: findCheck('LMS')?.summary ?? (systemHealth.isLoading ? 'Checking saved sign-ins' : 'Status unavailable'),
      status: integrationStatus('LMS'),
    },
    {
      name: 'Zoom profiles',
      to: '/zoom-accounts',
      summary: findCheck('Zoom profiles')?.summary ?? (systemHealth.isLoading ? 'Checking configured accounts' : 'Status unavailable'),
      status: integrationStatus('Zoom profiles'),
    },
    {
      name: 'OpenRouter',
      to: '/settings',
      summary: findCheck('OpenRouter')?.summary ?? (systemHealth.isLoading ? 'Checking AI matching' : 'Status unavailable'),
      status: integrationStatus('OpenRouter'),
    },
  ]

  return (
    <div className="space-y-8 pb-12">
      {/* Page Title & Global Operational Ticker */}
      <PageHeader
        title="Overview"
        description={
          isAdmin
            ? 'Your cloud activity and the classes that need attention.'
            : "Your groups' classes, attendance and recordings."
        }
        action={
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => setHealthModalOpen(true)}
              className={`flex items-center gap-2 rounded-xl px-3.5 py-2 text-xs font-bold transition-all shadow-xs border ${healthStyles.header}`}
            >
              <span className={`size-2 rounded-full ${healthStyles.dot}`} />
              {healthTitle}
              <span className="text-[11px] font-normal opacity-80">· View diagnostics</span>
            </button>
          </div>
        }
      />

      {overview.error && <ErrorBanner error={overview.error} />}

      <section aria-label="Operations summary" className="overflow-hidden rounded-2xl border border-slate-200/80 bg-white shadow-xs dark:border-slate-800/80 dark:bg-[#0c111d] dark:shadow-none">
        <div className="grid divide-y divide-slate-100 dark:divide-slate-800/80 sm:grid-cols-2 sm:divide-y-0 lg:grid-cols-6 lg:divide-x lg:divide-y-0">
          <button
            type="button"
            onClick={() => setHealthModalOpen(true)}
            className={`border-l-[3px] p-4 text-left transition-colors hover:bg-slate-50 dark:hover:bg-[#111726] sm:col-span-2 lg:col-span-1 ${healthStyles.accent}`}
          >
            <p className="text-[11px] font-semibold text-slate-500 dark:text-slate-400">System health</p>
            <p className={`mt-2 text-lg font-semibold tracking-tight ${healthStyles.title}`}>
              {healthTitle}
            </p>
            <p className="mt-1 text-[11px] text-slate-500 dark:text-slate-400">{healthHint}</p>
          </button>
          {metrics.map((metric) => (
            <div key={metric.label} className="flex min-h-[6.25rem] flex-col justify-between p-4">
              <p className="text-[11px] font-medium text-slate-500 dark:text-slate-400">{metric.label}</p>
              <p className={`mt-3 text-2xl font-semibold tabular-nums tracking-tight ${metric.alert ? 'text-rose-600 dark:text-rose-400' : 'text-slate-900 dark:text-slate-100'}`}>
                {metric.value}
              </p>
            </div>
          ))}
        </div>
      </section>

      {!isAdmin && me?.groups?.length === 0 && (
        <p role="status" className="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-xs text-amber-800 dark:border-amber-900/60 dark:bg-amber-950/30 dark:text-amber-200">
          You have no groups yet, so there is nothing to show. The admin assigns groups to each coordinator.
        </p>
      )}

      <div className="mb-6">
        <RunningNow />
      </div>

      {/* ========================================================================= */}
      {/* 2. LATEST RECORDINGS TABLE */}
      {/* ========================================================================= */}
      <Card
        title="Latest recordings"
        action={
          <Link to="/recordings" className="text-xs font-semibold text-indigo-600 hover:underline dark:text-indigo-400">
            View all →
          </Link>
        }
      >
        {latest.error ? (
          <ErrorBanner error={latest.error} />
        ) : (
          <RecordingsTable
            items={latest.data?.items}
            loading={latest.isLoading}
            onSelect={(recording) => navigate(`/recordings?recording=${recording.id}`)}
          />
        )}
      </Card>

      {/* ========================================================================= */}
      {/* 3. CONNECTION HEALTH & RECENT ACTIVITY DUAL GRID */}
      {/* ========================================================================= */}
      <div className="grid gap-6 lg:grid-cols-3">
        <Card title="Integrations">
          <div className="divide-y divide-slate-100 px-5 dark:divide-slate-800/70">
            {integrations.map((integration) => {
              const tone = integration.status === 'healthy' ? 'green' : integration.status === 'failed' ? 'red'
                : integration.status === 'checking' ? 'slate' : 'amber'
              const label = integration.status === 'healthy' ? 'Ready' : integration.status === 'failed' ? 'Unavailable'
                : integration.status === 'checking' ? 'Checking' : 'Setup needed'
              return (
                <div key={integration.name} className="flex items-center justify-between gap-3 py-3.5">
                  <div className="min-w-0">
                    <p className="text-xs font-semibold text-slate-900 dark:text-slate-100">{integration.name}</p>
                    <p className="mt-1 truncate text-[11px] text-slate-500 dark:text-slate-400">{integration.summary}</p>
                  </div>
                  <div className="flex shrink-0 items-center gap-2">
                    <Pill tone={tone}>{label}</Pill>
                    <Link to={integration.to} aria-label={`Manage ${integration.name}`} className="text-xs font-semibold text-indigo-600 hover:underline dark:text-indigo-400">Manage</Link>
                  </div>
                </div>
              )
            })}
          </div>
        </Card>

        {/* Right 2 cols: Recent Operational Activity Timeline */}
        <div className="lg:col-span-2 space-y-4">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-bold uppercase tracking-wider text-slate-400 dark:text-slate-400">
              Recent Activity Feed
            </h2>
            <Link to="/activity" className="text-xs font-semibold text-indigo-600 hover:underline dark:text-indigo-400">
              Full Audit Logs →
            </Link>
          </div>

          <div className="rounded-2xl border border-slate-200/80 bg-white p-5 shadow-xs dark:border-slate-800/80 dark:bg-[#111726] dark:shadow-none ring-1 ring-slate-900/5 dark:ring-white/[0.03]">
            {!activity.data || activity.data.items.length === 0 ? (
              <EmptyState>No operational events recorded yet.</EmptyState>
            ) : (
              <div className="relative divide-y divide-slate-100 dark:divide-slate-800/70">
                {activity.data.items.map((event) => {
                  const readable = formatHumanActivity(event.kind, event.summary, event.detail, event.outcome)
                  const isDone = event.outcome === 'done'
                  const isFail = event.outcome === 'failed'

                  return (
                    <div key={event.id} className="py-3.5 first:pt-0 last:pb-0 flex items-start gap-3">
                      <span className="mt-1 shrink-0">
                        {isDone ? (
                          <span className="inline-flex size-2 rounded-full bg-emerald-500 dark:bg-emerald-400 shadow-2xs" />
                        ) : isFail ? (
                          <span className="inline-flex size-2 rounded-full bg-rose-500 dark:bg-rose-400 shadow-2xs" />
                        ) : (
                          <span className="inline-flex size-2 rounded-full bg-amber-500 dark:bg-amber-400 shadow-2xs" />
                        )}
                      </span>

                      <div className="min-w-0 flex-1">
                        <div className="flex items-center justify-between gap-2">
                          <p className="text-xs font-bold text-slate-900 dark:text-slate-100">
                            {readable}
                          </p>
                          <span className="text-[11px] text-slate-400 shrink-0">
                            <TimeAgo iso={event.at} />
                          </span>
                        </div>
                        <div className="mt-1 flex items-center gap-2 text-[11px] text-slate-500 dark:text-slate-400">
                          {event.group && (
                            <span className="font-semibold text-slate-700 dark:text-slate-300">
                              {event.group}
                            </span>
                          )}
                          {event.group && <span>·</span>}
                          <span>{event.device || 'Cloud VPS'}</span>
                        </div>
                      </div>
                    </div>
                  )
                })}
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Global Health Check Modal */}
      <HealthModal open={healthModalOpen} onClose={() => setHealthModalOpen(false)} />
    </div>
  )
}
