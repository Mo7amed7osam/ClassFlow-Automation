import { useMemo, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router'
import {
  exportUrl,
  useAddParticipants,
  useCorrectRecord,
  useFinalizeSession,
  useIgnoreParticipant,
  useRematch,
  useSessionDetails,
  type AttendanceRecordView,
  type AttendanceStatus,
  type ParticipantView,
  type SessionDetails,
} from '../api/attendance'
import { PageHeader } from '../components/Layout'
import { Modal } from '../components/Overlay'
import { useToast } from '../components/Toast'
import { ConfirmModal, reason } from '../components/UserModals'
import { button, Card, EmptyState, ErrorBanner, input, LoadingRows, Pill, Spinner, StatCard, TimeAgo, td, th, type Tone } from '../components/ui'
import { CAIRO, formatSessionDate } from '../lib/format'

const STATUS: Record<AttendanceStatus, { tone: Tone; label: string }> = {
  present: { tone: 'green', label: 'Present' },
  needs_review: { tone: 'amber', label: 'Needs review' },
  absent: { tone: 'slate', label: 'Absent' },
}
const SOURCE: Record<string, string> = {
  exact: 'Exact name', alias: 'Roster alias', memory: 'Remembered', rule: 'Name rules', fuzzy: 'Similar spelling',
  ai: 'AI', manual: 'By hand', none: '',
}
const clock = new Intl.DateTimeFormat('en-GB', { timeZone: CAIRO, hour: '2-digit', minute: '2-digit' })
export const hhmm = (iso: string | null) => (iso ? clock.format(new Date(iso)) : '—')
export const minutes = (seconds: number | null) => (seconds === null ? '—' : `${Math.round(seconds / 60)} min`)

type Tab = 'students' | 'review' | 'names'

/** One session: who attended, the matching behind it, and the corrections. */
export function AttendanceSessionPage() {
  const { id = '' } = useParams()
  const details = useSessionDetails(id)
  const rematch = useRematch()
  const finalize = useFinalizeSession()
  const toast = useToast()
  const [tab, setTab] = useState<Tab>('students')
  const [editing, setEditing] = useState<AttendanceRecordView | null>(null)
  const [adding, setAdding] = useState(false)
  const [confirmFinal, setConfirmFinal] = useState(false)
  const d = details.data

  if (details.error) return <><PageHeader title="Attendance" /><Card><ErrorBanner error={details.error} /></Card></>
  if (!d) return <><PageHeader title="Attendance" /><Card><table className="min-w-full"><tbody><LoadingRows columns={6} /></tbody></table></Card></>

  const s = d.session
  const locked = s.status === 'finalized'
  const reviewCount = d.summary.needsReview + d.summary.unmatched

  return (
    <>
      <p className="mb-2 text-sm"><Link to="/attendance" className="text-teal-700 hover:underline">← Attendance</Link></p>
      <PageHeader
        title={`${s.group} · ${formatSessionDate(s.date)}${s.startTime ? ` · ${s.startTime}` : ''}`}
        description={`${s.title ? `${s.title} · ` : ''}${d.snapshots.count} read${d.snapshots.count === 1 ? '' : 's'} of the Zoom list${d.snapshots.lastCapturedAt ? ', last ' : ''}`}
        action={
          <div className="flex flex-wrap gap-2">
            <button type="button" className={button.secondary} disabled={locked || rematch.isPending} onClick={() => rematch.mutate({ id, useAi: false })}>
              {rematch.isPending && <Spinner label="Matching" />}Match again
            </button>
            {d.aiAvailable && (
              <button type="button" className={button.secondary} disabled={locked || rematch.isPending}
                onClick={() => rematch.mutate({ id, useAi: true }, { onSuccess: (r) => toast.success('AI matching done', `${r.aiSuggestions?.length ?? 0} suggestion(s) to review.`), onError: (e) => toast.error('AI matching failed', reason(e)) })}>
                Match with AI
              </button>
            )}
            <button type="button" className={button.secondary} disabled={locked} onClick={() => setAdding(true)}>Add names</button>
            <a className={button.secondary} href={exportUrl(id)} download>Export CSV</a>
            <button type="button" className={locked ? button.secondary : button.primary} onClick={() => setConfirmFinal(true)}>{locked ? 'Reopen' : 'Finalize'}</button>
          </div>
        }
      />
      {d.snapshots.lastCapturedAt && <p className="-mt-3 mb-4 text-xs text-slate-500">Last read <TimeAgo iso={d.snapshots.lastCapturedAt} />. {s.status === 'open' ? 'Still live: this page refreshes itself.' : ''}</p>}
      {locked && <p role="status" className="mb-4 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800">Finalized. Reopen it to change anything.</p>}

      <div className="mb-5 grid grid-cols-2 gap-4 lg:grid-cols-4">
        <StatCard label="Present" value={d.summary.present} hint={`of ${d.summary.students} students`} tone="green" />
        <StatCard label="Needs review" value={d.summary.needsReview} tone={d.summary.needsReview ? 'amber' : 'slate'} />
        <StatCard label="Absent" value={d.summary.absent} />
        <StatCard label="Unmatched Zoom names" value={d.summary.unmatched} hint="Not given to any student" tone={d.summary.unmatched ? 'amber' : 'slate'} />
      </div>

      {d.aiSuggestions && d.aiSuggestions.length > 0 && <AiSuggestions details={d} locked={locked} />}

      <Card>
        <div className="flex gap-1.5 border-b border-slate-100 px-4 py-3" role="tablist" aria-label="View">
          {([['students', `Students (${d.summary.students})`], ['review', `Review (${reviewCount})`], ['names', `Zoom names (${d.participants.length})`]] as [Tab, string][]).map(([key, label]) => (
            <button key={key} type="button" role="tab" aria-selected={tab === key} onClick={() => setTab(key)}
              className={`rounded-full px-3 py-1 text-sm ${tab === key ? 'bg-slate-900 text-white' : 'text-slate-600 hover:bg-slate-100'}`}>{label}</button>
          ))}
        </div>
        {tab === 'names' ? <NamesTable details={d} locked={locked} /> : (
          <RecordsTable details={d} locked={locked} onlyReview={tab === 'review'} onEdit={setEditing} />
        )}
        {tab === 'review' && <UnmatchedNames details={d} locked={locked} />}
      </Card>

      {editing && <RecordModal details={d} record={editing} onClose={() => setEditing(null)} />}
      {adding && <AddNamesModal sessionId={id} onClose={() => setAdding(false)} />}
      {confirmFinal && (
        <ConfirmModal
          title={locked ? 'Reopen this session?' : 'Finalize this session?'}
          body={locked ? <p>It can be corrected again, and new reads from the agent will change it.</p>
            : <p>The attendance is locked as it is now{d.summary.needsReview ? <>, with <strong>{d.summary.needsReview}</strong> still marked “needs review”</> : ''}. You can reopen it later.</p>}
          confirm={locked ? 'Reopen' : 'Finalize'} busy={finalize.isPending} error={finalize.error}
          onClose={() => { finalize.reset(); setConfirmFinal(false) }}
          onConfirm={() => finalize.mutate({ id, reopen: locked }, { onSuccess: () => { setConfirmFinal(false); toast.success(locked ? 'Reopened' : 'Finalized') } })}
        />
      )}
    </>
  )
}

function RecordsTable({ details, locked, onlyReview, onEdit }: { details: SessionDetails; locked: boolean; onlyReview: boolean; onEdit: (r: AttendanceRecordView) => void }) {
  const correct = useCorrectRecord()
  const toast = useToast()
  const rows = onlyReview ? details.records.filter((r) => r.status === 'needs_review') : details.records
  const columns = ['#', 'Student', 'Status', 'Zoom name', 'Confidence', 'First Observed', 'Last Observed', 'Observation Count', '']
  const quick = (r: AttendanceRecordView, status: AttendanceStatus) =>
    correct.mutate({ id: details.session.id, studentId: r.studentId, status }, {
      onSuccess: () => toast.success(status === 'present' ? `${r.fullName}: present` : `${r.fullName}: absent`, 'Remembered for next time.'),
      onError: (e) => toast.error('Could not save', reason(e)),
    })

  return (
    <div className="overflow-x-auto">
      <table className="min-w-full divide-y divide-slate-100 dark:divide-slate-800">
        <thead className="bg-slate-50/70 dark:bg-[#0c111d]"><tr>{columns.map((c, i) => <th key={i} scope="col" className={th}>{c}</th>)}</tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
          {rows.map((r) => {
            const p = r.participant ? details.participants.find((item) => item.id === r.participant?.id) : undefined
            return (
              <tr key={r.studentId} className="hover:bg-slate-50/60 dark:hover:bg-[#161d2f]/40 transition-colors">
                <td className={`${td} tabular-nums text-slate-500 dark:text-slate-400`}>{r.order ?? ''}</td>
                <td className={td}><span className="font-medium text-slate-900 dark:text-slate-100">{r.fullName}</span>{r.reason && r.status !== 'absent' && <p className="max-w-72 text-xs text-slate-500 dark:text-slate-400">{r.reason}</p>}</td>
                <td className={td}>
                  <Pill tone={STATUS[r.status].tone}>{STATUS[r.status].label}</Pill>
                  {r.manual && <span className="ml-1 text-xs text-slate-500 dark:text-slate-400" title="Set by hand; re-matching keeps it">✎</span>}
                </td>
                <td className={td}>
                  {r.participant ? <span className="text-slate-800 dark:text-slate-200">{r.participant.name}</span> : <span className="text-slate-400">—</span>}
                  {r.extraNames.length > 0 && <p className="text-xs text-slate-500">also {r.extraNames.join(', ')}</p>}
                </td>
                <td className={`${td} whitespace-nowrap text-slate-600`}>
                  {r.status === 'absent' && !r.participant ? '' : <>{r.confidence}% <span className="text-xs text-slate-400">{SOURCE[r.source] ?? r.source}</span></>}
                </td>
                <td className={`${td} tabular-nums`}>{hhmm(r.joinTime)}</td>
                <td className={`${td} tabular-nums`}>{hhmm(r.leaveTime)}</td>
                <td className={`${td} tabular-nums`}>
                  {p ? `${p.sightings} snapshots` : '—'}
                </td>
              <td className={`${td} text-right`}>
                <div className="flex justify-end gap-1.5">
                  {r.status === 'needs_review' && !locked && (
                    <>
                      <button type="button" className={button.smallPrimary} disabled={correct.isPending} onClick={() => quick(r, 'present')} aria-label={`Confirm ${r.fullName}`}>Confirm</button>
                      <button type="button" className={button.small} disabled={correct.isPending} onClick={() => quick(r, 'absent')} aria-label={`Not ${r.fullName}`}>Not them</button>
                    </>
                  )}
                  <button type="button" className={button.small} disabled={locked} onClick={() => onEdit(r)} aria-label={`Change ${r.fullName}`}>Change</button>
                </div>
              </td>
            </tr>
          )
        })}
        </tbody>
      </table>
      {rows.length === 0 && <EmptyState>{onlyReview ? 'Nothing needs a decision.' : 'No students on this group’s roster yet. Add them on the Students page.'}</EmptyState>}
    </div>
  )
}

function UnmatchedNames({ details, locked }: { details: SessionDetails; locked: boolean }) {
  const correct = useCorrectRecord()
  const ignore = useIgnoreParticipant()
  const names = details.participants.filter((p) => !p.ignored && !p.assignedTo)
  const studentName = useMemo(() => new Map(details.records.map((r) => [r.studentId, r.fullName])), [details.records])
  if (names.length === 0) return null
  return (
    <div className="border-t border-slate-100 px-5 py-4">
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Zoom names nobody has</h3>
      <ul className="divide-y divide-slate-100 rounded-lg border border-slate-200">
        {names.map((p) => {
          const best = p.candidates[0]
          return (
            <li key={p.id} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2 text-sm">
              <span><span className="font-medium text-slate-800">{p.name}</span> <span className="text-xs text-slate-500">({p.sightings} snapshots)</span></span>
              <span className="flex flex-wrap gap-1.5">
                {best && !locked && (
                  <button type="button" className={button.smallPrimary} disabled={correct.isPending}
                    onClick={() => correct.mutate({ id: details.session.id, studentId: best.studentId, participantId: p.id })}
                    aria-label={`Give ${p.name} to ${studentName.get(best.studentId)}`}>
                    It’s {studentName.get(best.studentId)} ({best.score}%)
                  </button>
                )}
                {!locked && <button type="button" className={button.small} disabled={ignore.isPending} onClick={() => ignore.mutate({ id: details.session.id, participantId: p.id, ignored: true })}>Not a student</button>}
              </span>
            </li>
          )
        })}
      </ul>
    </div>
  )
}

function NamesTable({ details, locked }: { details: SessionDetails; locked: boolean }) {
  const ignore = useIgnoreParticipant()
  const studentName = useMemo(() => new Map(details.records.map((r) => [r.studentId, r.fullName])), [details.records])
  const columns = ['Zoom name', 'First Observed', 'Last Observed', 'Observation Count', 'Student', '']
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full divide-y divide-slate-100 dark:divide-slate-800">
        <thead className="bg-slate-50/70 dark:bg-[#0c111d]"><tr>{columns.map((c, i) => <th key={i} scope="col" className={th}>{c}</th>)}</tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
          {details.participants.map((p: ParticipantView) => (
            <tr key={p.id} className={`${p.ignored ? 'opacity-50' : ''} hover:bg-slate-50/60 dark:hover:bg-[#161d2f]/40 transition-colors`}>
              <td className={td}><span className="font-medium text-slate-800 dark:text-slate-200">{p.name}</span>{p.ignored && <span className="ml-2"><Pill tone="slate">Not a student</Pill></span>}</td>
              <td className={`${td} tabular-nums text-slate-600 dark:text-slate-400`}>{hhmm(p.firstSeenAt)}</td>
              <td className={`${td} tabular-nums text-slate-600 dark:text-slate-400`}>{hhmm(p.lastSeenAt)}</td>
              <td className={`${td} tabular-nums text-slate-600 dark:text-slate-400`}>{p.sightings} snapshots</td>
              <td className={td}>{p.assignedTo ? <span className="text-slate-800 dark:text-slate-200">{studentName.get(p.assignedTo)}</span> : <span className="text-slate-400">—</span>}</td>
              <td className={`${td} text-right`}>
                <button type="button" className={button.small} disabled={locked || ignore.isPending}
                  onClick={() => ignore.mutate({ id: details.session.id, participantId: p.id, ignored: !p.ignored })}>
                  {p.ignored ? 'Include' : 'Not a student'}
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {details.participants.length === 0 && <EmptyState>No Zoom names yet. They arrive from the Windows agent during the meeting, or add them by hand.</EmptyState>}
    </div>
  )
}

function RecordModal({ details, record, onClose }: { details: SessionDetails; record: AttendanceRecordView; onClose: () => void }) {
  const correct = useCorrectRecord()
  const toast = useToast()
  const [choice, setChoice] = useState(record.participant?.id ?? '')
  const [remember, setRemember] = useState(true)
  const owner = useMemo(() => new Map(details.records.filter((r) => r.participant).map((r) => [r.participant!.id, r.fullName])), [details.records])
  const names = details.participants.filter((p) => !p.ignored)
  const save = (body: { participantId?: string | null; status?: AttendanceStatus; reset?: boolean }) =>
    correct.mutate({ id: details.session.id, studentId: record.studentId, remember, ...body }, {
      onSuccess: () => { toast.success(`Saved ${record.fullName}`); onClose() },
    })

  return (
    <Modal open busy={correct.isPending} onClose={onClose} title={`Change ${record.fullName}`}
      description="A change made here is kept when the session is matched again."
      footer={<>
        <button type="button" className={button.secondary} onClick={() => save({ reset: true })} disabled={correct.isPending || !record.manual}>Back to automatic</button>
        <button type="button" className={button.secondary} onClick={() => save({ status: 'absent' })} disabled={correct.isPending}>Absent</button>
        <button type="button" className={button.secondary} onClick={() => save({ status: 'present', participantId: undefined })} disabled={correct.isPending}>Present without a Zoom name</button>
        <button type="button" className={button.primary} onClick={() => save({ participantId: choice })} disabled={correct.isPending || !choice}>{correct.isPending && <Spinner label="Saving" />}Use this name</button>
      </>}>
      <form onSubmit={(e: FormEvent) => { e.preventDefault(); if (choice) save({ participantId: choice }) }} className="flex flex-col gap-3">
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700">Zoom name
          <select data-autofocus className={input} value={choice} onChange={(e) => setChoice(e.target.value)}>
            <option value="">— choose —</option>
            {names.map((p) => (
              <option key={p.id} value={p.id}>{p.name}{owner.get(p.id) && owner.get(p.id) !== record.fullName ? ` (now ${owner.get(p.id)})` : ''}</option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2 text-sm text-slate-700">
          <input type="checkbox" className="size-4 accent-teal-700" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
          Remember this for the group’s next sessions
        </label>
        {correct.error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{reason(correct.error)}</p>}
      </form>
    </Modal>
  )
}

function AddNamesModal({ sessionId, onClose }: { sessionId: string; onClose: () => void }) {
  const add = useAddParticipants()
  const toast = useToast()
  const [text, setText] = useState('')
  const names = text.split(/\r?\n/).map((n) => n.trim()).filter(Boolean)
  return (
    <Modal open busy={add.isPending} onClose={onClose} title="Add Zoom names"
      description="One name per line, e.g. copied from Zoom's participant list. They are matched like the agent's reads."
      footer={<>
        <button type="button" className={button.secondary} onClick={onClose} disabled={add.isPending}>Cancel</button>
        <button type="button" className={button.primary} disabled={add.isPending || names.length === 0}
          onClick={() => add.mutate({ id: sessionId, names }, { onSuccess: () => { toast.success(`${names.length} name(s) added`); onClose() } })}>
          {add.isPending && <Spinner label="Adding" />}Add {names.length || ''}
        </button>
      </>}>
      <textarea data-autofocus aria-label="Zoom names" className={`${input} h-56 w-full py-2`} value={text} onChange={(e) => setText(e.target.value)} />
      {add.error && <p role="alert" className="mt-3 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{reason(add.error)}</p>}
    </Modal>
  )
}

function AiSuggestions({ details, locked }: { details: SessionDetails; locked: boolean }) {
  const correct = useCorrectRecord()
  const studentName = new Map(details.records.map((r) => [r.studentId, r.fullName]))
  return (
    <Card title="AI suggestions to review" className="mb-5 border-amber-300">
      <ul className="divide-y divide-slate-100">
        {details.aiSuggestions!.map((s) => (
          <li key={`${s.studentId}-${s.participantId}`} className="flex flex-wrap items-center justify-between gap-2 px-5 py-2.5 text-sm">
            <span><span className="font-medium">{s.name}</span> may be <span className="font-medium">{studentName.get(s.studentId)}</span> <span className="text-slate-500">({s.confidence}%)</span></span>
            {!locked && <button type="button" className={button.smallPrimary} disabled={correct.isPending}
              onClick={() => correct.mutate({ id: details.session.id, studentId: s.studentId, participantId: s.participantId })}>Accept</button>}
          </li>
        ))}
      </ul>
    </Card>
  )
}
