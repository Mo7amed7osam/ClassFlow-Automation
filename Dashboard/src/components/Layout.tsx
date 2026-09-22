import type { ReactNode } from 'react'
import { NavLink, Outlet, useNavigate } from 'react-router'
import { useLogout, useMe, useUsers } from '../api/hooks'
import type { Role } from '../api/types'

interface NavItem {
  to: string
  label: string
  end: boolean
  badge?: number
}

/** What each role finds in the menu. The backend enforces the same split on every request. */
export function navFor(role: Role | undefined, pending = 0): NavItem[] {
  if (role === 'admin') {
    return [
      { to: '/', label: 'Overview', end: true },
      { to: '/sessions', label: 'Sessions', end: false },
      { to: '/recordings', label: 'Recordings', end: false },
      { to: '/attendance', label: 'Attendance', end: false },
      { to: '/students', label: 'Students', end: false },
      { to: '/groups', label: 'Groups', end: false },
      { to: '/accounts', label: 'Accounts', end: false },
      { to: '/users', label: 'Users', end: false, badge: pending || undefined },
      { to: '/runs', label: 'Run classes', end: false },
      { to: '/agents', label: 'Agents', end: false },
    ]
  }
  return [
    { to: '/', label: 'Overview', end: true },
    { to: '/sessions', label: 'Sessions', end: false },
    { to: '/recordings', label: 'Recordings', end: false },
    { to: '/attendance', label: 'Attendance', end: false },
    { to: '/students', label: 'Students', end: false },
    { to: '/groups', label: 'My groups', end: false },
    { to: '/accounts', label: 'Accounts', end: false },
  ]
}

/** Registrations waiting for approval: the admin sees their number next to "Users". */
function usePendingCount(enabled: boolean): number {
  const users = useUsers('pending', enabled)
  return enabled ? (users.data?.counts.pending ?? 0) : 0
}

export function Layout() {
  const { data: me } = useMe()
  const logout = useLogout()
  const navigate = useNavigate()
  const isAdmin = me?.role === 'admin'
  const NAV = navFor(me?.role, usePendingCount(isAdmin))

  return (
    <div className="flex min-h-full">
      <aside className="hidden w-60 shrink-0 flex-col bg-slate-900 text-slate-300 md:flex">
        <div className="flex items-center gap-2.5 px-5 py-5">
          <span className="grid size-8 place-items-center rounded-lg bg-teal-600 text-sm font-bold text-white">Z</span>
          <div>
            <p className="text-sm font-semibold text-white">Zoom Auto Admit</p>
            <p className="text-xs text-slate-400">{isAdmin ? 'Admin' : 'Coordinator'}</p>
          </div>
        </div>
        <nav className="mt-2 flex flex-col gap-0.5 px-3" aria-label="Main">
          {NAV.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) =>
                `rounded-lg px-3 py-2 text-sm font-medium transition-colors ${isActive ? 'bg-slate-800 text-white' : 'hover:bg-slate-800/60 hover:text-white'}`
              }
            >
              <span className="flex items-center justify-between gap-2">
                {item.label}
                {item.badge ? <span className="rounded-full bg-amber-500 px-1.5 text-xs font-semibold text-slate-900" aria-label={`${item.badge} waiting`}>{item.badge}</span> : null}
              </span>
            </NavLink>
          ))}
        </nav>
        <p className="mt-auto px-5 py-4 text-xs text-slate-500">Operations · V3</p>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 border-b border-slate-200 bg-white px-4 py-3 md:px-6">
          <nav className="flex flex-wrap gap-x-3 gap-y-1 text-sm md:hidden" aria-label="Main (small screens)">
            {NAV.map((item) => (
              <NavLink key={item.to} to={item.to} end={item.end} className={({ isActive }) => (isActive ? 'font-semibold text-teal-700' : 'text-slate-600')}>
                {item.label}
                {item.badge ? ` (${item.badge})` : ''}
              </NavLink>
            ))}
          </nav>
          <span className="hidden md:block" />
          <div className="ml-auto flex items-center gap-3 text-sm">
            <NavLink to="/account" className="text-slate-600 sm:hidden">Account</NavLink>
            <NavLink to="/account" className="hidden text-slate-500 hover:text-slate-800 sm:inline" title="Your account and password">
              Signed in as <span className="font-medium text-slate-800">{me?.displayName || me?.username}</span>
            </NavLink>
            <button
              type="button"
              onClick={() => logout.mutate(undefined, { onSettled: () => navigate('/login', { replace: true }) })}
              className="rounded-lg border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50"
            >
              Sign out
            </button>
          </div>
        </header>
        <main className="flex-1 px-4 py-5 md:px-6 md:py-6">
          <Outlet />
        </main>
      </div>
    </div>
  )
}

export function PageHeader({ title, description, action }: { title: string; description?: string; action?: ReactNode }) {
  return (
    <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 className="text-xl font-semibold text-slate-900">{title}</h1>
        {description && <p className="mt-1 text-sm text-slate-500">{description}</p>}
      </div>
      {action}
    </div>
  )
}
