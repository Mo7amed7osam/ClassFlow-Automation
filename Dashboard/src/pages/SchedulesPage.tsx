import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { useSaveSchedules, useSchedules } from '../api/hooks'
import type { ScheduleEngine, ScheduleRow } from '../api/types'
import { PageHeader } from '../components/Layout'
import { useToast } from '../components/Toast'
import { button, Card, EmptyState, ErrorBanner, input, LoadingRows, Pill, Spinner, td, th, TimeAgo } from '../components/ui'
import { Field } from './ZoomAccountsPage'

/** The days as the app's own flags name them, in the order a week is read. */
export const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const
export type Day = (typeof DAYS)[number]

/** "Monday, Wednesday" -> the two days. "None", "" or a one-off class -> nothing. */
export function parseDays(days: string | undefined | null): Day[] {
  if (!days || days === 'None') return []
  if (days === 'EveryDay') return [...DAYS]
  const named = days.split(',').map((day) => day.trim())
  return DAYS.filter((day) => named.includes(day))
}

/** Back to what the app writes: the names, or "None" for a class on one date only. */
export function writeDays(days: Day[]): string {
  if (days.length === 0) return 'None'
  if (days.length === DAYS.length) return 'EveryDay'
  return DAYS.filter((day) => days.includes(day)).join(', ')
}

/** "19:00:00" -> "19:00" for a time field, and back again. */
export const toTimeField = (time: string | undefined) => (time ?? '').slice(0, 5)
export const toTimeValue = (field: string) => `${field.length === 5 ? field : '00:00'}:00`

function describeDays(row: ScheduleRow): string {
  const days = parseDays(row.days)
  if (days.length === 7) return 'Every day'
  if (days.length > 0) return days.map((day) => day.slice(0, 3)).join(', ')
  return row.occurrenceDate ? `Once, on ${row.occurrenceDate}` : 'No day set'
}

interface Draft {
  id: string
  name: string
  meetingUrl: string
  accountId: string
  time: string
  days: Day[]
  once: string
  enabled: boolean
  groupName: string
  preferredEngine: '' | ScheduleEngine
}

const empty: Draft = { id: '', name: '', meetingUrl: '', accountId: '', time: '19:00', days: [], once: '', enabled: true, groupName: '', preferredEngine: '' }

function toDraft(row: ScheduleRow): Draft {
  return {
    id: row.id,
    name: row.name ?? '',
    meetingUrl: row.meetingUrl ?? '',
    accountId: row.accountId ?? '',
    time: toTimeField(row.time),
    days: parseDays(row.days),
    once: row.occurrenceDate ?? '',
    enabled: row.enabled !== false,
    groupName: row.groupName ?? '',
    preferredEngine: (row.preferredEngine ?? '') as Draft['preferredEngine'],
  }
}

/**
 * An id for a new class, in the shape the app writes them. crypto.randomUUID is only there in a
 * secure context, and a dashboard opened over plain http on a server's own address is not one, so
 * this falls back to the same shape built from whatever randomness the browser does offer.
 */
