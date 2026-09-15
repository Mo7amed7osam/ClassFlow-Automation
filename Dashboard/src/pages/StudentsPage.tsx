import { useEffect, useState, type ChangeEvent, type FormEvent } from 'react'
import { useSearchParams } from 'react-router'
import { useCreateStudent, useForgetAlias, useImportStudents, useStudentAliases, useStudents, useUpdateStudent, type ImportResult, type Student } from '../api/attendance'
import { useGroups } from '../api/hooks'
import { PageHeader } from '../components/Layout'
import { Drawer, Modal } from '../components/Overlay'
import { useToast } from '../components/Toast'
import { reason } from '../components/UserModals'
import { button, Card, EmptyState, ErrorBanner, input, LoadingRows, Pill, Spinner, td, th } from '../components/ui'

const COLUMNS = ['#', 'Student', 'E-mail', 'ID', 'Remembered names', '']

/** A group's roster: the students attendance is matched against. */
export function StudentsPage() {
  const [params, setParams] = useSearchParams()
  const groups = useGroups()
  const groupNames = (groups.data?.groups ?? []).filter((g) => !g.archived).map((g) => g.group)
  const group = params.get('group') || groupNames[0] || ''
  const [q, setQ] = useState('')
  const [inactive, setInactive] = useState(false)
  const students = useStudents({ group: group || undefined, q: q.trim() || undefined, includeInactive: inactive })
  const [dialog, setDialog] = useState<null | { kind: 'add' } | { kind: 'import' } | { kind: 'edit'; student: Student }>(null)
  const [aliasesOf, setAliasesOf] = useState<Student | null>(null)
  const items = students.data?.students ?? []

  return (
    <>
      <PageHeader
        title="Students"
        description="Each group's roster. Attendance is matched against these names, and against the Zoom names remembered for each student."
        action={
          <div className="flex gap-2">
            <button type="button" className={button.secondary} disabled={!group} onClick={() => setDialog({ kind: 'import' })}>Import roster</button>
            <button type="button" className={button.primary} disabled={!group} onClick={() => setDialog({ kind: 'add' })}>Add student</button>
          </div>
        }
      />
      <Card>
        <div className="flex flex-wrap items-end gap-3 border-b border-slate-100 px-5 py-4">
          <label className="flex flex-col gap-1 text-xs font-medium text-slate-500">
            Group
            <select aria-label="Group" className={`${input} min-w-48`} value={group} onChange={(e) => setParams({ group: e.target.value }, { replace: true })}>
              {groupNames.length === 0 && <option value="">No groups</option>}
              {groupNames.map((g) => <option key={g} value={g}>{g}</option>)}
            </select>
          </label>
          <label className="flex flex-col gap-1 text-xs font-medium text-slate-500">
            Search
            <input aria-label="Search students" className={input} placeholder="Name, e-mail or id" value={q} onChange={(e) => setQ(e.target.value)} />
          </label>
          <label className="flex items-center gap-2 pb-2 text-sm text-slate-600">
            <input type="checkbox" className="size-4 accent-teal-700" checked={inactive} onChange={(e) => setInactive(e.target.checked)} />
            Show removed students
          </label>
        </div>
        {students.error ? <ErrorBanner error={students.error} /> : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-100">
              <thead className="bg-slate-50/70"><tr>{COLUMNS.map((c, i) => <th key={i} scope="col" className={th}>{c}</th>)}</tr></thead>
              <tbody className="divide-y divide-slate-100">
                {students.isLoading ? <LoadingRows columns={COLUMNS.length} /> : items.map((s) => (
                  <tr key={s.id} className={`hover:bg-slate-50/60 ${s.active ? '' : 'opacity-60'}`}>
                    <td className={`${td} tabular-nums text-slate-500`}>{s.order ?? ''}</td>
                    <td className={td}>
                      <span className="font-medium text-slate-900">{s.fullName}</span>
                      {!s.active && <span className="ml-2"><Pill tone="slate">Removed</Pill></span>}
                      {s.aliases.length > 0 && <p className="text-xs text-slate-500">Also: {s.aliases.join(', ')}</p>}
                    </td>
                    <td className={`${td} text-slate-600`}>{s.email ?? <span className="text-slate-400">—</span>}</td>
                    <td className={`${td} font-mono text-xs text-slate-600`}>{s.externalId ?? ''}</td>
                    <td className={td}>
                      <button type="button" className={button.small} onClick={() => setAliasesOf(s)} aria-label={`Remembered names of ${s.fullName}`}>View</button>
                    </td>
                    <td className={`${td} text-right`}>
                      <button type="button" className={button.small} onClick={() => setDialog({ kind: 'edit', student: s })} aria-label={`Edit ${s.fullName}`}>Edit</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!students.isLoading && items.length === 0 && (
              <EmptyState>{group ? 'No students in this group yet. Import the roster or add them one by one.' : 'You have no groups yet.'}</EmptyState>
            )}
          </div>
        )}
        <footer className="border-t border-slate-100 px-5 py-3 text-sm text-slate-600">{students.data ? `${students.data.count} student${students.data.count === 1 ? '' : 's'}` : ' '}</footer>
      </Card>

      {dialog?.kind === 'add' && <StudentModal group={group} onClose={() => setDialog(null)} />}
      {dialog?.kind === 'edit' && <StudentModal group={group} student={dialog.student} onClose={() => setDialog(null)} />}
      {dialog?.kind === 'import' && <ImportModal group={group} onClose={() => setDialog(null)} />}
      <AliasesDrawer student={aliasesOf} onClose={() => setAliasesOf(null)} />
    </>
  )
}

function StudentModal({ group, student, onClose }: { group: string; student?: Student; onClose: () => void }) {
  const create = useCreateStudent()
  const update = useUpdateStudent()
  const toast = useToast()
  const [name, setName] = useState(student?.fullName ?? '')
  const [email, setEmail] = useState(student?.email ?? '')
  const [externalId, setExternalId] = useState(student?.externalId ?? '')
  const [order, setOrder] = useState(student?.order?.toString() ?? '')
  const [aliases, setAliases] = useState((student?.aliases ?? []).join(', '))
  const [active, setActive] = useState(student?.active ?? true)
  const busy = create.isPending || update.isPending
  const error = create.error ?? update.error

  function submit(event: FormEvent) {
    event.preventDefault()
    if (!name.trim()) return
    const body = {
      fullName: name.trim(), email: email.trim() || null, externalId: externalId.trim() || null,
      order: order.trim() ? Number(order) : null, aliases: aliases.split(',').map((a) => a.trim()).filter(Boolean),
    }
    const done = () => { toast.success(student ? `Saved ${body.fullName}` : `Added ${body.fullName}`); onClose() }
    if (student) update.mutate({ id: student.id, ...body, active }, { onSuccess: done })
    else create.mutate({ group, ...body, email: body.email ?? undefined, externalId: body.externalId ?? undefined }, { onSuccess: done })
  }

  return (
    <Modal open busy={busy} onClose={onClose} title={student ? `Edit ${student.fullName}` : `Add a student to ${group}`}
      footer={<>
        <button type="button" className={button.secondary} onClick={onClose} disabled={busy}>Cancel</button>
        <button type="submit" form="student-form" className={button.primary} disabled={busy || !name.trim()}>{busy && <Spinner label="Saving" />}Save</button>
      </>}>
      <form id="student-form" onSubmit={submit} className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700 sm:col-span-2">Full name
          <input data-autofocus className={input} maxLength={300} value={name} onChange={(e) => setName(e.target.value)} /></label>
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700">E-mail
          <input className={input} maxLength={320} value={email} onChange={(e) => setEmail(e.target.value)} /></label>
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700">Student id
          <input className={input} maxLength={128} value={externalId} onChange={(e) => setExternalId(e.target.value)} /></label>
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700">Order
          <input className={input} inputMode="numeric" value={order} onChange={(e) => setOrder(e.target.value.replace(/\D/g, ''))} /></label>
        <label className="flex flex-col gap-1 text-sm font-medium text-slate-700 sm:col-span-2">Other names (comma-separated)
          <input className={input} value={aliases} onChange={(e) => setAliases(e.target.value)} placeholder="e.g. Mo Osama, محمد أسامة" /></label>
        {student && (
          <label className="flex items-center gap-2 text-sm text-slate-700 sm:col-span-2">
            <input type="checkbox" className="size-4 accent-teal-700" checked={active} onChange={(e) => setActive(e.target.checked)} />
            On the roster (untick to remove; past attendance keeps the name)
          </label>
        )}
        {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 sm:col-span-2">{reason(error)}</p>}
      </form>
    </Modal>
  )
}

function ImportModal({ group, onClose }: { group: string; onClose: () => void }) {
  const imp = useImportStudents()
  const toast = useToast()
  const [text, setText] = useState('')
  const [preview, setPreview] = useState<ImportResult | null>(null)
  useEffect(() => setPreview(null), [text, group])

  async function readFile(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    if (file) setText(await file.text())
  }

  function run(dryRun: boolean) {
    imp.mutate({ group, text, dryRun }, {
      onSuccess: (result) => {
        if (dryRun) { setPreview(result); return }
        toast.success(`Roster imported into ${group}`, `${result.created} added, ${result.updated} updated.`)
        onClose()
      },
    })
  }

  return (
    <Modal open busy={imp.isPending} onClose={onClose} title={`Import a roster into ${group}`}
      description="Paste from Excel or the LMS, or pick a CSV. A header row (Name, Email, Group, ID, Order) is used when there is one. Nobody is removed."
      footer={<>
        <button type="button" className={button.secondary} onClick={onClose} disabled={imp.isPending}>Cancel</button>
        {preview
          ? <button type="button" className={button.primary} disabled={imp.isPending || preview.created + preview.updated === 0} onClick={() => run(false)}>{imp.isPending && <Spinner label="Importing" />}Import</button>
          : <button type="button" className={button.primary} disabled={imp.isPending || !text.trim()} onClick={() => run(true)}>{imp.isPending && <Spinner label="Checking" />}Preview</button>}
      </>}>
      <div className="flex flex-col gap-3">
        <textarea data-autofocus aria-label="Roster" disabled={imp.isPending} className={`${input} h-48 py-2 font-mono text-xs`} value={text} onChange={(e) => setText(e.target.value)}
          placeholder={'Order\tName\tEmail\n1\tMohab Osama Sayed\tmohab@example.com'} />
        <input aria-label="Roster file" disabled={imp.isPending} type="file" accept=".csv,.txt,.tsv" onChange={readFile} className="text-sm" />
        {preview && (
          <div role="status" className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700">
            <p><span className="font-semibold">{preview.created}</span> to add, <span className="font-semibold">{preview.updated}</span> to update,{' '}
              {preview.unchanged} already there.</p>
            {preview.createdNames.length > 0 && <p className="mt-1 text-xs text-slate-500">New: {preview.createdNames.slice(0, 12).join(', ')}{preview.createdNames.length > 12 ? '…' : ''}</p>}
            {preview.updatedNames.length > 0 && <p className="mt-1 text-xs">Updated: {preview.updatedNames.join(', ')}</p>}
            <p className="mt-1 text-xs">Invalid rows: {preview.invalidRows?.length ?? 0}. Duplicate rows: {preview.duplicateRows?.length ?? 0}.</p>
            {preview.skipped.length > 0 && <ul className="mt-1 list-disc pl-5 text-xs text-amber-800">{preview.skipped.slice(0, 8).map((s) => <li key={s}>{s}</li>)}</ul>}
          </div>
        )}
        {imp.error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{reason(imp.error)}</p>}
      </div>
    </Modal>
  )
}

function AliasesDrawer({ student, onClose }: { student: Student | null; onClose: () => void }) {
  const aliases = useStudentAliases(student?.id ?? null)
  const forget = useForgetAlias()
  const items = aliases.data?.aliases ?? []
  return (
    <Drawer open={student !== null} onClose={onClose} title={student ? `Remembered names: ${student.fullName}` : ''}
      description="Zoom names confirmed (or ruled out) for this student. They are used in every session of the group.">
      {aliases.isLoading ? <p className="text-sm text-slate-500">Loading…</p> : items.length === 0 ? (
        <p className="text-sm text-slate-500">Nothing remembered yet. Matching a Zoom name to this student by hand remembers it.</p>
      ) : (
        <ul className="divide-y divide-slate-100 rounded-lg border border-slate-200">
          {items.map((a) => (
            <li key={a.id} className="flex items-center justify-between gap-3 px-3 py-2 text-sm">
              <span>
                <span className="font-medium text-slate-800">{a.alias}</span>{' '}
                <Pill tone={a.status === 'accepted' ? 'green' : 'red'}>{a.status === 'accepted' ? 'is this student' : 'is not this student'}</Pill>
                <span className="ml-2 text-xs text-slate-400">{a.source}</span>
              </span>
              <button type="button" className={button.small} disabled={forget.isPending} onClick={() => forget.mutate(a.id)} aria-label={`Forget ${a.alias}`}>Forget</button>
            </li>
          ))}
        </ul>
      )}
    </Drawer>
  )
}
