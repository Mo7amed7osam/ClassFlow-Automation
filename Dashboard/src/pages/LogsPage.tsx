import { useMemo, useState } from 'react'
import { useActivity, useGroups } from '../api/hooks'
import type { Activity } from '../api/types'
import { PageHeader } from '../components/Layout'
import { Card, EmptyState, LoadingRows, Pill, StatCard, td, th } from '../components/ui'
import { formatDateTime, formatSessionDate } from '../lib/format'

const toneFor = (outcome: Activity['outcome']) => outcome === 'done' ? 'green' : outcome === 'failed' ? 'red' : 'amber'
const labelFor = (outcome: Activity['outcome']) => outcome === 'done' ? 'Done' : outcome === 'failed' ? 'Failed' : 'Skipped'

/** The workers' real activity stream. Unlike a browser console, it remains available after a worker reconnects. */
export function LogsPage() {
  const [group, setGroup] = useState('')
  const [search, setSearch] = useState('')
  const activity = useActivity({ ...(group ? { group } : {}), limit: 200 })
  const groups = useGroups()
  const rows = useMemo(() => {
    const needle = search.trim().toLowerCase()
    if (!needle) return activity.data?.items ?? []
    return (activity.data?.items ?? []).filter((item) => [item.kind, item.summary, item.group, item.device]
      .filter(Boolean).join(' ').toLowerCase().includes(needle))
  }, [activity.data, search])
  const succeeded = rows.filter((row) => row.outcome === 'done').length
  const failed = rows.filter((row) => row.outcome === 'failed').length

  return (
    <div className="space-y-5">
      <PageHeader title="Logs" description="Real activity reported by the connected workers. Updates automatically every 10 seconds." />
      <div className="grid gap-3 sm:grid-cols-3">
        <StatCard label="Events shown" value={rows.length} hint="Newest 200 worker events" tone="blue" />
        <StatCard label="Completed" value={succeeded} hint="In the current result" tone="green" />
        <StatCard label="Needs attention" value={failed} hint="Failed worker actions" tone={failed ? 'red' : 'slate'} />
      </div>
      <Card title="Worker activity">
        <div className="flex flex-wrap gap-3 border-b border-slate-100 px-5 py-3">
          <label className="sr-only" htmlFor="activity-group">Group</label>
          <select id="activity-group" value={group} onChange={(event) => setGroup(event.target.value)} className="h-9 rounded-lg border border-slate-300 bg-white px-3 text-sm text-slate-700">
            <option value="">All groups</option>
            {(groups.data?.groups ?? []).map((item) => <option key={item.id} value={item.group}>{item.displayName ?? item.group}</option>)}
          </select>
          <label className="min-w-52 flex-1"><span className="sr-only">Search logs</span><input className="h-9 w-full rounded-lg border border-slate-300 bg-white px-3 text-sm text-slate-800" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search event, worker, or group" /></label>
        </div>
        {activity.isError ? <p role="alert" className="px-5 py-6 text-sm text-rose-700">Activity could not be loaded. It will retry automatically.</p> : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[760px] border-collapse">
              <thead className="border-b border-slate-100 bg-slate-50/70"><tr><th className={th}>Time</th><th className={th}>Action</th><th className={th}>Group</th><th className={th}>Worker</th><th className={th}>Status</th></tr></thead>
              <tbody className="divide-y divide-slate-100">
                {activity.isLoading ? <LoadingRows columns={5} /> : rows.map((row) => <tr key={row.id}>
                  <td className={`${td} whitespace-nowrap text-slate-500`}><time dateTime={row.at}>{formatDateTime(row.at)}</time>{row.date && <span className="mt-0.5 block text-xs text-slate-400">{formatSessionDate(row.date)}</span>}</td>
                  <td className={td}><p className="font-medium text-slate-800">{row.summary || row.kind}</p><p className="mt-0.5 text-xs text-slate-500">{row.kind}</p></td>
                  <td className={`${td} text-slate-600`}>{row.group ?? '—'}</td>
                  <td className={`${td} text-slate-600`}>{row.device ?? 'Disconnected worker'}</td>
                  <td className={td}><Pill tone={toneFor(row.outcome)}>{labelFor(row.outcome)}</Pill></td>
                </tr>)}
              </tbody>
            </table>
            {!activity.isLoading && rows.length === 0 && <EmptyState>No worker activity matches these filters yet.</EmptyState>}
          </div>
        )}
      </Card>
    </div>
  )
}
