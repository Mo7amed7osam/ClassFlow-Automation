import { useMemo, useState, type FormEvent, type ReactNode } from 'react'
import { ApiError } from '../api/client'
import { useAdminGroups, useCreateUser, useResetPassword, useSetUserGroups } from '../api/hooks'
import type { User } from '../api/types'
import { accountProblems, MIN_PASSWORD } from '../pages/RegisterPage'
import { Modal } from './Overlay'
import { useToast } from './Toast'
import { button, input, Spinner } from './ui'

/** The server's reason, when it gave one in words. */
export function reason(error: unknown): string {
  if (error instanceof ApiError) return typeof error.details === 'string' ? error.details : error.message
  return error instanceof Error ? error.message : 'Something went wrong.'
}

/** Tick the groups a coordinator sees. Archived groups cannot be given out. */
export function GroupPicker({ selected, onChange }: { selected: Set<string>; onChange: (next: Set<string>) => void }) {
  const groups = useAdminGroups()
  const [filter, setFilter] = useState('')
  const active = useMemo(() => (groups.data?.groups ?? []).filter((g) => !g.archived), [groups.data])
  const shown = active.filter((g) => `${g.group} ${g.displayName ?? ''}`.toLowerCase().includes(filter.trim().toLowerCase()))

  function toggle(id: string) {
    const next = new Set(selected)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    onChange(next)
  }

  if (groups.isLoading) return <p className="text-sm text-slate-500">Loading groups…</p>
  if (groups.error) return <p role="alert" className="text-sm text-rose-700">{reason(groups.error)}</p>
  if (active.length === 0) return <p className="text-sm text-slate-500">No groups yet. They appear with their first recording, or add one on the Groups page.</p>

  return (
    <fieldset>
      <legend className="sr-only">Groups</legend>
      <div className="mb-2 flex items-center justify-between gap-3">
        <input aria-label="Filter groups" placeholder="Filter groups" className={`${input} h-8 flex-1`} value={filter} onChange={(e) => setFilter(e.target.value)} />
        <span className="shrink-0 text-xs text-slate-500">{selected.size} selected</span>
      </div>
      <div className="max-h-56 overflow-y-auto rounded-lg border border-slate-200">
        {shown.map((g) => (
          <label key={g.id} className="flex cursor-pointer items-center gap-3 border-b border-slate-100 px-3 py-2 text-sm last:border-b-0 hover:bg-slate-50">
            <input type="checkbox" className="size-4 accent-teal-700" checked={selected.has(g.id)} onChange={() => toggle(g.id)} />
            <span className="min-w-0 flex-1">
              <span className="font-medium text-slate-800">{g.group}</span>
              {g.displayName && <span className="ml-2 text-slate-500">{g.displayName}</span>}
            </span>
            <span className="shrink-0 text-xs tabular-nums text-slate-400">{g.recordings} rec.</span>
          </label>
        ))}
        {shown.length === 0 && <p className="px-3 py-3 text-sm text-slate-500">No group matches.</p>}
      </div>
    </fieldset>
  )
}

function Field({ id, label, hint, error, children }: { id: string; label: string; hint?: string; error?: string; children: ReactNode }) {
  return (
    <div>
      <label htmlFor={id} className="mb-1.5 block text-sm font-medium text-slate-700">{label}</label>
      {children}
      {error ? <p className="mt-1 text-xs text-rose-700">{error}</p> : hint ? <p className="mt-1 text-xs text-slate-500">{hint}</p> : null}
    </div>
  )
}

export function CreateUserModal({ onClose }: { onClose: () => void }) {
  const create = useCreateUser()
  const toast = useToast()
  const [username, setUsername] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [groups, setGroups] = useState<Set<string>>(new Set())
  const [submitted, setSubmitted] = useState(false)
  const problems = accountProblems(username, displayName, password, confirm)

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (Object.keys(problems).length > 0) return
    create.mutate(
      { username: username.trim().toLowerCase(), displayName: displayName.trim(), password, groupIds: [...groups] },
      {
        onSuccess: (user) => {
          toast.success(`Account ${user.username} created`, 'Give them the password yourself; it is not shown again.')
          onClose()
        },
      },
    )
  }

  const shown = (key: string) => (submitted ? problems[key] : undefined)
  return (
    <Modal
      open
      busy={create.isPending}
      onClose={onClose}
      title="New coordinator"
      description="The account is active at once; no approval needed."
      footer={
        <>
          <button type="button" className={button.secondary} onClick={onClose} disabled={create.isPending}>Cancel</button>
          <button type="submit" form="create-user" className={button.primary} disabled={create.isPending}>
            {create.isPending && <Spinner label="Creating" />}
            {create.isPending ? 'Creating…' : 'Create account'}
          </button>
        </>
      }
    >
      <form id="create-user" onSubmit={submit} noValidate className="flex flex-col gap-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field id="new-name" label="Name" error={shown('displayName')}>
            <input id="new-name" data-autofocus className={`${input} w-full`} maxLength={100} value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          </Field>
          <Field id="new-username" label="Username" hint="a-z 0-9 . _ -" error={shown('username')}>
            <input id="new-username" autoComplete="off" className={`${input} w-full`} maxLength={50} value={username} onChange={(e) => setUsername(e.target.value)} />
          </Field>
          <Field id="new-password" label="Password" hint={`At least ${MIN_PASSWORD} characters.`} error={shown('password')}>
            <input id="new-password" type="password" autoComplete="new-password" className={`${input} w-full`} maxLength={200} value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          <Field id="new-confirm" label="Password again" error={shown('confirm')}>
            <input id="new-confirm" type="password" autoComplete="new-password" className={`${input} w-full`} maxLength={200} value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          </Field>
        </div>
        <div>
          <p className="mb-1.5 text-sm font-medium text-slate-700">Groups</p>
          <GroupPicker selected={groups} onChange={setGroups} />
        </div>
        {create.isError && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{reason(create.error)}</p>}
      </form>
    </Modal>
  )
}

