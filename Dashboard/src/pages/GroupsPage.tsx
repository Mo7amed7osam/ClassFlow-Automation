import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { useAdminGroups, useCreateGroup, useGroups, useMe, useUpdateGroup } from '../api/hooks'
import type { AdminGroup, GroupSummary } from '../api/types'
import { PageHeader } from '../components/Layout'
import { Modal } from '../components/Overlay'
import { useToast } from '../components/Toast'
import { reason } from '../components/UserModals'
import { button, Card, EmptyState, ErrorBanner, input, LoadingRows, Pill, Spinner, TimeAgo, td, th } from '../components/ui'
import { formatSessionDate } from '../lib/format'

const GROUP = /^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$/

function Count({ n, tone }: { n: number; tone: 'amber' | 'green' | 'red' }) {
  return n > 0 ? <Pill tone={tone}>{n}</Pill> : <span className="text-slate-400">0</span>
}

function GroupName({ group }: { group: GroupSummary }) {
  return (
    <div className="min-w-0">
      <div className="flex items-center gap-2">
        <Link to={`/recordings?group=${encodeURIComponent(group.group)}`} className="font-medium text-teal-700 hover:underline">
          {group.group}
        </Link>
        {group.archived && <Pill tone="slate" title="Archived: hidden from coordinators">Archived</Pill>}
      </div>
      <div className="mt-2 flex gap-3 text-xs text-teal-700">
        <Link to={`/students?group=${encodeURIComponent(group.group)}`}>Students</Link>
        <Link to={`/attendance?group=${encodeURIComponent(group.group)}`}>Attendance</Link>
        <Link to={`/recordings?group=${encodeURIComponent(group.group)}`}>Recordings</Link>
      </div>
      {group.displayName && <p className="text-xs text-slate-500">{group.displayName}</p>}
    </div>
  )
}

function Stats({ group }: { group: GroupSummary }) {
  return (
    <>
      <td className={`${td} tabular-nums`}>{group.recordings}</td>
      <td className={`${td} whitespace-nowrap`}>{group.lastSessionDate ? formatSessionDate(group.lastSessionDate) : <span className="text-slate-400">—</span>}</td>
      <td className={td}><Count n={group.pending} tone="amber" /></td>
      <td className={td}><Count n={group.onLms} tone="green" /></td>
      <td className={td}><Count n={group.missingLink} tone="red" /></td>
      <td className={`${td} text-slate-600`}>{group.lastUpdatedAt ? <TimeAgo iso={group.lastUpdatedAt} /> : <span className="text-slate-400">—</span>}</td>
    </>
  )
}

const STAT_COLUMNS = ['Recordings', 'Last recording', 'Pending', 'On LMS', 'No link', 'Last updated']

export function GroupsPage() {
  const { data: me } = useMe()
  return me?.role === 'admin' ? <AdminGroups /> : <MyGroups />
}

/** A coordinator's own groups (the backend sends no others). */
function MyGroups() {
  const groups = useGroups()
  const items = groups.data?.groups
  const columns = ['Group', ...STAT_COLUMNS]
  return (
    <>
      <PageHeader title="My groups" description="Manage students, attendance and recordings for your assigned groups." />
      <Card>
        {groups.error ? (
          <ErrorBanner error={groups.error} />
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-100">
              <thead className="bg-slate-50/70">
                <tr>{columns.map((c) => <th key={c} scope="col" className={th}>{c}</th>)}</tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {groups.isLoading ? (
                  <LoadingRows columns={columns.length} />
                ) : (
                  items?.map((g) => (
                    <tr key={g.id} className="hover:bg-slate-50/60">
                      <td className={td}><GroupName group={g} /></td>
                      <Stats group={g} />
                    </tr>
                  ))
                )}
              </tbody>
            </table>
            {!groups.isLoading && items?.length === 0 && <EmptyState>You have no groups yet. Ask the admin to assign yours.</EmptyState>}
          </div>
        )}
      </Card>
    </>
  )
}

