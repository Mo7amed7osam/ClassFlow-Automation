import { useState } from 'react'
import { Link } from 'react-router'
import { useLiveSessions } from '../api/hooks'
import { HealthModal } from '../components/HealthModal'
import { PageHeader } from '../components/Layout'
import {
  IconExternalLink,
  IconRefresh,
} from '../components/Icons'
import { Card, Pill, Spinner, StatCard, TimeAgo } from '../components/ui'
import { formatCairoClock } from '../lib/translations'

export function LiveSessionsPage() {
  const live = useLiveSessions()
  const [healthModalOpen, setHealthModalOpen] = useState(false)

  const sessions = live.data?.items ?? []
  const running = sessions.filter((item) => item.status === 'running')
  const queued = sessions.filter((item) => item.status !== 'running')
  const totalObserved = sessions.reduce((total, item) => total + item.observed, 0)
  const totalSnapshots = sessions.reduce((total, item) => total + item.snapshots, 0)

  return (
    <div className="space-y-6 pb-12">
      <PageHeader
        title="Live Meeting Operations"
        description="Real-time monitoring of cloud worker browser profiles holding Zoom sessions, admitting waiting room students, and capturing presence snapshots."
        action={
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => live.refetch()}
              disabled={live.isFetching}
              className="flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-700 shadow-xs hover:bg-slate-50 dark:border-slate-800 dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700 disabled:opacity-50"
            >
              <IconRefresh className={`size-3.5 ${live.isFetching ? 'animate-spin' : ''}`} />
              Refresh Snapshot
            </button>
            <button
              type="button"
              onClick={() => setHealthModalOpen(true)}
              className="rounded-xl bg-slate-900 px-3.5 py-2 text-xs font-semibold text-white shadow-xs hover:bg-slate-800 dark:bg-slate-100 dark:text-slate-900"
            >
              Health Check
            </button>
          </div>
        }
      />

      {/* Top Stat Summary */}
      <div className="grid gap-4 sm:grid-cols-4">
        <StatCard
          label="Live now"
          value={running.length}
          hint="Workers holding Zoom meetings"
          tone={running.length > 0 ? 'green' : 'slate'}
        />
        <StatCard
          label="Starting"
          value={queued.length}
          hint="Assigned or queued for cloud worker"
          tone={queued.length > 0 ? 'blue' : 'slate'}
        />
        <StatCard
          label="People observed"
          value={totalObserved}
          hint="In most recent participant reads"
          tone="blue"
        />
        <StatCard
          label="Snapshots taken"
          value={totalSnapshots}
          hint="Total presence reads recorded"
          tone="slate"
        />
      </div>

      {/* Live Sessions Container */}
      <Card title="Active Cloud Sessions & Waiting Rooms">
        {live.isLoading ? (
          <div className="py-12 text-center">
            <Spinner label="Loading live session telemetry…" />
          </div>
        ) : live.isError ? (
          <p role="alert" className="p-8 text-sm text-rose-700 dark:text-rose-400">
            Live session telemetry could not be loaded. It will retry automatically.
          </p>
        ) : sessions.length === 0 ? (
          <div className="py-12 text-center">
            <p className="text-sm font-semibold text-slate-700 dark:text-slate-300">
              No cloud meeting is currently active or waiting to start.
            </p>
            <p className="mt-1 text-xs text-slate-500">
              Classes open automatically 15 minutes before scheduled start time.
            </p>
          </div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {sessions.map((session) => {
              const isRunning = session.status === 'running'

              return (
                <div key={session.jobId} className="p-5 space-y-4">
                  {/* Session Header */}
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                      <div className="flex items-center gap-2">
                        <span className="text-base font-bold text-slate-900 dark:text-slate-100">
                          {session.group ?? 'Class Session'}
                        </span>
                        <Pill tone={isRunning ? 'green' : 'blue'}>
                          <span className={`size-1.5 rounded-full ${isRunning ? 'bg-emerald-500 animate-pulse' : 'bg-sky-500'}`} />
                          {isRunning ? 'Meeting Live' : 'Starting Up'}
                        </Pill>
                        {session.worker && (
                          <span className="rounded bg-slate-100 px-2 py-0.5 text-[11px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                            Worker: {session.worker}
                          </span>
                        )}
                      </div>

                      <div className="mt-1 flex items-center gap-3 text-xs text-slate-500">
                        <span>Started: {session.startedAt ? formatCairoClock(session.startedAt) : 'pending'}</span>
                        <span>·</span>
                        <span>Duration: {session.durationMinutes ? `${session.durationMinutes} min` : '3 hours'}</span>
                        <span>·</span>
                        <span>Elapsed: {session.startedAt ? <TimeAgo iso={session.startedAt} /> : 'not started'}</span>
                      </div>
                    </div>

                    {session.meetingUrl && (
                      <a
                        href={session.meetingUrl}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="inline-flex items-center gap-1.5 rounded-xl border border-slate-200 px-3 py-1.5 text-xs font-semibold text-indigo-600 hover:bg-slate-50 dark:border-slate-700 dark:text-indigo-400 dark:hover:bg-slate-800"
                      >
                        Open Zoom in Browser
                        <IconExternalLink className="size-3" />
                      </a>
                    )}
                  </div>

                  {/* Telemetry Grid: Mapped 100% to real backend API fields */}
                  <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6 text-xs">
                    <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-3 dark:border-slate-800 dark:bg-slate-800/40">
                      <span className="text-slate-500 text-[11px] block">Observed in Snapshot</span>
                      <span className="text-base font-bold text-slate-900 dark:text-slate-100 mt-0.5 block">
                        {session.observed}
                      </span>
                    </div>

                    <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-3 dark:border-slate-800 dark:bg-slate-800/40">
                      <span className="text-slate-500 text-[11px] block">Snapshots Captured</span>
                      <span className="text-base font-bold text-slate-900 dark:text-slate-100 mt-0.5 block">
                        {session.snapshots}
                      </span>
                    </div>

                    <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-3 dark:border-slate-800 dark:bg-slate-800/40">
                      <span className="text-slate-500 text-[11px] block">Worker Device</span>
                      <span className="text-base font-bold text-slate-900 dark:text-slate-100 mt-0.5 block truncate">
                        {session.worker || 'Pending'}
                      </span>
                    </div>

                    <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-3 dark:border-slate-800 dark:bg-slate-800/40">
                      <span className="text-slate-500 text-[11px] block">Attendance Session</span>
                      <span className="text-base font-bold text-slate-900 dark:text-slate-100 mt-0.5 block">
                        {session.attendanceStatus ? session.attendanceStatus.toUpperCase() : 'None'}
                      </span>
                    </div>

                    <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-3 dark:border-slate-800 dark:bg-slate-800/40">
                      <span className="text-slate-500 text-[11px] block">Job State</span>
                      <span className="text-base font-bold text-indigo-600 dark:text-indigo-400 mt-0.5 block uppercase">
                        {session.status}
                      </span>
                    </div>

                    <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-3 dark:border-slate-800 dark:bg-slate-800/40">
                      <span className="text-slate-500 text-[11px] block">Planned Duration</span>
                      <span className="text-base font-bold text-slate-900 dark:text-slate-100 mt-0.5 block">
                        {session.durationMinutes ? `${session.durationMinutes}m` : '—'}
                      </span>
                    </div>
                  </div>

                  {/* Operational Controls & Direct Links */}
                  <div className="flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-3 dark:border-slate-800">
                    <div className="flex items-center gap-2">
                      <button
                        type="button"
                        onClick={() => live.refetch()}
                        disabled={live.isFetching}
                        className="rounded-xl border border-slate-200 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700 disabled:opacity-50"
                      >
                        Refresh Telemetry
                      </button>

                      <button
                        type="button"
                        onClick={() => setHealthModalOpen(true)}
                        className="rounded-xl border border-slate-200 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700"
                      >
                        Run Diagnostics
                      </button>
                    </div>

                    <div>
                      {session.attendanceSessionId ? (
                        <Link
                          to={`/attendance/${session.attendanceSessionId}`}
                          className="font-bold text-xs text-indigo-600 hover:underline dark:text-indigo-400"
                        >
                          Open Attendance Review Workspace →
                        </Link>
                      ) : (
                        <span className="text-xs text-slate-400">
                          Attendance workspace creates upon first participant snapshot
                        </span>
                      )}
                    </div>
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </Card>

      <p className="text-xs leading-5 text-slate-500">
        A live cloud meeting is intentionally ended by its worker when its planned duration finishes, so its Zoom browser profile and host session are closed cleanly.
      </p>

      <HealthModal open={healthModalOpen} onClose={() => setHealthModalOpen(false)} />
    </div>
  )
}
