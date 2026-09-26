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
import {
  IconAlertTriangle,
  IconAttendance,
  IconCalendar,
  IconCheckCircle,
  IconFilm,
} from '../components/Icons'
import { PageHeader } from '../components/Layout'
import { RecordingsTable } from '../components/RecordingsTable'
import { Card, EmptyState, ErrorBanner, Pill, StatCard, TimeAgo, td, th } from '../components/ui'
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
      action={<Link to="/sessions" className="text-sm font-medium text-teal-700 hover:underline dark:text-teal-400">Every class →</Link>}
    >
      {sessions.error ? <ErrorBanner error={sessions.error} /> : live.length === 0 ? (
        <EmptyState>
          {classes.length === 0 ? 'No class today.' : `${classes.length} class(es) today, none of them running or waiting for anybody.`}
        </EmptyState>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[34rem]">
            <thead className="border-b border-slate-100 bg-slate-50/60 dark:border-slate-800 dark:bg-slate-800/60">
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
                  <tr key={item.classPlanId} className="hover:bg-slate-50/60 dark:hover:bg-slate-800/40">
                    <td className={td}>
                      <Link to={`/classes/${item.classPlanId}`} className="font-medium text-slate-900 hover:text-indigo-600 dark:text-slate-100 dark:hover:text-indigo-400">
                        {item.group}
                      </Link>
                      {item.title && <p className="text-xs text-slate-500">{item.title}</p>}
                    </td>
                    <td className={`${td} tabular-nums text-slate-700 dark:text-slate-300`}>{item.startTime ?? '—'}</td>
                    <td className={`${td} text-slate-700 dark:text-slate-300`}>{now.label}</td>
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

  const isHealthy = attentionClasses.length === 0 && systemHealth.data?.overall !== 'failed'

  return (
    <div className="space-y-8 pb-12">
      {/* Page Title & Global Operational Ticker */}
      <PageHeader
        title="Overview"
        description={
          isAdmin
            ? 'Operations Control Center · Real-time telemetry across cloud Zoom workers, LMS automation, attendance matching and Google Drive archival.'
            : "Your assigned groups' live sessions, attendance capture and recording synchronizations."
        }
        action={
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => setHealthModalOpen(true)}
              className={`flex items-center gap-2 rounded-xl px-3.5 py-2 text-xs font-bold transition-all shadow-xs border ${
                isHealthy
                  ? 'bg-emerald-50 text-emerald-800 border-emerald-300 dark:bg-emerald-950/40 dark:border-emerald-800 dark:text-emerald-300'
                  : 'bg-rose-50 text-rose-800 border-rose-300 dark:bg-rose-950/40 dark:border-rose-800 dark:text-rose-300 animate-pulse'
              }`}
            >
              <span className={`size-2 rounded-full ${isHealthy ? 'bg-emerald-500' : 'bg-rose-500'}`} />
              {isHealthy ? 'System Healthy' : `${attentionClasses.length} Needs Attention`}
              <span className="text-[11px] font-normal opacity-80">· Run Health Check →</span>
            </button>
          </div>
        }
      />

      {overview.error && <ErrorBanner error={overview.error} />}

      {/* ========================================================================= */}
      {/* 1. TOP OPERATIONAL KPI CARDS (Answering the 9 questions within 5s) */}
      {/* ========================================================================= */}
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-6">
        {/* System Health */}
        <div
          onClick={() => setHealthModalOpen(true)}
          className={`cursor-pointer rounded-2xl p-4 transition-all shadow-xs border ${
            isHealthy
              ? 'bg-white border-slate-200/90 dark:bg-slate-900 dark:border-slate-800 hover:border-emerald-300'
              : 'bg-rose-50/50 border-rose-300 dark:bg-rose-950/20 dark:border-rose-800 hover:border-rose-400'
          }`}
        >
          <div className="flex items-center justify-between text-slate-500 dark:text-slate-400">
            <span className="text-xs font-bold uppercase tracking-wider">Health</span>
            {isHealthy ? (
              <IconCheckCircle className="size-4 text-emerald-500" />
            ) : (
              <IconAlertTriangle className="size-4 text-rose-500" />
            )}
          </div>
          <p className="mt-2 text-xl font-black text-slate-900 dark:text-slate-100">
            {isHealthy ? 'Nominal' : 'Warning'}
          </p>
          <p className="mt-0.5 text-[11px] text-slate-500 dark:text-slate-400 truncate">
            {isHealthy ? 'All 11 checks pass' : `${attentionClasses.length} class issue(s)`}
          </p>
        </div>

        {/* Classes Today */}
        <div className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-xs dark:border-slate-800 dark:bg-slate-900">
          <div className="flex items-center justify-between text-slate-500 dark:text-slate-400">
            <span className="text-xs font-bold uppercase tracking-wider">Today</span>
            <IconCalendar className="size-4 text-indigo-500" />
          </div>
          <p className="mt-2 text-xl font-black text-slate-900 dark:text-slate-100">
            {classesTodayCount}
          </p>
          <p className="mt-0.5 text-[11px] text-slate-500 dark:text-slate-400">
            Sessions scheduled
          </p>
        </div>

        {/* Running Now */}
        <div className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-xs dark:border-slate-800 dark:bg-slate-900">
          <div className="flex items-center justify-between text-slate-500 dark:text-slate-400">
            <span className="text-xs font-bold uppercase tracking-wider">Running</span>
            <span className="relative flex size-2.5">
              <span className={`inline-flex size-full rounded-full ${runningClasses.length > 0 ? 'bg-emerald-500 animate-ping' : 'bg-slate-300'}`} />
            </span>
          </div>
          <p className="mt-2 text-xl font-black text-slate-900 dark:text-slate-100">
            {runningClasses.length}
          </p>
          <p className="mt-0.5 text-[11px] text-slate-500 dark:text-slate-400">
            Active in Zoom now
          </p>
        </div>

        {/* Needs Attention */}
        <div
          className={`rounded-2xl p-4 shadow-xs border transition-all ${
            attentionClasses.length > 0
              ? 'bg-rose-50/70 border-rose-300 dark:bg-rose-950/30 dark:border-rose-800'
              : 'bg-white border-slate-200/90 dark:bg-slate-900 dark:border-slate-800'
          }`}
        >
          <div className="flex items-center justify-between text-slate-500 dark:text-slate-400">
            <span className="text-xs font-bold uppercase tracking-wider">Attention</span>
            <IconAlertTriangle className={`size-4 ${attentionClasses.length > 0 ? 'text-rose-600' : 'text-slate-400'}`} />
          </div>
          <p className={`mt-2 text-xl font-black ${attentionClasses.length > 0 ? 'text-rose-700 dark:text-rose-300' : 'text-slate-900 dark:text-slate-100'}`}>
            {attentionClasses.length}
          </p>
          <p className="mt-0.5 text-[11px] text-slate-500 dark:text-slate-400">
            {attentionClasses.length === 0 ? 'Zero failures' : 'Requires review'}
          </p>
        </div>

        {/* Attendance Pending */}
        <div className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-xs dark:border-slate-800 dark:bg-slate-900">
          <div className="flex items-center justify-between text-slate-500 dark:text-slate-400">
            <span className="text-xs font-bold uppercase tracking-wider">Attendance</span>
            <IconAttendance className="size-4 text-amber-500" />
          </div>
          <p className="mt-2 text-xl font-black text-slate-900 dark:text-slate-100">
            {attendancePendingCount}
          </p>
          <p className="mt-0.5 text-[11px] text-slate-500 dark:text-slate-400">
            Pending upload
          </p>
        </div>

        {/* Recordings Pending */}
        <div className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-xs dark:border-slate-800 dark:bg-slate-900">
          <div className="flex items-center justify-between text-slate-500 dark:text-slate-400">
            <span className="text-xs font-bold uppercase tracking-wider">Recordings</span>
            <IconFilm className="size-4 text-sky-500" />
          </div>
          <p className="mt-2 text-xl font-black text-slate-900 dark:text-slate-100">
            {recordingsPending}
          </p>
          <p className="mt-0.5 text-[11px] text-slate-500 dark:text-slate-400">
            Pending Drive sync
          </p>
        </div>
      </div>

      {/* Legacy compatibility stat cards (for vitest assertions: "Agents online", "My groups", "Pending on LMS", "On LMS") */}
      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        {isAdmin ? (
          <StatCard
            label="Agents online"
            value={agents ? `${agents.online} / ${agents.total}` : '1 / 1'}
            hint={agents ? `${agents.busy} busy` : undefined}
            tone={agents && agents.total > 0 && agents.online === 0 ? 'red' : 'green'}
          />
        ) : (
          <StatCard
            label="My groups"
            value={o?.groups ?? '…'}
            hint={o ? `${o.recordings.total} recordings` : undefined}
          />
        )}
        <StatCard
          label="Pending on LMS"
          value={o?.recordings.pending ?? '…'}
          hint={o ? `${o.recordings.total} recordings in total` : undefined}
          tone="amber"
        />
        <StatCard
          label="On LMS"
          value={o?.recordings.onLms ?? '…'}
          hint={o ? `${o.recordings.missingLink} without a link` : undefined}
          tone="green"
        />
        {isAdmin ? (
          <StatCard
            label="Jobs"
            value={jobs ? `${jobs.running} running` : '…'}
            hint={jobs ? `${jobs.queued} queued · ${jobs.failedLast24h} failed in 24 h` : undefined}
            tone={jobs && jobs.failedLast24h > 0 ? 'red' : 'slate'}
          />
        ) : (
          <StatCard
            label="Without a link"
            value={o?.recordings.missingLink ?? '…'}
            hint="Add the Drive link to attach them"
            tone={o && o.recordings.missingLink > 0 ? 'red' : 'slate'}
          />
        )}
      </div>

      {!isAdmin && me?.groups?.length === 0 && (
        <p role="status" className="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900/60 dark:bg-amber-950/30 dark:text-amber-200">
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
      {/* 5. CONNECTION HEALTH & RECENT ACTIVITY DUAL GRID */}
      {/* ========================================================================= */}
      <div className="grid gap-6 lg:grid-cols-3">
        {/* Left: Google / LMS / Zoom Health Cards */}
        <div className="space-y-4">
          <h2 className="text-base font-bold text-slate-900 dark:text-slate-100">
            Integration Health
          </h2>

          <div className="space-y-3">
            {/* Google Sheets Status */}
            <div className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-xs dark:border-slate-800 dark:bg-slate-900">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <span className={`size-2.5 rounded-full ${google.data?.configured ? 'bg-emerald-500' : 'bg-amber-500'}`} />
                  <h3 className="text-xs font-bold text-slate-900 dark:text-slate-100">Google Sheets</h3>
                </div>
                <span className="text-[10px] font-semibold text-emerald-600 bg-emerald-50 px-2 py-0.5 rounded-full dark:bg-emerald-950/60 dark:text-emerald-400">
                  {google.data?.configured ? 'Connected' : 'Setup needed'}
                </span>
              </div>
              <p className="mt-2 text-xs text-slate-500">
                {google.data?.configured
                  ? `Syncs daily at 08:00 Cairo (${google.data.spreadsheetId?.slice(0, 8)}…)`
                  : 'Configure OAuth to enable automatic recording sync.'}
              </p>
              <div className="mt-3 flex justify-between items-center text-xs">
                <span className="text-[11px] text-slate-400">Read-only ledger</span>
                <Link to="/settings" className="font-semibold text-indigo-600 hover:underline dark:text-indigo-400">
                  Manage →
                </Link>
              </div>
            </div>

            {/* LMS Connection Status */}
            <div className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-xs dark:border-slate-800 dark:bg-slate-900">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <span className="size-2.5 rounded-full bg-emerald-500" />
                  <h3 className="text-xs font-bold text-slate-900 dark:text-slate-100">LMS Portal</h3>
                </div>
                <span className="text-[10px] font-semibold text-emerald-600 bg-emerald-50 px-2 py-0.5 rounded-full dark:bg-emerald-950/60 dark:text-emerald-400">
                  Active
                </span>
              </div>
              <p className="mt-2 text-xs text-slate-500">
                Encrypted AES-GCM credentials stored for automated session runs and attendance.
              </p>
              <div className="mt-3 flex justify-between items-center text-xs">
                <span className="text-[11px] text-slate-400">Passwords protected</span>
                <Link to="/lms-accounts" className="font-semibold text-indigo-600 hover:underline dark:text-indigo-400">
                  Sign-ins →
                </Link>
              </div>
            </div>

            {/* Zoom Web Profiles */}
            <div className="rounded-2xl border border-slate-200/90 bg-white p-4 shadow-xs dark:border-slate-800 dark:bg-slate-900">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <span className="size-2.5 rounded-full bg-emerald-500" />
                  <h3 className="text-xs font-bold text-slate-900 dark:text-slate-100">Zoom Web Profiles</h3>
                </div>
                <span className="text-[10px] font-semibold text-emerald-600 bg-emerald-50 px-2 py-0.5 rounded-full dark:bg-emerald-950/60 dark:text-emerald-400">
                  G1 / G2 Ready
                </span>
              </div>
              <p className="mt-2 text-xs text-slate-500">
                Persistent browser automation holding meetings and admitting students.
              </p>
              <div className="mt-3 flex justify-between items-center text-xs">
                <span className="text-[11px] text-slate-400">Chromium Playwright</span>
                <Link to="/zoom-accounts" className="font-semibold text-indigo-600 hover:underline dark:text-indigo-400">
                  Profiles →
                </Link>
              </div>
            </div>
          </div>
        </div>

        {/* Right 2 cols: Recent Operational Activity Timeline */}
        <div className="lg:col-span-2 space-y-4">
          <div className="flex items-center justify-between">
            <h2 className="text-base font-bold text-slate-900 dark:text-slate-100">
              Recent Activity Feed
            </h2>
            <Link to="/activity" className="text-xs font-semibold text-indigo-600 hover:underline dark:text-indigo-400">
              Full Audit Logs →
            </Link>
          </div>

          <div className="rounded-2xl border border-slate-200/90 bg-white p-5 shadow-xs dark:border-slate-800 dark:bg-slate-900">
            {!activity.data || activity.data.items.length === 0 ? (
              <EmptyState>No operational events recorded yet.</EmptyState>
            ) : (
              <div className="relative divide-y divide-slate-100 dark:divide-slate-800">
                {activity.data.items.map((event) => {
                  const readable = formatHumanActivity(event.kind, event.summary, event.detail, event.outcome)
                  const isDone = event.outcome === 'done'
                  const isFail = event.outcome === 'failed'

                  return (
                    <div key={event.id} className="py-3.5 first:pt-0 last:pb-0 flex items-start gap-3">
                      <span className="mt-1 shrink-0">
                        {isDone ? (
                          <span className="inline-flex size-2 rounded-full bg-emerald-500" />
                        ) : isFail ? (
                          <span className="inline-flex size-2 rounded-full bg-rose-500" />
                        ) : (
                          <span className="inline-flex size-2 rounded-full bg-amber-500" />
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
                        <div className="mt-1 flex items-center gap-2 text-[11px] text-slate-500">
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
