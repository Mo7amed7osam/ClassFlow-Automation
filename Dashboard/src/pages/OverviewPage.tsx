import { Link, useNavigate } from 'react-router'
import { useMe, useOverview, useRecordings } from '../api/hooks'
import { PageHeader } from '../components/Layout'
import { RecordingsTable } from '../components/RecordingsTable'
import { Card, ErrorBanner, StatCard } from '../components/ui'

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
