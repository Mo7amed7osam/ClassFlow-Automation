import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { useChangePassword, useMe } from '../api/hooks'
import { PageHeader } from '../components/Layout'
import { useToast } from '../components/Toast'
import { button, Card, input, Pill, Spinner } from '../components/ui'
import { formatDateTime } from '../lib/format'
import { MIN_PASSWORD } from './RegisterPage'

/** Who is signed in, what they see, and their own password. */
export function AccountPage() {
  const { data: me } = useMe()
  const change = useChangePassword()
  const toast = useToast()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [submitted, setSubmitted] = useState(false)

  const problem =
    next.length < MIN_PASSWORD ? `At least ${MIN_PASSWORD} characters.`
      : next.trim().toLowerCase() === me?.username ? 'The password cannot be the username.'
        : next === current ? 'Choose a password different from the current one.'
          : confirm !== next ? 'The two new passwords differ.'
            : null

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (!current || problem) return
    change.mutate({ currentPassword: current, newPassword: next }, {
      onSuccess: () => {
        setSubmitted(false)
        toast.success('Password changed', 'Other browsers signed in as you were signed out.')
      },
      onSettled: () => { setCurrent(''); setNext(''); setConfirm('') },
    })
  }

  if (!me) return null
  const error = change.error instanceof ApiError && typeof change.error.details === 'string' ? change.error.details : change.error?.message

  return (
    <>
      <PageHeader title="Your account" />
      <div className="grid gap-5 lg:grid-cols-2">
        <Card title="Profile">
          <dl className="grid grid-cols-[8rem_1fr] gap-y-3 px-5 py-4 text-sm">
            <dt className="text-slate-500">Name</dt>
            <dd className="font-medium text-slate-900">{me.displayName}</dd>
            <dt className="text-slate-500">Username</dt>
            <dd className="text-slate-800">{me.username}</dd>
            <dt className="text-slate-500">Role</dt>
            <dd><Pill tone={me.role === 'admin' ? 'blue' : 'slate'}>{me.role === 'admin' ? 'Admin' : 'Coordinator'}</Pill></dd>
            <dt className="text-slate-500">Groups</dt>
            <dd className="flex flex-wrap gap-1.5">
              {me.allGroups ? (
                <span className="text-slate-700">All groups</span>
              ) : me.groups?.length ? (
                me.groups.map((g) => <Pill key={g.id} tone="green">{g.name}</Pill>)
              ) : (
                <span className="text-slate-500">None yet. The admin assigns your groups.</span>
              )}
            </dd>
            <dt className="text-slate-500">Session ends</dt>
            <dd className="text-slate-700">{formatDateTime(me.expiresAt)}</dd>
          </dl>
        </Card>

        <Card title="Change password">
          <form onSubmit={submit} noValidate aria-label="Change password" className="flex flex-col gap-4 px-5 py-4">
            <div>
              <label htmlFor="pw-current" className="mb-1.5 block text-sm font-medium text-slate-700">Current password</label>
              <input id="pw-current" type="password" autoComplete="current-password" maxLength={200} className={`${input} w-full`} value={current} onChange={(e) => setCurrent(e.target.value)} />
              {submitted && !current && <p className="mt-1 text-xs text-rose-700">Enter your current password.</p>}
            </div>
            <div>
              <label htmlFor="pw-new" className="mb-1.5 block text-sm font-medium text-slate-700">New password</label>
              <input id="pw-new" type="password" autoComplete="new-password" maxLength={200} className={`${input} w-full`} value={next} onChange={(e) => setNext(e.target.value)} />
              <p className="mt-1 text-xs text-slate-500">At least {MIN_PASSWORD} characters.</p>
            </div>
            <div>
              <label htmlFor="pw-confirm" className="mb-1.5 block text-sm font-medium text-slate-700">New password again</label>
              <input id="pw-confirm" type="password" autoComplete="new-password" maxLength={200} className={`${input} w-full`} value={confirm} onChange={(e) => setConfirm(e.target.value)} />
            </div>
            {submitted && problem && <p className="text-xs text-rose-700">{problem}</p>}
            {change.isError && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{error}</p>}
            <div>
              <button type="submit" className={button.primary} disabled={change.isPending}>
                {change.isPending && <Spinner label="Saving" />}
                {change.isPending ? 'Saving…' : 'Change password'}
              </button>
            </div>
          </form>
        </Card>
      </div>
    </>
  )
}