export function newId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  const bytes = new Uint8Array(16)
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') crypto.getRandomValues(bytes)
  else for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256)
  bytes[6] = (bytes[6] & 0x0f) | 0x40          // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80          // variant
  const hex = [...bytes].map((byte) => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

/** A draft as the app's own shape, keeping whatever the app put on the row that this page has no field for. */
function toRow(draft: Draft, was: ScheduleRow | null): ScheduleRow {
  return {
    ...(was ?? {}),
    id: draft.id || newId(),
    name: draft.name.trim(),
    meetingUrl: draft.meetingUrl.trim(),
    accountId: draft.accountId.trim(),
    time: toTimeValue(draft.time),
    days: writeDays(draft.days),
    enabled: draft.enabled,
    occurrenceDate: draft.days.length === 0 ? (draft.once || null) : null,
    groupName: draft.groupName.trim() || null,
    preferredEngine: draft.preferredEngine || null,
  }
}

/**
 * The classes that open by themselves - the app's Schedules page, on the web.
 *
 * A machine of yours reads these and opens each class at its time, and a machine with none of its own
 * takes these instead, so a class added here opens on whichever machine is yours. The server keeps
 * the list exactly as the app writes it and never looks inside a row; this page edits that same
 * shape, which is why a class edited here is the same class on the PC that syncs.
 *
 * The whole list is saved at once, as the endpoint takes it: what is on screen is what is kept.
 */
export function SchedulesPage() {
  const { data, isLoading, error } = useSchedules()
  const save = useSaveSchedules()
  const toast = useToast()
  const rows = data?.schedules ?? []

  const [editing, setEditing] = useState<string | null>(null)
  const [draft, setDraft] = useState<Draft>(empty)
  const [submitted, setSubmitted] = useState(false)

  const was = rows.find((row) => row.id === editing) ?? null
  const isNew = editing === ''

  const problem =
    !draft.name.trim() ? 'A name for the class is required.'
      : !draft.meetingUrl.trim().toLowerCase().startsWith('https://') ? 'The meeting link must start with https://.'
        : !/^([01]\d|2[0-3]):[0-5]\d$/.test(draft.time) ? 'A time like 19:00 is required.'
          : draft.days.length === 0 && !draft.once ? 'Pick the days it repeats on, or a single date.'
            : null

  function open(row: ScheduleRow | null) {
    setSubmitted(false)
    setEditing(row ? row.id : '')
    setDraft(row ? toDraft(row) : empty)
  }

  function sendAll(next: ScheduleRow[], done: string) {
    save.mutate({ schedules: next, deviceName: data?.deviceName ?? null }, {
      onSuccess: () => { setEditing(null); setSubmitted(false); toast.success(done) },
    })
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (problem) return
    const row = toRow(draft, was)
    const others = rows.filter((other) => other.id !== row.id)
    sendAll([...others, row], isNew ? `${row.name} added.` : `${row.name} saved.`)
  }

  function toggle(row: ScheduleRow) {
    sendAll(rows.map((other) => (other.id === row.id ? { ...other, enabled: !other.enabled } : other)),
      row.enabled ? `${row.name} will not open by itself.` : `${row.name} opens by itself again.`)
  }

  function remove(row: ScheduleRow) {
    if (!window.confirm(`Remove ${row.name}? It will not open by itself any more.`)) return
    sendAll(rows.filter((other) => other.id !== row.id), `${row.name} removed.`)
  }

  const failure = save.error instanceof ApiError && typeof save.error.details === 'string' ? save.error.details : save.error?.message

  return (
    <>
      <PageHeader
        title="Classes that open by themselves"
        description="Each one opens at its time on a machine of yours, without anybody pressing anything."
        action={<button type="button" className={button.primary} onClick={() => open(null)}>Add a class</button>}
      />

      {save.isError && <p role="alert" className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{failure}</p>}

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <Card
          title={`${rows.length} class${rows.length === 1 ? '' : 'es'}`}
          action={data?.deviceName ? <span className="text-xs text-slate-500">last from {data.deviceName} · <TimeAgo iso={data.updatedAt} /></span> : null}
        >
          {error ? <ErrorBanner error={error} /> : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[44rem]">
                <thead className="border-b border-slate-100 bg-slate-50/60">
                  <tr>
                    <th className={th}>Class</th>
                    <th className={th}>When</th>
                    <th className={th}>Zoom account</th>
                    <th className={th}>Opens with</th>
                    <th className={th}>Last opened</th>
                    <th className={th}><span className="sr-only">Actions</span></th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {isLoading && <LoadingRows columns={6} />}
                  {!isLoading && rows.length === 0 && (
                    <tr><td colSpan={6}><EmptyState>Nothing opens by itself yet. Add a class, or let a machine of yours send its own.</EmptyState></td></tr>
                  )}
                  {rows.map((row) => (
                    <tr key={row.id} className={`hover:bg-slate-50/60 ${row.enabled ? '' : 'opacity-60'}`}>
                      <td className={td}>
                        <p className="font-medium text-slate-900">{row.name || <span className="text-slate-400">(no name)</span>}</p>
                        <p className="text-xs text-slate-500">{row.groupName || '—'}</p>
                        {!row.enabled && <Pill tone="slate">Off</Pill>}
                        {row.coordinator && <Pill tone="blue">{row.coordinator}</Pill>}
                      </td>
                      <td className={td}>
                        <p className="font-medium tabular-nums text-slate-800">{toTimeField(row.time) || '—'}</p>
                        <p className="text-xs text-slate-500">{describeDays(row)}</p>
                      </td>
                      <td className={td}>{row.accountId || <span className="text-slate-400">any</span>}</td>
                      <td className={td}>
                        {row.preferredEngine === 'Web' ? 'Zoom in a browser'
                          : row.preferredEngine === 'Desktop' ? 'The Zoom app'
                            : <span className="text-slate-400">the account&apos;s choice</span>}
                      </td>
                      <td className={`${td} text-slate-500`}>{row.lastTriggeredDate || <span className="text-slate-400">never</span>}</td>
                      <td className={`${td} text-right`}>
                        <span className="inline-flex gap-1.5">
                          <button type="button" className={button.small} onClick={() => toggle(row)} disabled={save.isPending} aria-label={`${row.enabled ? 'Turn off' : 'Turn on'} ${row.name}`}>
                            {row.enabled ? 'Turn off' : 'Turn on'}
                          </button>
                          <button type="button" className={button.small} onClick={() => open(row)} aria-label={`Edit ${row.name}`}>Edit</button>
                          <button type="button" className={button.smallDanger} onClick={() => remove(row)} disabled={save.isPending} aria-label={`Remove ${row.name}`}>Remove</button>
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        {editing !== null && (
          <Card title={isNew ? 'New class' : `Edit ${was?.name || 'class'}`}>
            <form onSubmit={submit} noValidate aria-label="Class that opens by itself" className="flex flex-col gap-4 px-5 py-4">
              <Field label="Name" hint="What you call it in lists, for example CAI5_AIS4_S7 Tuesday.">
                <input className={`${input} w-full`} value={draft.name} maxLength={120} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
              </Field>
              <Field label="Meeting link">
                <input className={`${input} w-full`} inputMode="url" value={draft.meetingUrl} onChange={(e) => setDraft({ ...draft, meetingUrl: e.target.value })} />
              </Field>
              <Field label="Zoom account" hint="The account it is hosted with, as your Zoom accounts name it.">
                <input className={`${input} w-full`} value={draft.accountId} maxLength={100} onChange={(e) => setDraft({ ...draft, accountId: e.target.value })} />
              </Field>
              <Field label="Group" hint="Which group's class it is. Used for the attendance and the LMS.">
                <input className={`${input} w-full`} value={draft.groupName} maxLength={100} onChange={(e) => setDraft({ ...draft, groupName: e.target.value })} />
              </Field>
              <Field label="Time" hint="The class's own time. The meeting opens a quarter of an hour before it.">
                <input className={`${input} w-full`} type="time" value={draft.time} onChange={(e) => setDraft({ ...draft, time: e.target.value })} />
              </Field>
              <fieldset>
                <legend className="mb-1.5 text-sm font-medium text-slate-700">Days it repeats on</legend>
                <div className="flex flex-wrap gap-1.5">
                  {DAYS.map((day) => {
                    const on = draft.days.includes(day)
                    return (
                      <button
                        key={day} type="button" aria-pressed={on}
                        onClick={() => setDraft({ ...draft, days: on ? draft.days.filter((d) => d !== day) : [...draft.days, day] })}
                        className={`h-8 rounded-lg border px-2.5 text-xs font-medium ${on ? 'border-teal-700 bg-teal-700 text-white' : 'border-slate-300 bg-white text-slate-700 hover:bg-slate-50'}`}
                      >
                        {day.slice(0, 3)}
                      </button>
                    )
                  })}
                </div>
                <p className="mt-1 text-xs text-slate-500">Pick none for a class on a single date.</p>
              </fieldset>
              {draft.days.length === 0 && (
                <Field label="On this date only">
                  <input className={`${input} w-full`} type="date" value={draft.once} onChange={(e) => setDraft({ ...draft, once: e.target.value })} />
                </Field>
              )}
              <Field label="Opens with">
                <select className={`${input} w-full`} value={draft.preferredEngine} onChange={(e) => setDraft({ ...draft, preferredEngine: e.target.value as Draft['preferredEngine'] })}>
                  <option value="">The account's choice</option>
                  <option value="Web">Zoom in a browser (what a server uses)</option>
                  <option value="Desktop">The Zoom app</option>
                </select>
              </Field>
              <label className="flex items-center gap-2 text-sm text-slate-700">
                <input type="checkbox" className="size-4 rounded border-slate-300" checked={draft.enabled} onChange={(e) => setDraft({ ...draft, enabled: e.target.checked })} />
                Open it by itself
              </label>

              {submitted && problem && <p className="text-xs text-rose-700">{problem}</p>}
              <div className="flex gap-2">
                <button type="submit" className={button.primary} disabled={save.isPending}>
                  {save.isPending && <Spinner label="Saving" />}
                  {save.isPending ? 'Saving…' : 'Save'}
                </button>
                <button type="button" className={button.secondary} onClick={() => setEditing(null)}>Cancel</button>
              </div>
            </form>
          </Card>
        )}
      </div>
    </>
  )
}
