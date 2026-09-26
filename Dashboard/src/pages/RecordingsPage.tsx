import { useState, type ReactNode } from 'react'
import { useSearchParams } from 'react-router'
import { useGroups, useRecordings } from '../api/hooks'
import type { LinkKind, Recording, RecordingQuery, RecordingSort } from '../api/types'
import { AttachRecordingModal } from '../components/AttachRecordingModal'
import { CancelJobModal } from '../components/CancelJobModal'
import { EditRecordingModal } from '../components/EditRecordingModal'
import { PageHeader } from '../components/Layout'
import { RecordingDrawer } from '../components/RecordingDrawer'
import { RecordingsTable } from '../components/RecordingsTable'
import { Card, ErrorBanner, input } from '../components/ui'

const PAGE_SIZE = 25
const SORTS: { value: RecordingSort; label: string }[] = [
  { value: 'updated', label: 'Last updated' },
  { value: 'created', label: 'First seen' },
  { value: 'session', label: 'Session date' },
]
const STATUSES = [
  { value: '', label: 'Any LMS status' },
  { value: 'pending', label: 'Pending' },
  { value: 'attached', label: 'Attached' },
  { value: 'failed', label: 'Failed' },
]
const LINKS = [
  { value: '', label: 'Any source' },
  { value: 'drive', label: 'Drive' },
  { value: 'zoom', label: 'Zoom' },
  { value: 'missing', label: 'Missing' },
]

/** The filters live in the address bar, so a filtered view can be reloaded, shared and linked to. */
export function readQuery(params: URLSearchParams): Required<Pick<RecordingQuery, 'sort' | 'page'>> & RecordingQuery {
  const sort = params.get('sort')
  const page = Number(params.get('page'))
  return {
    group: params.get('group') ?? '',
    date: params.get('date') ?? '',
    status: params.get('status') ?? '',
    link: (params.get('link') ?? '') as LinkKind | '',
    sort: sort === 'created' || sort === 'session' ? sort : 'updated',
    page: Number.isInteger(page) && page > 0 ? page : 1,
  }
}

export function RecordingsPage() {
  const [params, setParams] = useSearchParams()
  const current = readQuery(params)
  const recordings = useRecordings({ ...current, pageSize: PAGE_SIZE })
  const groups = useGroups()
  const [editing, setEditing] = useState<Recording | null>(null)
  const [attaching, setAttaching] = useState<Recording | null>(null)
  const [cancelling, setCancelling] = useState<Recording | null>(null)
  // The open recording is in the address too (?recording=<id>), so it survives a reload.
  const selected = params.get('recording')

  function update(changes: Partial<Record<keyof RecordingQuery | 'recording', string>>) {
    const next = new URLSearchParams(params)
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value)
      else next.delete(key)
    }
    if (!('page' in changes) && !('recording' in changes)) next.delete('page')
    setParams(next, { replace: true })
  }

  const total = recordings.data?.total ?? 0
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const filtered = Boolean(current.group || current.date || current.status || current.link)

  return (
    <>
      <PageHeader title="Recordings" description="Every recording synchronized from Google Sheets. Select a row for details; edit it or attach it to its LMS session." />
      <Card>
        <div className="flex flex-wrap items-end gap-3 border-b border-slate-100 dark:border-slate-800/80 px-5 py-4" role="search">
          <Field label="Group">
            <select aria-label="Group" className={`${input} min-w-44`} value={current.group} onChange={(e) => update({ group: e.target.value })}>
              <option value="">All groups</option>
              {groups.data?.groups.map((g) => (
                <option key={g.group} value={g.group}>
                  {g.group}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Session date">
            <input aria-label="Session date" type="date" className={input} value={current.date} onChange={(e) => update({ date: e.target.value })} />
          </Field>
          <Field label="LMS status">
            <select aria-label="LMS status" className={input} value={current.status} onChange={(e) => update({ status: e.target.value })}>
              {STATUSES.map((s) => (
                <option key={s.value} value={s.value}>{s.label}</option>
              ))}
            </select>
          </Field>
          <Field label="Source">
            <select aria-label="Source" className={input} value={current.link} onChange={(e) => update({ link: e.target.value })}>
              {LINKS.map((l) => (
                <option key={l.value} value={l.value}>{l.label}</option>
              ))}
            </select>
          </Field>
          <Field label="Sort by">
            <select aria-label="Sort by" className={input} value={current.sort} onChange={(e) => update({ sort: e.target.value === 'updated' ? '' : e.target.value })}>
              {SORTS.map((s) => (
                <option key={s.value} value={s.value}>{s.label}</option>
              ))}
            </select>
          </Field>
          {filtered && (
            <button type="button" className="h-9 px-2 text-sm font-medium text-slate-600 hover:text-slate-900 dark:text-slate-400 dark:hover:text-slate-100 transition-colors" onClick={() => setParams(new URLSearchParams(current.sort === 'updated' ? {} : { sort: current.sort }), { replace: true })}>
              Clear filters
            </button>
          )}
        </div>

        {recordings.error ? (
          <ErrorBanner error={recordings.error} />
        ) : (
          <RecordingsTable
            items={recordings.data?.items}
            loading={recordings.isLoading}
            empty={filtered ? 'No recordings match these filters.' : 'No recordings yet. They appear here as Google Sheets synchronizes them.'}
            onSelect={(recording) => update({ recording: recording.id })}
            onEdit={setEditing}
            onAttach={setAttaching}
            onCancel={setCancelling}
          />
        )}

        <footer className="flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 dark:border-slate-800/80 px-5 py-3 text-sm text-slate-600 dark:text-slate-400">
          <span>{recordings.data ? `${total} recording${total === 1 ? '' : 's'}` : ' '}</span>
          <div className="flex items-center gap-2">
            <button type="button" className="rounded-xl border border-slate-200 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-800 dark:bg-[#161d2f] dark:text-slate-200 dark:hover:bg-[#1e273e] disabled:opacity-40 transition-colors" disabled={current.page <= 1} onClick={() => update({ page: String(current.page - 1) })}>
              Previous
            </button>
            <span className="tabular-nums">
              Page {current.page} of {pages}
            </span>
            <button type="button" className="rounded-xl border border-slate-200 bg-white px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-800 dark:bg-[#161d2f] dark:text-slate-200 dark:hover:bg-[#1e273e] disabled:opacity-40 transition-colors" disabled={current.page >= pages} onClick={() => update({ page: String(current.page + 1) })}>
              Next
            </button>
          </div>
        </footer>
      </Card>

      <RecordingDrawer recordingId={selected} onClose={() => update({ recording: '' })} onEdit={setEditing} onAttach={setAttaching} onCancel={setCancelling} />
      <EditRecordingModal recording={editing} onClose={() => setEditing(null)} />
      <AttachRecordingModal recording={attaching} onClose={() => setAttaching(null)} />
      <CancelJobModal recording={cancelling} onClose={() => setCancelling(null)} />
    </>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-xs font-medium text-slate-500 dark:text-slate-400">{label}</span>
      {children}
    </div>
  )
}
