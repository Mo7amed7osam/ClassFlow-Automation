import { useState, type FormEvent } from 'react'
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router'
import { ApiError } from '../api/client'
import { useLogin, useMe } from '../api/hooks'
import { input } from '../components/ui'

function safeNext(next: string | null): string {
  // Only paths inside the dashboard, never another site.
  return next && next.startsWith('/') && !next.startsWith('//') ? next : '/'
}

/** Why the sign-in did not work, in words for the person at the keyboard. */
export function loginMessage(error: Error | null): string | null {
  if (!error) return null
  if (!(error instanceof ApiError)) return error.message
  if (error.status === 401) return 'Invalid username or password.'
  if (error.status === 429) return 'Too many attempts. Wait a few minutes and try again.'
  const reason = (error.details as { reason?: string } | null)?.reason
  if (error.status === 403 && reason === 'pending') return "Your account is waiting for the admin's approval. You can sign in once it is approved."
  if (error.status === 403 && reason === 'rejected') return 'Your account request was not approved. Contact the admin.'
  if (error.status === 403 && reason === 'disabled') return 'Your account is disabled. Contact the admin.'
  return error.message
}

export function LoginPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const { data: me } = useMe()
  const login = useLogin()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const next = safeNext(params.get('next'))

  if (me) return <Navigate to={next} replace />

  function submit(event: FormEvent) {
    event.preventDefault()
    login.mutate({ username, password }, {
      onSuccess: () => navigate(next, { replace: true }),
      onSettled: () => setPassword(''),
    })
  }

  const message = loginMessage(login.error)

  return (
    <div className="grid min-h-full place-items-center bg-slate-100 px-4">
      <form onSubmit={submit} className="w-full max-w-sm rounded-2xl border border-slate-200 bg-white p-7 shadow-sm" aria-label="Sign in">
        <div className="mb-6 flex items-center gap-2.5">
          <span className="grid size-9 place-items-center rounded-lg bg-teal-600 font-bold text-white">Z</span>
          <div>
            <h1 className="text-base font-semibold text-slate-900">Zoom Auto Admit</h1>
            <p className="text-xs text-slate-500">Recordings dashboard</p>
          </div>
        </div>

        <label className="mb-1.5 block text-sm font-medium text-slate-700" htmlFor="username">Username</label>
        <input id="username" className={`${input} mb-4 w-full`} autoComplete="username" autoFocus required value={username} onChange={(e) => setUsername(e.target.value)} />

        <label className="mb-1.5 block text-sm font-medium text-slate-700" htmlFor="password">Password</label>
        <input id="password" type="password" className={`${input} mb-5 w-full`} autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />

        {message && (
          <p role="alert" className="mb-4 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">
            {message}
          </p>
        )}

        <button type="submit" disabled={login.isPending} className="h-10 w-full rounded-lg bg-teal-700 text-sm font-semibold text-white hover:bg-teal-800 disabled:opacity-60">
          {login.isPending ? 'Signing in…' : 'Sign in'}
        </button>
        <p className="mt-5 text-center text-sm text-slate-500">
          Coordinator without an account? <Link to="/register" className="font-medium text-teal-700 hover:underline">Request one</Link>
        </p>
      </form>
    </div>
  )
}