export function AssignGroupsModal({ user, onClose }: { user: User; onClose: () => void }) {
  const save = useSetUserGroups()
  const toast = useToast()
  const [groups, setGroups] = useState<Set<string>>(() => new Set(user.groups.filter((g) => !g.archived).map((g) => g.id)))

  function submit() {
    save.mutate({ id: user.id, groupIds: [...groups] }, {
      onSuccess: (saved) => {
        toast.success(`Groups saved for ${saved.displayName}`, saved.groups.length ? saved.groups.map((g) => g.name).join(', ') : 'No groups: they see nothing.')
        onClose()
      },
    })
  }

  return (
    <Modal
      open
      busy={save.isPending}
      onClose={onClose}
      title={`Groups of ${user.displayName}`}
      description="They see these groups' recordings only, from their next click."
      footer={
        <>
          <button type="button" className={button.secondary} onClick={onClose} disabled={save.isPending}>Cancel</button>
          <button type="button" className={button.primary} onClick={submit} disabled={save.isPending}>
            {save.isPending && <Spinner label="Saving" />}
            {save.isPending ? 'Saving…' : 'Save groups'}
          </button>
        </>
      }
    >
      <GroupPicker selected={groups} onChange={setGroups} />
      {save.isError && <p role="alert" className="mt-3 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{reason(save.error)}</p>}
    </Modal>
  )
}

export function ResetPasswordModal({ user, onClose }: { user: User; onClose: () => void }) {
  const reset = useResetPassword()
  const toast = useToast()
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [submitted, setSubmitted] = useState(false)
  const problem =
    password.length < MIN_PASSWORD ? `At least ${MIN_PASSWORD} characters.`
      : password.trim().toLowerCase() === user.username ? 'The password cannot be the username.'
        : confirm !== password ? 'The two passwords differ.'
          : null

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (problem) return
    reset.mutate({ id: user.id, password }, {
      onSuccess: () => {
        toast.success(`New password set for ${user.displayName}`, 'They were signed out everywhere.')
        onClose()
      },
      onSettled: () => { setPassword(''); setConfirm('') },
    })
  }

  return (
    <Modal
      open
      busy={reset.isPending}
      onClose={onClose}
      title={`New password for ${user.displayName}`}
      description="For someone who forgot theirs. They are signed out everywhere; tell them the new one yourself."
      footer={
        <>
          <button type="button" className={button.secondary} onClick={onClose} disabled={reset.isPending}>Cancel</button>
          <button type="submit" form="reset-password" className={button.primary} disabled={reset.isPending}>
            {reset.isPending ? 'Saving…' : 'Set password'}
          </button>
        </>
      }
    >
      <form id="reset-password" onSubmit={submit} noValidate className="flex flex-col gap-4">
        <Field id="reset-new" label="New password" hint={`At least ${MIN_PASSWORD} characters.`}>
          <input id="reset-new" data-autofocus type="password" autoComplete="new-password" className={`${input} w-full`} maxLength={200} value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Field id="reset-confirm" label="New password again" error={submitted ? problem ?? undefined : undefined}>
          <input id="reset-confirm" type="password" autoComplete="new-password" className={`${input} w-full`} maxLength={200} value={confirm} onChange={(e) => setConfirm(e.target.value)} />
        </Field>
        {reset.isError && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{reason(reset.error)}</p>}
      </form>
    </Modal>
  )
}

/** "Are you sure?" for the changes that lock someone out. */
export function ConfirmModal({ title, body, confirm, dismiss = 'Cancel', danger = false, busy, error, onConfirm, onClose }: {
  title: string
  body: ReactNode
  confirm: string
  /** The button that closes without doing anything. */
  dismiss?: string
  danger?: boolean
  busy: boolean
  error?: unknown
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <Modal
      open
      busy={busy}
      onClose={onClose}
      title={title}
      footer={
        <>
          <button type="button" className={button.secondary} onClick={onClose} disabled={busy}>{dismiss}</button>
          <button
            type="button"
            data-autofocus
            onClick={onConfirm}
            disabled={busy}
            className={danger ? button.danger : button.primary}
          >
            {busy && <Spinner label="Working" />}
            {confirm}
          </button>
        </>
      }
    >
      <div className="text-sm text-slate-700">{body}</div>
      {error ? <p role="alert" className="mt-3 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{reason(error)}</p> : null}
    </Modal>
  )
}
