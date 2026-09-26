import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { useMyZoomAccounts, useSaveZoomAccounts } from '../api/hooks'
import type { MyZoomAccount, PreferredEngine, ZoomAccountInput } from '../api/types'
import { PageHeader } from '../components/Layout'
import { useToast } from '../components/Toast'
import { button, Card, EmptyState, ErrorBanner, input, LoadingRows, Pill, Spinner, td, th, TimeAgo } from '../components/ui'

/** The form's own copy of an account, before it is saved. */
interface Draft {
  accountId: string
  label: string
  zoomEmail: string
  group: string
  meetingUrl: string
  preferredEngine: '' | PreferredEngine
  active: boolean
  /** left empty to keep whatever password is saved */
  password: string
  /** deliberately removing the saved password */
  clearPassword: boolean
}

const empty: Draft = {
  accountId: '', label: '', zoomEmail: '', group: '', meetingUrl: '',
  preferredEngine: '', active: false, password: '', clearPassword: false,
}

function toDraft(account: MyZoomAccount): Draft {
  return {
    accountId: account.accountId,
    label: account.label ?? '',
    zoomEmail: account.zoomEmail ?? '',
    group: account.group ?? '',
    meetingUrl: account.meetingUrl ?? '',
    preferredEngine: account.preferredEngine ?? '',
    active: account.active,
    password: '',
    clearPassword: false,
  }
}

/** An account as the endpoint takes it. A password is only ever sent when one was typed. */
function toInput(account: MyZoomAccount): ZoomAccountInput {
  return {
    accountId: account.accountId,
    label: account.label,
    zoomEmail: account.zoomEmail,
    group: account.group,
    meetingUrl: account.meetingUrl,
    preferredEngine: account.preferredEngine,
    active: account.active,
  }
}

function draftToInput(draft: Draft): ZoomAccountInput {
  const account: ZoomAccountInput = {
    accountId: draft.accountId.trim(),
    label: draft.label.trim(),
    zoomEmail: draft.zoomEmail.trim() || null,
    group: draft.group.trim() || null,
    meetingUrl: draft.meetingUrl.trim() || null,
    preferredEngine: draft.preferredEngine || null,
    active: draft.active,
  }
  if (draft.clearPassword) account.password = ''
  else if (draft.password) account.password = draft.password
  return account
}

/**
 * The Zoom accounts you host classes with - the app's Accounts page, on the web.
 *
 * A meeting is opened by a named account, never by whichever profile a machine happens to have, so
 * this is what makes a class runnable: the account, the group it hosts, its link, and the Zoom
 * password a server signs in with. The password is written here and read nowhere: the server keeps
 * it sealed and hands it only to the machine holding that class.
 *
 * The endpoint takes the whole set at once, so every save sends the list on screen. That is also
 * what makes "Remove" work: an account left out is gone.
 */
