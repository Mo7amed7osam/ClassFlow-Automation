import { Fragment, useState } from 'react'
import { useAgentJobs, useAgents } from '../api/hooks'
import type { Agent, JobSummary } from '../api/types'
import { PageHeader } from '../components/Layout'
import { Card, Dot, EmptyState, ErrorBanner, jobTone, LoadingRows, Pill, TimeAgo, td, th } from '../components/ui'
import { formatDateTime, formatSessionDate } from '../lib/format'

const COLUMNS = ['Device', 'Status', 'Last heartbeat', 'Assigned jobs', 'Last 24 h', 'Version', '']

export function AgentsPage() {
  const agents = useAgents()
  const [open, setOpen] = useState<string | null>(null)
  const items = agents.data?.agents

  return (
    <>
      <PageHeader
        title="Agents"
        description={agents.data ? `${agents.data.online} of ${agents.data.count} online. A device is offline after 90 s without a heartbeat.` : 'Windows PCs connected to the backend.'}
      />
      <Card>
        {agents.error ? (
          <ErrorBanner error={agents.error} />
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-100">
              <thead className="bg-slate-50/70">
                <tr>
                  {COLUMNS.map((c) => (
                    <th key={c} scope="col" className={th}>{c}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {agents.isLoading ? (
                  <LoadingRows columns={COLUMNS.length} rows={3} />
                ) : (
                  items?.map((agent) => (
                    <Fragment key={agent.deviceId}>
                      <AgentRow agent={agent} open={open === agent.deviceId} onToggle={() => setOpen(open === agent.deviceId ? null : agent.deviceId)} />
                      {open === agent.deviceId && (
                        <tr>
                          <td colSpan={COLUMNS.length} className="bg-slate-50/60 px-4 py-3">
                            <RecentJobs deviceId={agent.deviceId} />
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  ))
                )}
              </tbody>
            </table>
            {!agents.isLoading && items?.length === 0 && <EmptyState>No devices registered yet.</EmptyState>}
          </div>
        )}
      </Card>
    </>
  )
}

function AgentRow({ agent, open, onToggle }: { agent: Agent; open: boolean; onToggle: () => void }) {
  const online = agent.status === 'online'
  return (
    <tr className="hover:bg-slate-50/60">
      <td className={td}>
        <p className="font-medium text-slate-900">{agent.name}</p>
        <p className="font-mono text-xs text-slate-400" title={agent.deviceId}>{agent.deviceId.slice(0, 8)}</p>
      </td>
      <td className={td}>
        <div className="flex flex-wrap items-center gap-1.5">
          <Pill tone={online ? 'green' : 'slate'}>
            <Dot tone={online ? 'green' : 'slate'} />
            {online ? 'Online' : 'Offline'}
          </Pill>
          {online && agent.agentState && <Pill tone={agent.agentState === 'busy' ? 'blue' : 'slate'}>{agent.agentState}</Pill>}
          {agent.revoked && <Pill tone="red">revoked</Pill>}
        </div>
      </td>
      <td className={`${td} text-slate-600`}><TimeAgo iso={agent.lastHeartbeat} /></td>
      <td className={td}>
        {agent.activeJobs.length === 0 ? (
          <span className="text-slate-400">None</span>
        ) : (
          <ul className="flex flex-col gap-1">
            {agent.activeJobs.map((job) => (
              <li key={job.jobId} className="flex items-center gap-1.5 text-sm">
                <Pill tone={jobTone(job.status)}>{job.status}</Pill>
                <span className="text-slate-700">{job.group ?? job.type}</span>
                {job.date && <span className="text-slate-400">{job.date}</span>}
              </li>
            ))}
          </ul>
        )}
      </td>
      <td className={`${td} whitespace-nowrap tabular-nums`}>
        <span className="text-emerald-700">✓ {agent.jobsLast24h.succeeded}</span>
        <span className="ml-3 text-rose-700">✗ {agent.jobsLast24h.failed}</span>
      </td>
      <td className={`${td} font-mono text-xs text-slate-500`}>{agent.version}</td>
      <td className={`${td} text-right`}>
        <button type="button" onClick={onToggle} aria-expanded={open} className="text-sm font-medium text-teal-700 hover:underline">
          {open ? 'Hide jobs' : 'Recent jobs'}
        </button>
      </td>
    </tr>
  )
}

function RecentJobs({ deviceId }: { deviceId: string }) {
  const jobs = useAgentJobs(deviceId)
  if (jobs.error) return <ErrorBanner error={jobs.error} />
  if (jobs.isLoading) return <p className="text-sm text-slate-500">Loading jobs…</p>
  if (!jobs.data?.jobs.length) return <p className="text-sm text-slate-500">This device has not run any job yet.</p>
  return (
    <table className="min-w-full text-sm">
      <thead>
        <tr className="text-left text-xs uppercase tracking-wide text-slate-500">
          <th className="py-1.5 pr-4 font-semibold">Status</th>
          <th className="py-1.5 pr-4 font-semibold">Group</th>
          <th className="py-1.5 pr-4 font-semibold">Session</th>
          <th className="py-1.5 pr-4 font-semibold">Created</th>
          <th className="py-1.5 pr-4 font-semibold">Finished</th>
          <th className="py-1.5 pr-4 font-semibold">Outcome</th>
        </tr>
      </thead>
      <tbody>
        {jobs.data.jobs.map((job) => (
          <tr key={job.jobId} className="border-t border-slate-200/70">
            <td className="py-1.5 pr-4"><Pill tone={jobTone(job.status)}>{job.status}</Pill></td>
            <td className="py-1.5 pr-4">{job.group ?? '—'}</td>
            <td className="py-1.5 pr-4 whitespace-nowrap">{formatSessionDate(job.date)}</td>
            <td className="py-1.5 pr-4 whitespace-nowrap text-slate-600">{formatDateTime(job.createdAt)}</td>
            <td className="py-1.5 pr-4 whitespace-nowrap text-slate-600">{formatDateTime(job.finishedAt)}</td>
            <td className="py-1.5 pr-4">{outcome(job)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function outcome(job: JobSummary): string {
  if (job.errorCode) return job.errorCode
  if (job.status === 'succeeded') return job.alreadyExists ? 'link was already there' : 'link attached'
  return '—'
}
