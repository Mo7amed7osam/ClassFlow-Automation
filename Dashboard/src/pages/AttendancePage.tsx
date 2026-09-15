import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { useAttendanceSessions, useCreateSession, type SessionStatus } from '../api/attendance'
import { useGroups } from '../api/hooks'
import { PageHeader } from '../components/Layout'
import { Modal } from '../components/Overlay'
import { reason } from '../components/UserModals'
import { button, Card, EmptyState, ErrorBanner, input, LoadingRows, Pill, Spinner, TimeAgo, td, th, type Tone } from '../components/ui'
import { formatSessionDate } from '../lib/format'

const COLUMNS = ['Session', 'Group', 'Status', 'Present', 'Review', 'Absent', 'Last read', '']
const STATUS_TONE: Record<SessionStatus, Tone> = { open: 'blue', closed: 'slate', finalized: 'green' }
const STATUS_LABEL: Record<SessionStatus, string> = { open: 'Live', closed: 'Ended', finalized: 'Finalized' }

/** Every class meeting whose attendance was taken, newest first. */
export function AttendancePage() {
  const [params, setParams] = useSearchParams()
  const groups = useGroups()
  const group = params.get('group') ?? ''
  const date = params.get('date') ?? ''
  const status = (params.get('status') ?? '') as SessionStatus | ''
  const page = Math.max(1, Number(params.get('page')) || 1)
  const sessions = useAttendanceSessions({ group: group || undefined, date: date || undefined, status, page, pageSize: 25 })
  const [creating, setCreating] = useState(false)
  const items = sessions.data?.items ?? []
  const pages = Math.max(1, Math.ceil((sessions.data?.total ?? 0) / 25))

  function update(changes: Record<string, string>) {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k) }
    if (!('page' in changes)) next.delete('page')
    setParams(next, { replace: true })
  }

  return (
    <>
      <PageHeader title="Attendance" description="Sessions taken by the Windows agent during the meeting, or created here by hand. Open one to review who attended."
        action={<button type="button" className={button.primary} onClick={() => setCreating(true)}>New session</button>} />
      <Card>
        <div className="flex flex-wrap items-end gap-3 border-b border-slate-100 px-5 py-4" role="search">
          <label className="flex flex-col gap-1 text-xs font-medium text-slate-500">Group
            <select aria-label="Group" className={`${input} min-w-44`} value={group} onChange={(e) => update({ group: e.target.value })}>
              <option value="">All groups</option>
              {groups.data?.groups.map((g) => <option key={g.group} value={g.group}>{g.group}</option>)}
            </select>
          </label>
          <label className="flex flex-col gap-1 text-xs font-medium text-slate-500">Date
            <input aria-label="Date" type="date" className={input} value={date} onChange={(e) => update({ date: e.target.value })} />
          </label>
          <label className="flex flex-col gap-1 text-xs font-medium text-slate-500">Status
            <select aria-label="Status" className={input} value={status} onChange={(e) => update({ status: e.target.value })}>
              <option value="">Any</option><option value="open">Live</option><option value="closed">Ended</option><option value="finalized">Finalized</option>
            </select>
          </label>
        </div>
        {sessions.error ? <ErrorBanner error={sessions.error} /> : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-100">
              <thead className="bg-slate-50/70"><tr>{COLUMNS.map((c, i) => <th key={i} scope="col" className={th}>{c}</th>)}</tr></thead>
              <tbody className="divide-y divide-slate-100">
                {sessions.isLoading ? <LoadingRows columns={COLUMNS.length} /> : items.map((s) => (
                  <tr key={s.id} className="hover:bg-slate-50/60">
                    <td className={td}>
                      <Link to={`/attendance/${s.id}`} className="font-medium text-teal-700 hover:underline">
                        {formatSessionDate(s.date)}{s.startTime ? ` · ${s.startTime}` : ''}
                      </Link>
                      {s.title && <p className="text-xs text-slate-500">{s.title}</p>}
                    </td>
                    <td className={td}>{s.group}</td>
                    <td className={td}><Pill tone={STATUS_TONE[s.status]}>{STATUS_LABEL[s.status]}</Pill></td>
                    <td className={`${td} tabular-nums`}>{s.present > 0 ? <Pill tone="green">{s.present}</Pill> : <span className="text-slate-400">0</span>}</td>
                    <td className={`${td} tabular-nums`}>{s.needsReview > 0 ? <Pill tone="amber">{s.needsReview}</Pill> : <span className="text-slate-400">0</span>}</td>
                    <td className={`${td} tabular-nums text-slate-600`}>{s.absent}</td>
                    <td className={`${td} text-slate-600`}><TimeAgo iso={s.lastCapturedAt} /></td>
                    <td className={`${td} text-right`}><Link to={`/attendance/${s.id}`} className={button.small}>Open</Link></td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!sessions.isLoading && items.length === 0 && <EmptyState>No sessions yet. They appear when the Windows agent takes attendance in a meeting, or create one by hand.</EmptyState>}
          </div>
        )}
        <footer className="flex items-center justify-between border-t border-slate-100 px-5 py-3 text-sm text-slate-600">
          <span>{sessions.data ? `${sessions.data.total} session${sessions.data.total === 1 ? '' : 's'}` : ' '}</span>
          <div className="flex items-center gap-2">
            <button type="button" className={button.small} disabled={page <= 1} onClick={() => update({ page: String(page - 1) })}>Previous</button>
            <span className="tabular-nums">Page {page} of {pages}</span>
            <button type="button" className={button.small} disabled={page >= pages} onClick={() => update({ page: String(page + 1) })}>Next</button>
          </div>
        </footer>
      </Card>
      {creating && <CreateSessionModal groups={groups.data?.groups.filter((g) => !g.archived).map((g) => g.group) ?? []} onClose={() => setCreating(false)} />}
    </>
  )
}

function CreateSessionModal({ groups, onClose }: { groups: string[]; onClose: () => void }) {
  const create = useCreateSession()
  const navigate = useNavigate()
  const [group, setGroup] = useState(groups[0] ?? '')
  const [date, setDate] = useState(new Date().toISOString().slice(0, 10))
  const [start, setStart] = useState('')
  const [title, setTitle] = useState('')

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ group, date, startTime: start || undefined, title: title.trim() || undefined },
      { onSuccess: (s) => { onClose(); navigate(`/attendance/${s.id}`) } })
  }

  return (
    <Modal open busy={create.isPending} onClose={onClose} title="New attendance session"
      description="For a meeting the agent did not record. Add the participants' names on the next page."
      footer={<>
        <button type="button" className={button.secondary} onClick={onClose} disabled={create.isPending}>Cancel</button>
        <button type="submit" form="session-form" className={button.primary} disabled={create.isPending || !group || !date}>{create.isPending && <Spinner label="Creating" />}Create</button>
      </>}>
      <form id="session-form" onSubmit={submit} className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700 sm:col-span-2">Group
          <select data-autofocus className={input} value={group} onChange={(e) => setGroup(e.target.value)}>
            {groups.map((g) => <option key={g} value={g}>{g}</option>)}
          </select></label>
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700">Date
          <input type="date" className={input} value={date} onChange={(e) => setDate(e.target.value)} /></label>
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700">Start time
          <input type="time" className={input} value={start} onChange={(e) => setStart(e.target.value)} /></label>
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700 sm:col-span-2">Title (optional)
          <input className={input} maxLength={200} value={title} onChange={(e) => setTitle(e.target.value)} /></label>
        {create.error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 sm:col-span-2">{reason(create.error)}</p>}
      </form>
    </Modal>
  )
}
