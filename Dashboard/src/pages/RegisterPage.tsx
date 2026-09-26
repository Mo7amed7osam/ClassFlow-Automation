import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, Navigate } from 'react-router'
import { ApiError } from '../api/client'
import { useMe, useRegister } from '../api/hooks'
import { input } from '../components/ui'

export const USERNAME = /^[a-z0-9][a-z0-9._-]{2,49}$/
export const MIN_PASSWORD = 12

/** What is wrong with a new account's details before the server is asked (it checks them again). */
export function accountProblems(username: string, displayName: string, password: string, confirm: string): Record<string, string> {
  const problems: Record<string, string> = {}
  if (!USERNAME.test(username.trim().toLowerCase())) problems.username = '3-50 characters: lower-case letters, digits and . _ -'
  if (!displayName.trim()) problems.displayName = 'Your name is required.'
  if (password.length < MIN_PASSWORD) problems.password = `At least ${MIN_PASSWORD} characters.`
  else if (password.trim().toLowerCase() === username.trim().toLowerCase()) problems.password = 'The password cannot be the username.'
  if (confirm !== password) problems.confirm = 'The two passwords differ.'
  return problems
}

function registerMessage(error: Error | null): string | null {
  if (!error) return null
  if (error instanceof ApiError && error.status === 409) return 'That username is taken. Choose another.'
  if (error instanceof ApiError && error.status === 429) return 'Too many requests from here. Try again in an hour.'
  if (error instanceof ApiError && error.message === 'Registration is closed') return 'Sign-up is closed. Ask the admin to create your account.'
  if (error instanceof ApiError && typeof error.details === 'string') return error.details
  return error.message
}

/** A coordinator asks for an account. It can be used once the admin approves it. */
export function RegisterPage() {
  const { data: me } = useMe()
  const register = useRegister()
  const [username, setUsername] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [submitted, setSubmitted] = useState(false)

  if (me) return <Navigate to="/" replace />
  const problems = accountProblems(username, displayName, password, confirm)

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (Object.keys(problems).length > 0) return
    register.mutate(
      { username: username.trim().toLowerCase(), displayName: displayName.trim(), password },
      { onSettled: () => { setPassword(''); setConfirm('') } },
    )
  }

  const shell = (children: ReactNode) => (
    <div className="grid min-h-full place-items-center bg-slate-100 dark:bg-[#090d16] px-4 py-8">
      <div className="w-full max-w-sm rounded-2xl border border-slate-200/80 bg-white p-7 shadow-xs dark:border-slate-800/80 dark:bg-[#111726] dark:shadow-none ring-1 ring-slate-900/5 dark:ring-white/[0.03]">
        <div className="mb-6 flex items-center gap-2.5">
          <span className="grid size-9 place-items-center rounded-xl bg-indigo-600 font-bold text-white shadow-xs">C</span>
          <div>
            <h1 className="text-base font-bold text-slate-900 dark:text-slate-100">Request an account</h1>
            <p className="text-xs text-slate-500 dark:text-slate-400">For coordinators · the admin approves each request</p>
          </div>
        </div>
        {children}
      </div>
    </div>
  )

  if (register.isSuccess) {
    return shell(
      <div role="status">
        <p className="rounded-xl bg-emerald-50 dark:bg-emerald-950/40 px-3 py-3 text-sm text-emerald-800 dark:text-emerald-200">
          Your request for <span className="font-semibold">{register.data.username}</span> was sent. The admin needs to approve it
          and give you your groups; after that you can sign in.
        </p>
        <Link to="/login" className="mt-5 inline-flex h-10 w-full items-center justify-center rounded-xl bg-indigo-600 text-sm font-semibold text-white shadow-xs hover:bg-indigo-500 active:bg-indigo-700 transition-colors">
          Back to sign in
        </Link>
      </div>,
    )
  }

  const message = registerMessage(register.error)
  const field = (id: string, label: string, error: string | undefined, control: ReactNode, hint?: string) => (
    <div className="mb-4">
      <label className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300" htmlFor={id}>{label}</label>
      {control}
      {submitted && error ? (
        <p id={`${id}-error`} className="mt-1 text-xs text-rose-700 dark:text-rose-400">{error}</p>
      ) : hint ? (
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{hint}</p>
      ) : null}
    </div>
  )
  const invalid = (key: string) => (submitted && problems[key] ? { 'aria-invalid': true, 'aria-describedby': `reg-${key}-error` } : {})

  return shell(
    <form onSubmit={submit} noValidate aria-label="Request an account">
      {field('reg-displayName', 'Your name', problems.displayName,
        <input id="reg-displayName" className={`${input} w-full`} autoComplete="name" autoFocus maxLength={100} value={displayName} onChange={(e) => setDisplayName(e.target.value)} {...invalid('displayName')} />)}
      {field('reg-username', 'Username', problems.username,
        <input id="reg-username" className={`${input} w-full`} autoComplete="username" maxLength={50} value={username} onChange={(e) => setUsername(e.target.value)} {...invalid('username')} />,
        'Lower-case letters, digits and . _ - (3-50).')}
      {field('reg-password', 'Password', problems.password,
        <input id="reg-password" type="password" className={`${input} w-full`} autoComplete="new-password" maxLength={200} value={password} onChange={(e) => setPassword(e.target.value)} {...invalid('password')} />,
        `At least ${MIN_PASSWORD} characters.`)}
      {field('reg-confirm', 'Password again', problems.confirm,
        <input id="reg-confirm" type="password" className={`${input} w-full`} autoComplete="new-password" maxLength={200} value={confirm} onChange={(e) => setConfirm(e.target.value)} {...invalid('confirm')} />)}

      {message && <p role="alert" className="mb-4 rounded-lg bg-rose-50 dark:bg-rose-950/40 px-3 py-2 text-sm text-rose-700 dark:text-rose-300">{message}</p>}

      <button type="submit" disabled={register.isPending} className="h-10 w-full rounded-xl bg-indigo-600 text-sm font-semibold text-white shadow-xs hover:bg-indigo-500 active:bg-indigo-700 disabled:opacity-60 transition-colors">
        {register.isPending ? 'Sending…' : 'Send request'}
      </button>
      <p className="mt-5 text-center text-sm text-slate-500 dark:text-slate-400">
        Already have an account? <Link to="/login" className="font-semibold text-indigo-600 hover:underline dark:text-indigo-400">Sign in</Link>
      </p>
    </form>,
  )
}
