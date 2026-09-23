import type { ReactNode } from 'react'
import { NavLink, Outlet, useNavigate } from 'react-router'
import { useLogout, useMe, useUsers } from '../api/hooks'
import type { Role } from '../api/types'

interface NavItem {
  to: string
  label: string
  end: boolean
  /** Which part of the work it belongs to, drawn as a heading in the menu. */
  section: Section
  badge?: number
}

/** The same workspace areas used by the Windows application. */
export const SECTIONS = ['MAIN', 'AUTOMATION', 'RECORDS', 'SYSTEM'] as const
export type Section = (typeof SECTIONS)[number]

/**
 * What each role finds in the menu. The backend enforces the same split on every request.
 *
 * Everything the app does is here, in the parts it groups them in: what is happening now, the
 * records of what happened, the accounts and lists that make a class runnable, and the machines and
 * switches underneath. A coordinator sees their own of each; only the admin sees other people's
 * classes, the machines and the shared switches.
 */
export function navFor(role: Role | undefined, pending = 0): NavItem[] {
  const main: NavItem[] = [
    { to: '/', label: 'Overview', end: true, section: 'MAIN' },
    { to: '/sessions', label: 'Sessions', end: false, section: 'MAIN' },
    { to: '/live', label: 'Meetings', end: false, section: 'MAIN' },
  ]
  const automation: NavItem[] = [
    { to: '/schedules', label: 'Schedules', end: false, section: 'AUTOMATION' },
    { to: '/session-roles', label: 'Session roles', end: false, section: 'AUTOMATION' },
  ]
  const system: NavItem[] = [
    { to: '/zoom-accounts', label: 'Accounts', end: false, section: 'SYSTEM' },
    { to: '/lms-accounts', label: 'LMS accounts', end: false, section: 'SYSTEM' },
    { to: '/activity', label: 'Logs', end: false, section: 'SYSTEM' },
    { to: '/settings', label: 'Settings', end: false, section: 'SYSTEM' },
  ]
  if (role === 'admin') {
    return [
      ...main,
      ...automation,
      { to: '/attendance', label: 'Attendance', end: false, section: 'RECORDS' },
      { to: '/recordings', label: 'Recordings', end: false, section: 'RECORDS' },
      { to: '/students', label: 'Students', end: false, section: 'RECORDS' },
      { to: '/groups', label: 'Groups', end: false, section: 'RECORDS' },
      { to: '/runs', label: 'Coordinators & groups', end: false, section: 'RECORDS' },
      { to: '/users', label: 'Users', end: false, section: 'RECORDS', badge: pending || undefined },
      { to: '/agents', label: 'Server', end: false, section: 'SYSTEM' },
      ...system,
    ]
  }
  return [
    ...main,
    ...automation,
    { to: '/attendance', label: 'Attendance', end: false, section: 'RECORDS' },
    { to: '/recordings', label: 'Recordings', end: false, section: 'RECORDS' },
    { to: '/students', label: 'Students', end: false, section: 'RECORDS' },
    { to: '/groups', label: 'Groups & students', end: false, section: 'RECORDS' },
    ...system,
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
      <aside className="hidden w-60 shrink-0 flex-col border-r border-[#e4e7ee] bg-white text-slate-600 md:flex">
        <div className="flex items-center gap-2.5 px-5 py-5">
          <span className="grid size-8 place-items-center rounded-lg bg-indigo-600 text-sm font-bold text-white">Z</span>
          <div>
            <p className="text-sm font-semibold text-slate-900">Zoom Auto Admit</p>
            <p className="text-xs text-slate-500">Classes, admits and attendance</p>
            <p className="mt-0.5 text-xs font-medium text-indigo-600">{isAdmin ? 'Admin' : 'Coordinator'}</p>
          </div>
        </div>
        <NavLink to={isAdmin ? '/runs' : '/sessions'} className="mx-3 mb-2 flex items-center justify-center rounded-lg bg-indigo-600 px-3 py-2.5 text-sm font-semibold text-white shadow-sm hover:bg-indigo-700">
          Start automation
        </NavLink>
        <nav className="mt-1 flex flex-col overflow-y-auto px-3 pb-4" aria-label="Main">
          {SECTIONS.map((section) => {
            const items = NAV.filter((item) => item.section === section)
            if (items.length === 0) return null
            return (
              <div key={section} className="mb-1">
                <p className="px-3 pb-1 pt-2.5 text-[0.65rem] font-semibold uppercase tracking-wider text-slate-400">{section}</p>
                {items.map((item) => (
                  <NavLink
                    key={item.to}
                    to={item.to}
                    end={item.end}
                    className={({ isActive }) =>
                      `block rounded-lg border-l-[3px] px-3 py-2 text-sm font-medium transition-colors ${isActive ? 'border-indigo-600 bg-indigo-50 text-indigo-700' : 'border-transparent hover:bg-[#eef0f6] hover:text-slate-900'}`
                    }
                  >
                    <span className="flex items-center justify-between gap-2">
                      {item.label}
                      {item.badge ? <span className="rounded-full bg-amber-500 px-1.5 text-xs font-semibold text-slate-900" aria-label={`${item.badge} waiting`}>{item.badge}</span> : null}
                    </span>
                  </NavLink>
                ))}
              </div>
            )
          })}
        </nav>
        <p className="mt-auto px-5 py-4 text-xs text-slate-400">Cloud workspace · {isAdmin ? 'Admin' : 'Coordinator'}</p>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 border-b border-[#e4e7ee] bg-white px-4 py-3 md:px-6">
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