/** Every group, who coordinates it, and the admin's changes: add, label, archive. */
function AdminGroups() {
  const groups = useAdminGroups()
  const update = useUpdateGroup()
  const toast = useToast()
  const [adding, setAdding] = useState(false)
  const [labelling, setLabelling] = useState<AdminGroup | null>(null)
  const [showArchived, setShowArchived] = useState(false)
  const all = groups.data?.groups ?? []
  const archivedCount = all.filter((g) => g.archived).length
  const items = showArchived ? all : all.filter((g) => !g.archived)
  const columns = ['Group', 'Coordinators', ...STAT_COLUMNS, 'Actions']

  function archive(group: AdminGroup, archived: boolean) {
    update.mutate({ id: group.id, archived }, {
      onSuccess: () => toast.success(archived ? `${group.group} archived` : `${group.group} restored`,
        archived ? 'Its coordinators no longer see it. Its recordings are kept.' : 'Its coordinators see it again.'),
      onError: (error) => toast.error('Could not change the group', reason(error)),
    })
  }

  return (
    <>
      <PageHeader
        title="Groups"
        description="New groups appear automatically with their first recording from Google Sheets. Give them to coordinators on the Users page."
        action={<button type="button" className={button.secondary} onClick={() => setAdding(true)}>Add group</button>}
      />
      <Card>
        {archivedCount > 0 && (
          <label className="flex items-center gap-2 border-b border-slate-100 px-5 py-2.5 text-sm text-slate-600">
            <input type="checkbox" className="size-4 accent-teal-700" checked={showArchived} onChange={(e) => setShowArchived(e.target.checked)} />
            Show archived groups ({archivedCount})
          </label>
        )}
        {groups.error ? (
          <ErrorBanner error={groups.error} />
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-100">
              <thead className="bg-slate-50/70">
                <tr>{columns.map((c) => <th key={c} scope="col" className={th}>{c}</th>)}</tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {groups.isLoading ? (
                  <LoadingRows columns={columns.length} />
                ) : (
                  items.map((g) => (
                    <tr key={g.id} className={`hover:bg-slate-50/60 ${g.archived ? 'opacity-60' : ''}`}>
                      <td className={td}><GroupName group={g} /></td>
                      <td className={`${td} max-w-56`}>
                        {g.coordinators.length ? (
                          <div className="flex flex-wrap gap-1">
                            {g.coordinators.map((c) => (
                              <Pill key={c.id} tone={c.status === 'active' ? 'blue' : 'slate'} title={c.status === 'active' ? c.username : `${c.username} (${c.status})`}>
                                {c.displayName}
                              </Pill>
                            ))}
                          </div>
                        ) : (
                          <span className="text-slate-400">Nobody</span>
                        )}
                      </td>
                      <Stats group={g} />
                      <td className={td}>
                        <div className="flex gap-1.5">
                          <button type="button" className={button.small} onClick={() => setLabelling(g)} aria-label={`Label ${g.group}`}>Label</button>
                          <button type="button" className={button.small} disabled={update.isPending} onClick={() => archive(g, !g.archived)} aria-label={`${g.archived ? 'Restore' : 'Archive'} ${g.group}`}>
                            {g.archived ? 'Restore' : 'Archive'}
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
            {!groups.isLoading && items.length === 0 && <EmptyState>No groups yet: they appear with their first recording.</EmptyState>}
          </div>
        )}
      </Card>
      {adding && <AddGroupModal onClose={() => setAdding(false)} />}
      {labelling && <LabelGroupModal group={labelling} onClose={() => setLabelling(null)} />}
    </>
  )
}

function AddGroupModal({ onClose }: { onClose: () => void }) {
  const create = useCreateGroup()
  const toast = useToast()
  const [name, setName] = useState('')
  const [label, setLabel] = useState('')
  const [submitted, setSubmitted] = useState(false)
  const problem = GROUP.test(name.trim()) ? null : 'Letters, digits, spaces and _ - . only (max 100). Use the exact name the recordings sheet uses.'

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (problem) return
    create.mutate({ name: name.trim(), displayName: label.trim() || undefined }, {
      onSuccess: () => { toast.success(`${name.trim()} added`); onClose() },
    })
  }

  return (
    <Modal
      open
      busy={create.isPending}
      onClose={onClose}
      title="Add group"
      description="Only needed to give a group out before its first recording arrives."
      footer={
        <>
          <button type="button" className={button.secondary} onClick={onClose} disabled={create.isPending}>Cancel</button>
          <button type="submit" form="add-group" className={button.primary} disabled={create.isPending}>
            {create.isPending && <Spinner label="Adding" />}
            Add group
          </button>
        </>
      }
    >
      <form id="add-group" onSubmit={submit} noValidate className="flex flex-col gap-4">
        <div>
          <label htmlFor="group-name" className="mb-1.5 block text-sm font-medium text-slate-700">Name</label>
          <input id="group-name" data-autofocus className={`${input} w-full`} maxLength={100} placeholder="CAI5_AIS4_S10" value={name} onChange={(e) => setName(e.target.value)} />
          {submitted && problem && <p className="mt-1 text-xs text-rose-700">{problem}</p>}
        </div>
        <div>
          <label htmlFor="group-label" className="mb-1.5 block text-sm font-medium text-slate-700">Label <span className="font-normal text-slate-400">(optional)</span></label>
          <input id="group-label" className={`${input} w-full`} maxLength={100} value={label} onChange={(e) => setLabel(e.target.value)} />
        </div>
        {create.isError && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{reason(create.error)}</p>}
      </form>
    </Modal>
  )
}

function LabelGroupModal({ group, onClose }: { group: AdminGroup; onClose: () => void }) {
  const update = useUpdateGroup()
  const toast = useToast()
  const [label, setLabel] = useState(group.displayName ?? '')

  function submit(event: FormEvent) {
    event.preventDefault()
    update.mutate({ id: group.id, displayName: label.trim() || null }, {
      onSuccess: () => { toast.success(`Label saved for ${group.group}`); onClose() },
    })
  }

  return (
    <Modal
      open
      busy={update.isPending}
      onClose={onClose}
      title={`Label ${group.group}`}
      description="A friendlier name shown under the group. The group's name itself never changes."
      footer={
        <>
          <button type="button" className={button.secondary} onClick={onClose} disabled={update.isPending}>Cancel</button>
          <button type="submit" form="label-group" className={button.primary} disabled={update.isPending}>Save label</button>
        </>
      }
    >
      <form id="label-group" onSubmit={submit} noValidate>
        <label htmlFor="label-input" className="mb-1.5 block text-sm font-medium text-slate-700">Label</label>
        <input id="label-input" data-autofocus className={`${input} w-full`} maxLength={100} value={label} onChange={(e) => setLabel(e.target.value)} placeholder="e.g. AI track, Saturday evening" />
        {update.isError && <p role="alert" className="mt-3 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{reason(update.error)}</p>}
      </form>
    </Modal>
  )
}
