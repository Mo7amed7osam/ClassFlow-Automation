import { Link, useNavigate } from 'react-router'
import { useMe, useOverview, useRecordings, useSessions } from '../api/hooks'
import type { SessionClass } from '../api/types'
import { PageHeader } from '../components/Layout'
import { RecordingsTable } from '../components/RecordingsTable'
import { Card, EmptyState, ErrorBanner, Pill, StatCard, td, th } from '../components/ui'

/** Today, where class times are written. */
function today(): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Africa/Cairo' }).format(new Date())
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

/**
 * The classes of today that are doing something right now, or want somebody: the meeting being held,
 * an LMS step running, a stage that failed or cannot run. It is the first thing a person wants on
 * opening the dashboard - "is anything wrong with today?" - and it links to the class itself.
 */
function RunningNow() {
  const sessions = useSessions({ from: today(), to: today() })
  const classes = sessions.data?.classes ?? []
  const live = classes.filter((item) => item.headline === 'running' || item.headline === 'needsAttention' || item.headline === 'blocked')

  return (
    <Card
      title="Today, right now"
      action={<Link to="/sessions" className="text-sm font-medium text-teal-700 hover:underline">Every class →</Link>}
    >
      {sessions.error ? <ErrorBanner error={sessions.error} /> : live.length === 0 ? (
        <EmptyState>
          {classes.length === 0 ? 'No class today.' : `${classes.length} class(es) today, none of them running or waiting for anybody.`}
        </EmptyState>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[34rem]">
            <thead className="border-b border-slate-100 bg-slate-50/60">
              <tr>
                <th className={th}>Class</th>
                <th className={th}>Starts</th>
                <th className={th}>Doing now</th>
                <th className={th}>How it is going</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {live.map((item) => {
                const now = doingNow(item)
                return (
                  <tr key={item.classPlanId} className="hover:bg-slate-50/60">
                    <td className={td}>
                      <p className="font-medium text-slate-900">{item.group}</p>
                      {item.title && <p className="text-xs text-slate-500">{item.title}</p>}
                    </td>
                    <td className={`${td} tabular-nums text-slate-700`}>{item.startTime ?? '—'}</td>
                    <td className={`${td} text-slate-700`}>{now.label}</td>
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
  const latest = useRecordings({ sort: 'updated', pageSize: 10 })
  const navigate = useNavigate()
  const { data: me } = useMe()
  const isAdmin = me?.role === 'admin'
  const o = overview.data
  const agents = o?.agents
  const jobs = o?.jobs

  return (
    <>
      <PageHeader
        title="Overview"
        description={isAdmin ? 'Agents, recordings and jobs at a glance. Refreshes on its own.' : "Your groups' recordings at a glance. Refreshes on its own."}
      />
      {overview.error && <ErrorBanner error={overview.error} />}

      <div className="mb-6 grid grid-cols-2 gap-4 lg:grid-cols-4">
        {isAdmin ? (
          <StatCard
            label="Agents online"
            value={agents ? `${agents.online} / ${agents.total}` : '…'}
            hint={agents ? `${agents.busy} busy` : undefined}
            tone={agents && agents.total > 0 && agents.online === 0 ? 'red' : 'green'}
          />
        ) : (
          <StatCard label="My groups" value={o?.groups ?? '…'} hint={o ? `${o.recordings.total} recordings` : undefined} />
        )}
        <StatCard label="Pending on LMS" value={o?.recordings.pending ?? '…'} hint={o ? `${o.recordings.total} recordings in total` : undefined} tone="amber" />
        <StatCard label="On LMS" value={o?.recordings.onLms ?? '…'} hint={o ? `${o.recordings.missingLink} without a link` : undefined} tone="green" />
        {isAdmin ? (
          <StatCard
            label="Jobs"
            value={jobs ? `${jobs.running} running` : '…'}
            hint={jobs ? `${jobs.queued} queued · ${jobs.failedLast24h} failed in 24 h` : undefined}
            tone={jobs && jobs.failedLast24h > 0 ? 'red' : 'slate'}
          />
        ) : (
          <StatCard label="Without a link" value={o?.recordings.missingLink ?? '…'} hint="Add the Drive link to attach them" tone={o && o.recordings.missingLink > 0 ? 'red' : 'slate'} />
        )}
      </div>
      {!isAdmin && me?.groups?.length === 0 && (
        <p role="status" className="mb-6 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
          You have no groups yet, so there is nothing to show. The admin assigns groups to each coordinator.
        </p>
      )}

      <div className="mb-6">
        <RunningNow />
      </div>

      <Card
        title="Latest recordings"
        action={
          <Link to="/recordings" className="text-sm font-medium text-teal-700 hover:underline">
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
    </>
  )
}
