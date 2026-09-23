import { useState } from 'react'
import { ApiError } from '../api/client'
import { useCloudPolicy, useEnrollDevice, useMe, useSaveCloudPolicy, useSaveSetting, useSetting } from '../api/hooks'
import type { CloudPolicy, RecordingsSheet } from '../api/types'
import { PageHeader } from '../components/Layout'
import { useToast } from '../components/Toast'
import { button, Card, input, Pill, Spinner, TimeAgo } from '../components/ui'
import { Field } from './ZoomAccountsPage'

/** What a worker does when nothing has been saved: the same defaults the server answers with. */
export const POLICY_DEFAULTS: CloudPolicy = { autoCoHost: true, autoEnd: true }

function Switch({ label, hint, checked, onChange, disabled }: {
  label: string
  hint: string
  checked: boolean
  onChange: (value: boolean) => void
  disabled?: boolean
}) {
  return (
    <label className="flex items-start gap-3 border-t border-slate-100 px-5 py-4 first:border-t-0">
      <input
        type="checkbox" className="mt-0.5 size-4 rounded border-slate-300" checked={checked} disabled={disabled}
        onChange={(event) => onChange(event.target.checked)}
      />
      <span>
        <span className="block text-sm font-medium text-slate-800">{label}</span>
        <span className="mt-0.5 block text-xs text-slate-500">{hint}</span>
      </span>
    </label>
  )
}

/**
 * The settings every machine reads - what the app keeps beside a meeting, for the machines that have
 * no window: whether the instructor is made co-host, and whether a class is ended when it is over.
 *
 * A worker reads these when it takes a class, so a switch changed here applies to the next class
 * rather than the one already running - a class does not change its behaviour halfway.
 */
export function SettingsPage() {
  const { data: me } = useMe()
  const isAdmin = me?.role === 'admin'
  const policy = useCloudPolicy()
  const savePolicy = useSaveCloudPolicy()
  const sheet = useSetting<RecordingsSheet>('recordingsSheet', isAdmin)
  const saveSheet = useSaveSetting<RecordingsSheet>('recordingsSheet')
  const enroll = useEnrollDevice()
  const toast = useToast()

  const current = { ...POLICY_DEFAULTS, ...(policy.data?.value ?? {}) }
  const [sheetId, setSheetId] = useState<string | null>(null)
  const [machine, setMachine] = useState('')
  const [token, setToken] = useState<string | null>(null)

  function set(change: Partial<CloudPolicy>) {
    savePolicy.mutate({ ...current, ...change }, {
      onSuccess: () => toast.success('Saved.', 'Every machine reads this before it takes its next class.'),
    })
  }

  const failed = [savePolicy.error, saveSheet.error, enroll.error].find(Boolean)
  const message = failed instanceof ApiError && typeof failed.details === 'string' ? failed.details : failed?.message
  const savedSheetId = typeof sheet.data?.value?.spreadsheetId === 'string' ? sheet.data.value.spreadsheetId : ''

  return (
    <>
      <PageHeader title="Settings" description="What every machine does while it holds a class, and how a machine joins." />

      {failed && <p role="alert" className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{message}</p>}

      <div className="grid gap-5 lg:grid-cols-2">
        <Card
          title="While a class runs"
          action={policy.data?.updatedAt ? <span className="text-xs text-slate-500">saved <TimeAgo iso={policy.data.updatedAt} /></span> : null}
        >
          <Switch
            label="Make the instructor co-host"
            hint="As people arrive, whoever is named on Who is made co-host is given co-host, and made co-host again if they drop out and come back."
            checked={current.autoCoHost}
            disabled={!isAdmin || savePolicy.isPending}
            onChange={(value) => set({ autoCoHost: value })}
          />
          <Switch
            label="End a class when it is over"
            hint="From three hours after the class's time: once nobody but the host is left, or the instructor and most of the class have gone for five minutes. Never while a mic is on, and never while breakout rooms are open."
            checked={current.autoEnd}
            disabled={!isAdmin || savePolicy.isPending}
            onChange={(value) => set({ autoEnd: value })}
          />
          <p className="border-t border-slate-100 px-5 py-3 text-xs text-slate-500">
            {isAdmin
              ? 'A class already running keeps the settings it started with.'
              : 'Only the admin changes these. They apply to every machine.'}
          </p>
        </Card>

        {isAdmin && (
          <Card title="A machine of yours">
            <div className="flex flex-col gap-4 px-5 py-4">
              <p className="text-sm text-slate-600">
                A machine joins with a token it spends at once. It is single-use and short-lived, and grants no more
                than registering: nothing that reads a password.
              </p>
              <Field label="A name for it" hint="Shown on Agents, so you know which machine is which.">
                <input className={`${input} w-full`} value={machine} maxLength={100} onChange={(event) => setMachine(event.target.value)} />
              </Field>
              <div>
                <button
                  type="button" className={button.primary} disabled={enroll.isPending}
                  onClick={() => enroll.mutate(machine.trim(), { onSuccess: (answer) => setToken(answer.enrollmentToken) })}
                >
                  {enroll.isPending && <Spinner label="Making a token" />}
                  {enroll.isPending ? 'Making a token…' : 'Make an enrollment token'}
                </button>
              </div>
              {token && (
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3">
                  <p className="text-xs font-medium uppercase tracking-wide text-slate-500">Enrollment token</p>
                  <code className="mt-1 block break-all font-mono text-xs text-slate-800">{token}</code>
                  <p className="mt-2 text-xs text-slate-500">
                    Give it to the machine as CLASSFLOW_ENROLLMENT_TOKEN (or paste it into the app). It works once, and
                    expires shortly.
                  </p>
                  <button type="button" className={`${button.small} mt-2`} onClick={() => { void navigator.clipboard?.writeText(token); toast.success('Copied.') }}>
                    Copy
                  </button>
                </div>
              )}
            </div>
          </Card>
        )}

        {isAdmin && (
          <Card
            title="Recordings sheet"
            action={sheet.data?.updatedAt ? <span className="text-xs text-slate-500">saved <TimeAgo iso={sheet.data.updatedAt} /></span> : null}
          >
            <div className="flex flex-col gap-4 px-5 py-4">
              <p className="text-sm text-slate-600">
                The sheet the recordings are read from. A machine that syncs recordings reads this, so every machine
                looks at the same sheet.
              </p>
              <Field label="Spreadsheet id" hint="The long id from the sheet's own address.">
                <input
                  className={`${input} w-full`} value={sheetId ?? savedSheetId}
                  onChange={(event) => setSheetId(event.target.value)}
                />
              </Field>
              <div className="flex items-center gap-2">
                <button
                  type="button" className={button.primary}
                  disabled={saveSheet.isPending || sheetId === null || sheetId.trim() === savedSheetId}
                  onClick={() => saveSheet.mutate({ ...(sheet.data?.value ?? {}), spreadsheetId: (sheetId ?? '').trim() }, {
                    onSuccess: () => { setSheetId(null); toast.success('Saved.') },
                  })}
                >
                  {saveSheet.isPending && <Spinner label="Saving" />}
                  {saveSheet.isPending ? 'Saving…' : 'Save'}
                </button>
                {savedSheetId ? <Pill tone="green">A sheet is set</Pill> : <Pill tone="amber">None set</Pill>}
              </div>
            </div>
          </Card>
        )}
      </div>
    </>
  )
}
