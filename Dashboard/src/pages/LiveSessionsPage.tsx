import { Link } from 'react-router'
import { useLiveSessions } from '../api/hooks'
import { PageHeader } from '../components/Layout'
import { Card, EmptyState, Pill, Spinner, StatCard, TimeAgo } from '../components/ui'
import { formatDateTime } from '../lib/format'

/**
 * The cloud equivalent of Windows' Meetings and Waiting Room pages. A class.run worker owns the
 * browser for the full meeting, while participant snapshots are saved centrally for attendance.
 */
export function LiveSessionsPage() {
  const live = useLiveSessions()
  const sessions = live.data?.items ?? []
  const running = sessions.filter((item) => item.status === 'running').length
  const queued = sessions.length - running
  const observed = sessions.reduce((total, item) => total + item.observed, 0)

  return (
    <div className="space-y-5">
      <PageHeader title="Live sessions" description="Cloud meetings being opened or monitored now. Attendance snapshots update every 10 seconds." />
      <div className="grid gap-3 sm:grid-cols-3">
        <StatCard label="Live now" value={running} hint="Workers holding a Zoom meeting" tone={running ? 'green' : 'slate'} />
        <StatCard label="Starting" value={queued} hint="Waiting for, or assigned to, a worker" tone={queued ? 'blue' : 'slate'} />
        <StatCard label="People observed" value={observed} hint="In the most recent Zoom reads" tone="blue" />
      </div>
      <Card title="Cloud meeting monitor">
        {live.isLoading ? <div className="px-5 py-10"><Spinner label="Loading live sessions" /></div> : live.isError ? <p role="alert" className="px-5 py-8 text-sm text-rose-700">Live session data could not be loaded. It will retry automatically.</p> : sessions.length === 0 ? <EmptyState>No cloud meeting is active or waiting to start.</EmptyState> : (
          <ul className="divide-y divide-slate-100">
            {sessions.map((session) => (
              <li key={session.jobId} className="grid gap-4 px-5 py-4 lg:grid-cols-[minmax(0,1fr)_auto_auto_auto] lg:items-center">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2"><span className="font-semibold text-slate-900">{session.group ?? 'Class'}</span><Pill tone={session.status === 'running' ? 'green' : 'blue'}>{session.status === 'running' ? 'Monitoring now' : 'Starting'}</Pill></div>
                  <p className="mt-1 text-sm text-slate-500">{session.worker ? `Worker: ${session.worker}` : 'Waiting for a cloud worker'} · {session.date ?? 'No date'} {session.startedAt ? `· started ${formatDateTime(session.startedAt)}` : ''}</p>
                </div>
                <div className="text-sm"><p className="font-medium text-slate-800">{session.observed} observed</p><p className="text-slate-500">{session.snapshots} attendance read{session.snapshots === 1 ? '' : 's'}</p></div>
                <div className="text-sm text-slate-500"><p>{session.durationMinutes ? `${session.durationMinutes} min session` : 'Session duration pending'}</p><p>{session.startedAt ? <TimeAgo iso={session.startedAt} /> : 'not started'}</p></div>
                {session.attendanceSessionId ? <Link className="text-sm font-semibold text-teal-700 hover:text-teal-900" to={`/attendance/${session.attendanceSessionId}`}>Open attendance →</Link> : <span className="text-sm text-slate-400">Attendance starts after the first Zoom read</span>}
              </li>
            ))}
          </ul>
        )}
      </Card>
      <p className="text-xs leading-5 text-slate-500">A live cloud meeting is intentionally ended by its worker when its planned duration finishes, so its Zoom browser and host session are closed cleanly.</p>
    </div>
  )
}
