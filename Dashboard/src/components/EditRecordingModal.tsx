import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { ApiError } from '../api/client'
import { useEditRecording, useMe } from '../api/hooks'
import type { Recording, RecordingChanges } from '../api/types'
import { fieldLabel, lmsLabel } from '../lib/format'
import { Modal } from './Overlay'
import { useToast } from './Toast'
import { button, input, Spinner } from './ui'

interface Form {
  group: string
  date: string
  startTime: string
  fileName: string
  type: string
  link: string
}

const GROUP = /^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$/
const DATE = /^\d{4}-\d{2}-\d{2}$/
const TIME = /^([01]\d|2[0-3]):[0-5]\d$/

function currentLink(recording: Recording): string {
  return recording.driveLink ?? recording.zoomLink ?? ''
}

function formOf(recording: Recording): Form {
  return {
    group: recording.group,
    date: recording.date,
    startTime: recording.startTime ?? '',
    fileName: recording.fileName ?? '',
    type: recording.type ?? '',
    link: currentLink(recording),
  }
}

/** Mistakes the form can spot before asking the server (which checks everything again). */
export function validate(form: Form): Partial<Record<keyof Form, string>> {
  const errors: Partial<Record<keyof Form, string>> = {}
  if (!form.group.trim()) errors.group = 'Group is required.'
  else if (!GROUP.test(form.group.trim())) errors.group = 'Letters, digits, spaces and _ - . only (max 100).'
  if (!form.date.trim()) errors.date = 'Date is required.'
  else if (!DATE.test(form.date.trim())) errors.date = 'Use the date picker (yyyy-mm-dd).'
  if (form.startTime.trim() && !TIME.test(form.startTime.trim())) errors.startTime = 'Use HH:mm, 24-hour.'
  if (form.link.trim() && !/^https:\/\//i.test(form.link.trim())) errors.link = 'The link must start with https://.'
  return errors
}

/** Only what differs from the stored recording: the PATCH changes exactly the fields it is sent. */
export function changesOf(recording: Recording, form: Form): RecordingChanges {
  const before = formOf(recording)
  const changes: RecordingChanges = {}
  for (const key of Object.keys(before) as (keyof Form)[]) {
    const value = form[key].trim()
    if (value !== before[key].trim()) changes[key] = value
  }
  return changes
}

export function EditRecordingModal({ recording, onClose }: { recording: Recording | null; onClose: () => void }) {
  const edit = useEditRecording()
  const toast = useToast()
  const { data: me } = useMe()
  const [form, setForm] = useState<Form | null>(null)
  const [submitted, setSubmitted] = useState(false)

  useEffect(() => {
    setForm(recording ? formOf(recording) : null)
    setSubmitted(false)
    edit.reset()
    // Only when another recording is opened, not on every refresh of the same one.
  }, [recording?.id])

  if (!recording || !form) return null
  // A coordinator may only move a recording between their own groups: offer just those.
  const ownGroups = me && !me.allGroups ? [...new Set([recording.group, ...(me.groups ?? []).map((g) => g.name)])].sort() : null
  const errors = validate(form)
  const changes = changesOf(recording, form)
  const nothingChanged = Object.keys(changes).length === 0
  const linkChanges = 'link' in changes

  function set<K extends keyof Form>(key: K, value: string) {
    setForm((f) => (f ? { ...f, [key]: value } : f))
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (Object.keys(errors).length > 0 || nothingChanged || !recording) return
    edit.mutate(
      { id: recording.id, changes },
      {
        onSuccess: (result) => {
          const changed = result.changed.map(fieldLabel).join(', ')
          toast.success(
            `Saved ${result.group} · ${result.date}`,
            `${changed ? `Changed: ${changed}.` : 'Nothing changed.'} LMS status: ${lmsLabel(result.lmsStatus)}.`,
          )
          onClose()
        },
      },
    )
  }

  const serverError = edit.error ? describe(edit.error) : null

  return (
    <Modal
      open
      busy={edit.isPending}
      onClose={onClose}
      title="Edit recording"
      description={`${recording.group} · ${recording.date}${recording.startTime ? ` · ${recording.startTime}` : ''}`}
      footer={
        <>
          <button type="button" className={button.secondary} onClick={onClose} disabled={edit.isPending}>Cancel</button>
          <button type="submit" form="edit-recording" className={button.primary} disabled={edit.isPending || nothingChanged}>
            {edit.isPending && <Spinner label="Saving" />}
            {edit.isPending ? 'Saving…' : 'Save changes'}
          </button>
        </>
      }
    >
      <form id="edit-recording" onSubmit={submit} noValidate className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <Field id="edit-group" label="Group" required error={submitted ? errors.group : undefined}>
          {ownGroups ? (
            <select id="edit-group" data-autofocus className={`${input} w-full`} value={form.group} onChange={(e) => set('group', e.target.value)}>
              {ownGroups.map((name) => <option key={name} value={name}>{name}</option>)}
            </select>
          ) : (
            <input id="edit-group" data-autofocus className={`${input} w-full`} maxLength={100} value={form.group} onChange={(e) => set('group', e.target.value)} />
          )}
        </Field>
        <Field id="edit-date" label="Date" required error={submitted ? errors.date : undefined}>
          <input id="edit-date" type="date" className={`${input} w-full`} value={form.date} onChange={(e) => set('date', e.target.value)} />
        </Field>
        <Field id="edit-start" label="Start time" hint="Optional" error={submitted ? errors.startTime : undefined}>
          <input id="edit-start" type="time" className={`${input} w-full`} value={form.startTime} onChange={(e) => set('startTime', e.target.value)} />
        </Field>
        <Field id="edit-type" label="Type" hint="Optional">
          <input id="edit-type" className={`${input} w-full`} maxLength={50} value={form.type} onChange={(e) => set('type', e.target.value)} />
        </Field>
        <Field id="edit-file" label="File name" hint="Optional" wide>
          <input id="edit-file" className={`${input} w-full`} maxLength={255} value={form.fileName} onChange={(e) => set('fileName', e.target.value)} />
        </Field>
        <Field id="edit-link" label="Link" hint="Google Drive file link or Zoom recording link. Empty removes it." wide error={submitted ? errors.link : undefined}>
          <input id="edit-link" type="url" inputMode="url" className={`${input} w-full font-mono text-xs`} maxLength={2048} value={form.link} onChange={(e) => set('link', e.target.value)} placeholder="https://drive.google.com/file/d/…/view" />
        </Field>

        {linkChanges && (
          <p className="rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 sm:col-span-2">
            Changing the link puts the LMS status back to <strong>Pending</strong>: the LMS may still hold the old link.
          </p>
        )}
        {serverError && (
          <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 sm:col-span-2">{serverError}</p>
        )}
        {submitted && nothingChanged && !serverError && <p className="text-sm text-slate-500 sm:col-span-2">Nothing has changed yet.</p>}
      </form>
    </Modal>
  )
}

function describe(error: Error): string {
  if (error instanceof ApiError) {
    if (error.status === 409) return 'Another recording already exists for that group, date and start time.'
    if (typeof error.details === 'string') return error.details
    if (Array.isArray(error.details)) return 'Some values are not accepted. Check the fields and try again.'
  }
  return error.message
}

function Field({ id, label, hint, error, required, wide, children }: { id: string; label: string; hint?: string; error?: string; required?: boolean; wide?: boolean; children: ReactNode }) {
  return (
    <div className={`flex flex-col gap-1 ${wide ? 'sm:col-span-2' : ''}`}>
      <label htmlFor={id} className="text-sm font-medium text-slate-700">
        {label}
        {required && <span className="text-rose-600"> *</span>}
      </label>
      {children}
      {error ? <p className="text-xs text-rose-600">{error}</p> : hint ? <p className="text-xs text-slate-500">{hint}</p> : null}
    </div>
  )
}