export function ZoomAccountsPage() {
  const { data, isLoading, error } = useMyZoomAccounts()
  const save = useSaveZoomAccounts()
  const toast = useToast()
  const accounts = useMemo(() => data?.accounts ?? [], [data])
  const [editing, setEditing] = useState<string | null>(null)
  const [draft, setDraft] = useState<Draft>(empty)
  const [submitted, setSubmitted] = useState(false)

  // An account saved elsewhere (a machine syncing its own) must not overwrite what is being typed.
  useEffect(() => { if (editing === null) setDraft(empty) }, [editing])

  const existing = accounts.find((account) => account.accountId === editing) ?? null
  const isNew = editing === '' || (editing !== null && existing === null)

  const problem =
    !draft.accountId.trim() ? 'A name for the account is required - the one your machines know it by.'
      : isNew && accounts.some((a) => a.accountId.toLowerCase() === draft.accountId.trim().toLowerCase())
        ? 'You already have an account with that name.'
        : draft.meetingUrl.trim() && !draft.meetingUrl.trim().toLowerCase().startsWith('https://')
          ? 'A meeting link must start with https://.'
          : null

  function open(account: MyZoomAccount | null) {
    setSubmitted(false)
    setEditing(account ? account.accountId : '')
    setDraft(account ? toDraft(account) : empty)
  }

  function sendAll(next: ZoomAccountInput[], done: string) {
    save.mutate(next, {
      onSuccess: () => { setEditing(null); setSubmitted(false); toast.success(done) },
    })
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (problem) return
    const saved = draftToInput(draft)
    // Only one account can be the one in use, here as on a PC.
    const others = accounts
      .filter((account) => account.accountId !== editing)
      .map((account) => (saved.active ? { ...toInput(account), active: false } : toInput(account)))
    sendAll([...others, saved], isNew ? `${saved.accountId} added.` : `${saved.accountId} saved.`)
  }

  function remove(account: MyZoomAccount) {
    if (!window.confirm(`Remove ${account.accountId}? Classes that open with it will have no account until another one hosts their group.`)) return
    sendAll(accounts.filter((other) => other.accountId !== account.accountId).map(toInput), `${account.accountId} removed.`)
  }

  function use(account: MyZoomAccount) {
    sendAll(accounts.map((other) => ({ ...toInput(other), active: other.accountId === account.accountId })), `${account.accountId} is the one in use.`)
  }

  const failure = save.error instanceof ApiError && typeof save.error.details === 'string' ? save.error.details : save.error?.message

  return (
    <>
      <PageHeader
        title="Zoom accounts"
        description="The accounts your classes are hosted with. A class opens with the account that hosts its group."
        action={<button type="button" className={button.primary} onClick={() => open(null)}>Add an account</button>}
      />

      {save.isError && <p role="alert" className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{failure}</p>}

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <Card title={`${accounts.length} account${accounts.length === 1 ? '' : 's'}`}>
          {error ? <ErrorBanner error={error} /> : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[46rem]">
                <thead className="border-b border-slate-100 dark:border-slate-800/80 bg-slate-50/60 dark:bg-[#0c111d]">
                  <tr>
                    <th className={th}>Account</th>
                    <th className={th}>Group</th>
                    <th className={th}>Opens with</th>
                    <th className={th}>Password</th>
                    <th className={th}>Saved</th>
                    <th className={th}><span className="sr-only">Actions</span></th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-slate-800/80">
                  {isLoading && <LoadingRows columns={6} />}
                  {!isLoading && accounts.length === 0 && (
                    <tr><td colSpan={6}><EmptyState>No Zoom account yet. Add the one your classes are hosted with.</EmptyState></td></tr>
                  )}
                  {accounts.map((account) => (
                    <tr key={account.id} className="hover:bg-slate-50/60 dark:hover:bg-[#161d2f]/40 transition-colors">
                      <td className={td}>
                        <p className="font-semibold text-slate-900 dark:text-slate-100">{account.accountId}</p>
                        <p className="text-xs text-slate-500 dark:text-slate-400">{account.zoomEmail || account.label || '—'}</p>
                        {account.active && <Pill tone="green">In use</Pill>}
                      </td>
                      <td className={td}>{account.group || <span className="text-slate-400">any</span>}</td>
                      <td className={td}>
                        {account.preferredEngine === 'web' ? 'Zoom in a browser' : account.preferredEngine === 'desktop' ? 'The Zoom app' : <span className="text-slate-400">whatever the machine has</span>}
                        {account.meetingUrl && <p className="text-xs text-slate-500 dark:text-slate-400">has a meeting link</p>}
                      </td>
                      <td className={td}>
                        {account.hasPassword
                          ? <Pill tone="green">Saved</Pill>
                          : <span title="A server cannot sign in as this account without one."><Pill tone="amber">None</Pill></span>}
                      </td>
                      <td className={`${td} text-slate-500`}><TimeAgo iso={account.updatedAt} /></td>
                      <td className={`${td} text-right`}>
                        <span className="inline-flex gap-1.5">
                          {!account.active && (
                            <button type="button" className={button.small} onClick={() => use(account)} disabled={save.isPending}>Use</button>
                          )}
                          <button type="button" className={button.small} onClick={() => open(account)} aria-label={`Edit ${account.accountId}`}>Edit</button>
                          <button type="button" className={button.smallDanger} onClick={() => remove(account)} disabled={save.isPending} aria-label={`Remove ${account.accountId}`}>Remove</button>
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
          <Card title={isNew ? 'New Zoom account' : `Edit ${editing}`}>
            <form onSubmit={submit} noValidate aria-label="Zoom account" className="flex flex-col gap-4 px-5 py-4">
              <Field label="Account name" hint="What your machines know it by, for example eyouth-1.">
                <input className={`${input} w-full`} value={draft.accountId} maxLength={100} onChange={(e) => setDraft({ ...draft, accountId: e.target.value })} />
              </Field>
              <Field label="Zoom email" hint="The address it signs in with.">
                <input className={`${input} w-full`} type="email" value={draft.zoomEmail} maxLength={320} onChange={(e) => setDraft({ ...draft, zoomEmail: e.target.value })} />
              </Field>
              <Field label="Group it hosts" hint="The classes of this group open with this account. Leave empty for any group.">
                <input className={`${input} w-full`} value={draft.group} maxLength={100} onChange={(e) => setDraft({ ...draft, group: e.target.value })} />
              </Field>
              <Field label="Meeting link" hint="Where this account's classes open. This is where a class's link comes from.">
                <input className={`${input} w-full`} inputMode="url" value={draft.meetingUrl} onChange={(e) => setDraft({ ...draft, meetingUrl: e.target.value })} />
              </Field>
              <Field label="Opens with">
                <select className={`${input} w-full`} value={draft.preferredEngine} onChange={(e) => setDraft({ ...draft, preferredEngine: e.target.value as Draft['preferredEngine'] })}>
                  <option value="">Whatever the machine has</option>
                  <option value="web">Zoom in a browser (what a server uses)</option>
                  <option value="desktop">The Zoom app</option>
                </select>
              </Field>
              <Field
                label={existing?.hasPassword ? 'Zoom password (a password is saved)' : 'Zoom password'}
                hint={existing?.hasPassword ? 'Leave empty to keep the saved one.' : 'Without one, a server cannot open this account’s meetings.'}
              >
                <input
                  className={`${input} w-full`} type="password" autoComplete="new-password" maxLength={500}
                  value={draft.password} disabled={draft.clearPassword}
                  onChange={(e) => setDraft({ ...draft, password: e.target.value })}
                />
              </Field>
              {existing?.hasPassword && (
                <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                  <input type="checkbox" className="size-4 rounded border-slate-300 accent-indigo-600" checked={draft.clearPassword} onChange={(e) => setDraft({ ...draft, clearPassword: e.target.checked, password: '' })} />
                  Remove the saved password
                </label>
              )}
              <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                <input type="checkbox" className="size-4 rounded border-slate-300 accent-indigo-600" checked={draft.active} onChange={(e) => setDraft({ ...draft, active: e.target.checked })} />
                This is the account in use
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

/**
 * A labelled field. The hint sits outside the label on purpose: inside it, a screen reader reads the
 * whole sentence as the field's name, so "Zoom password" becomes "Zoom password Leave empty to keep
 * the saved one".
 */
export function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block">
        <span className="mb-1.5 block text-sm font-medium text-slate-700">{label}</span>
        {children}
      </label>
      {hint && <p className="mt-1 text-xs text-slate-500">{hint}</p>}
    </div>
  )
}
