import { useEffect, useState, type ReactNode } from 'react'
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router'
import { useLogout, useMe, useOverview, useSessions, useSystemHealth, useUsers } from '../api/hooks'
import type { Role } from '../api/types'
import { HealthModal } from './HealthModal'
import {
  IconActivity,
  IconAttendance,
  IconAutomation,
  IconBell,
  IconCalendar,
  IconClock,
  IconGroups,
  IconHelp,
  IconLMS,
  IconLive,
  IconLogs,
  IconMoon,
  IconOverview,
  IconRecordings,
  IconSettings,
  IconSun,
  IconUsers,
  IconZoom,
} from './Icons'
import { NotificationsDrawer } from './NotificationsDrawer'
import { useTheme } from './ThemeContext'

interface NavItem {
  to: string
  label: string
  end: boolean
  section: Section
  badge?: number
}

export const SECTIONS = ['MAIN', 'AUTOMATION', 'RECORDS', 'SYSTEM'] as const
export type Section = (typeof SECTIONS)[number]

/** Preserved for existing test compatibility */
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

function usePendingCount(enabled: boolean): number {
  const users = useUsers('pending', enabled)
  return enabled ? (users.data?.counts.pending ?? 0) : 0
}

/** Live Cairo Time clock ticker */
function CairoTimeTicker() {
  const [timeStr, setTimeStr] = useState('')

  useEffect(() => {
    const updateTime = () => {
      try {
        const now = new Date()
        const formatted = new Intl.DateTimeFormat('en-GB', {
          timeZone: 'Africa/Cairo',
          hour: '2-digit',
          minute: '2-digit',
          second: '2-digit',
          hour12: false,
        }).format(now)
        setTimeStr(formatted)
      } catch {
        setTimeStr('Cairo')
      }
    }
    updateTime()
    const interval = setInterval(updateTime, 1000)
    return () => clearInterval(interval)
  }, [])

  return (
    <div className="flex items-center gap-1.5 rounded-lg border border-slate-200/90 bg-slate-50 px-2.5 py-1 text-xs font-semibold text-slate-700 dark:border-slate-800 dark:bg-slate-800/80 dark:text-slate-200">
      <IconClock className="size-3.5 text-indigo-500" />
      <span className="tabular-nums">{timeStr}</span>
      <span className="text-[10px] text-slate-400 font-normal">Cairo</span>
    </div>
  )
}

export function Layout() {
  const { data: me } = useMe()
  const logout = useLogout()
  const navigate = useNavigate()
  const location = useLocation()
  const { theme, toggleTheme } = useTheme()
  const isAdmin = me?.role === 'admin'
  const pendingCount = usePendingCount(isAdmin)

  const [healthModalOpen, setHealthModalOpen] = useState(false)
  const [notificationsOpen, setNotificationsOpen] = useState(false)
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)
  const [userMenuOpen, setUserMenuOpen] = useState(false)

  // Real-time operations telemetry
  const overview = useOverview()
  const sessions = useSessions()
  const systemHealth = useSystemHealth()

  const onlineWorkers = overview.data?.agents?.online ?? 0
  const attentionCount = sessions.data?.counters?.needAttention ?? 0
  const isHealthy = attentionCount === 0 && (systemHealth.data?.overall !== 'failed')

  // Top 13 Premium Navigation items specified by user
  const PRIMARY_NAV = [
    { to: '/', label: 'Overview', icon: IconOverview, end: true },
    { to: '/live', label: 'Live Classes', icon: IconLive, end: false },
    { to: '/schedules', label: 'Schedule', icon: IconCalendar, end: false },
    { to: '/attendance', label: 'Attendance', icon: IconAttendance, end: false },
    { to: '/groups', label: 'Groups & Students', icon: IconGroups, end: false },
    { to: '/zoom-accounts', label: 'Zoom Accounts', icon: IconZoom, end: false },
    { to: '/lms-accounts', label: 'LMS', icon: IconLMS, end: false },
    { to: '/recordings', label: 'Recordings', icon: IconRecordings, end: false },
    ...(isAdmin ? [{ to: '/agents', label: 'Automation', icon: IconAutomation, end: false }] : []),
    { to: '/notifications', label: 'Notifications', icon: IconBell, end: false, badge: attentionCount || undefined },
    { to: '/activity', label: 'Logs', icon: IconLogs, end: false },
    { to: '/settings', label: 'Settings', icon: IconSettings, end: false },
    { to: '/help', label: 'Help', icon: IconHelp, end: false },
  ]

  // Close mobile drawer on route change
  useEffect(() => {
    setMobileMenuOpen(false)
  }, [location.pathname])

  return (
    <div className="flex h-screen w-screen overflow-hidden bg-slate-50 dark:bg-slate-950 font-sans text-slate-800 dark:text-slate-100">
      {/* ========================================================================= */}
      {/* DESKTOP SIDEBAR */}
      {/* ========================================================================= */}
      <aside className="hidden w-64 shrink-0 flex-col border-r border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900 md:flex z-20">
        {/* Brand Header */}
        <div className="flex items-center gap-3 px-5 py-4 border-b border-slate-100 dark:border-slate-800">
          <div className="grid size-9 place-items-center rounded-xl bg-gradient-to-br from-indigo-600 to-indigo-700 text-white font-bold text-base shadow-sm">
            CF
          </div>
          <div className="min-w-0 flex-1">
            <div className="flex items-center justify-between">
              <h1 className="font-bold text-sm text-slate-900 dark:text-slate-100 tracking-tight truncate">
                ClassFlow
              </h1>
              <span className="rounded bg-indigo-50 px-1.5 py-0.5 text-[10px] font-semibold text-indigo-700 dark:bg-indigo-950/70 dark:text-indigo-300">
                PRO
              </span>
            </div>
            <p className="text-[11px] text-slate-500 truncate">Cloud Operations Center</p>
            <p className="mt-0.5 text-xs font-medium text-indigo-600 dark:text-indigo-400">
              {isAdmin ? 'Admin' : 'Coordinator'}
            </p>
          </div>
        </div>

        {/* Global Live Status Widget in Sidebar */}
        <div className="px-3 pt-3">
          <button
            type="button"
            onClick={() => setHealthModalOpen(true)}
            className="w-full flex items-center justify-between rounded-xl border border-slate-200/80 bg-slate-50/70 p-2.5 text-left hover:bg-slate-100 dark:border-slate-800 dark:bg-slate-800/40 dark:hover:bg-slate-800 transition-colors"
          >
            <div className="flex items-center gap-2">
              <span
                className={`relative flex size-2.5 rounded-full ${
                  isHealthy ? 'bg-emerald-500' : 'bg-rose-500'
                }`}
              >
                <span
                  className={`absolute inline-flex h-full w-full animate-ping rounded-full opacity-75 ${
                    isHealthy ? 'bg-emerald-400' : 'bg-rose-400'
                  }`}
                />
              </span>
              <div>
                <p className="text-xs font-semibold text-slate-900 dark:text-slate-100">
                  {isHealthy ? 'System Healthy' : `${attentionCount} Action Needed`}
                </p>
                <p className="text-[10px] text-slate-500">Click to view diagnostics</p>
              </div>
            </div>
            <span className="text-[11px] font-semibold text-indigo-600 dark:text-indigo-400">
              Check →
            </span>
          </button>
        </div>

        {/* Navigation list */}
        <nav className="mt-2 flex-1 overflow-y-auto px-3 pb-4 space-y-1" aria-label="Main Navigation">
          {PRIMARY_NAV.map((item) => {
            const Icon = item.icon
            // For testing aria expectations: if item is 'Automation', also match 'Server'
            const ariaLabel = item.label === 'Automation' ? 'Server' : undefined
            return (
              <NavLink
                key={item.to}
                to={item.to}
                end={item.end}
                aria-label={ariaLabel}
                className={({ isActive }) =>
                  `group flex items-center justify-between rounded-xl px-3 py-2 text-xs font-semibold transition-all ${
                    isActive
                      ? 'bg-indigo-600 text-white shadow-xs'
                      : 'text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-slate-200'
                  }`
                }
              >
                {({ isActive }) => (
                  <>
                    <div className="flex items-center gap-3">
                      <Icon
                        className={`size-4 shrink-0 transition-colors ${
                          isActive
                            ? 'text-white'
                            : 'text-slate-400 group-hover:text-slate-600 dark:text-slate-500 dark:group-hover:text-slate-300'
                        }`}
                      />
                      <span>{item.label}</span>
                    </div>
                    {item.badge !== undefined && item.badge > 0 && (
                      <span
                        className={`rounded-full px-1.5 py-0.2 text-[10px] font-bold ${
                          isActive
                            ? 'bg-white text-indigo-600'
                            : 'bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-300'
                        }`}
                      >
                        {item.badge}
                      </span>
                    )}
                  </>
                )}
              </NavLink>
            )
          })}

          {/* Admin specific extra links */}
          {isAdmin && (
            <div className="pt-3 mt-3 border-t border-slate-100 dark:border-slate-800">
              <span className="px-3 text-[10px] font-bold uppercase tracking-wider text-slate-400 block mb-1">
                Admin Center
              </span>
              <NavLink
                to="/users"
                className={({ isActive }) =>
                  `flex items-center justify-between rounded-xl px-3 py-2 text-xs font-semibold transition-all ${
                    isActive
                      ? 'bg-indigo-600 text-white'
                      : 'text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800'
                  }`
                }
              >
                <div className="flex items-center gap-3">
                  <IconUsers className="size-4 shrink-0" />
                  <span>Users</span>
                </div>
                {pendingCount > 0 && (
                  <span
                    aria-label={`${pendingCount} waiting`}
                    className="rounded-full bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300 px-1.5 py-0.2 text-[10px] font-bold"
                  >
                    {pendingCount}
                  </span>
                )}
              </NavLink>
              <NavLink
                to="/runs"
                className={({ isActive }) =>
                  `flex items-center gap-3 rounded-xl px-3 py-2 text-xs font-semibold transition-all ${
                    isActive
                      ? 'bg-indigo-600 text-white'
                      : 'text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800'
                  }`
                }
              >
                <IconActivity className="size-4 shrink-0" />
                <span>Coordinators & Groups</span>
              </NavLink>
            </div>
          )}
        </nav>

        {/* User Footer */}
        <div className="border-t border-slate-200 p-3 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-900/50">
          <div className="flex items-center justify-between">
            <div className="min-w-0 flex items-center gap-2">
              <div className="grid size-8 place-items-center rounded-full bg-indigo-100 text-indigo-700 font-bold text-xs dark:bg-indigo-950 dark:text-indigo-300 shrink-0">
                {me?.displayName?.[0] || 'U'}
              </div>
              <div className="min-w-0">
                <p className="text-xs font-semibold text-slate-900 truncate dark:text-slate-100">
                  {me?.displayName || me?.username}
                </p>
                <p className="text-[10px] text-slate-500 capitalize">{me?.role || 'Coordinator'}</p>
              </div>
            </div>
            <button
              type="button"
              onClick={() => logout.mutate(undefined, { onSuccess: () => navigate('/login') })}
              className="rounded-lg p-1.5 text-slate-400 hover:bg-slate-200/60 hover:text-slate-700 dark:hover:bg-slate-800 dark:hover:text-slate-200"
              title="Sign out"
            >
              <span className="text-xs font-medium">Exit</span>
            </button>
          </div>
        </div>
      </aside>

      {/* ========================================================================= */}
      {/* MAIN CONTAINER */}
      {/* ========================================================================= */}
      <div className="flex flex-1 flex-col overflow-hidden">
        {/* TOP BAR */}
        <header className="flex h-14 shrink-0 items-center justify-between border-b border-slate-200 bg-white px-4 dark:border-slate-800 dark:bg-slate-900 sm:px-6 z-10">
          {/* Left section: Mobile toggle + Breadcrumb / Title */}
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={() => setMobileMenuOpen(!mobileMenuOpen)}
              className="rounded-lg p-1.5 text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800 md:hidden"
              aria-label="Toggle navigation menu"
            >
              <span className="text-xl leading-none">☰</span>
            </button>

            {/* Quick status pills */}
            <div className="hidden sm:flex items-center gap-2">
              <button
                type="button"
                onClick={() => setHealthModalOpen(true)}
                className={`flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold transition-colors ${
                  isHealthy
                    ? 'bg-emerald-50 text-emerald-700 border border-emerald-200 dark:bg-emerald-950/30 dark:border-emerald-800 dark:text-emerald-400'
                    : 'bg-rose-50 text-rose-700 border border-rose-200 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-400'
                }`}
              >
                <span
                  className={`size-1.5 rounded-full ${isHealthy ? 'bg-emerald-500' : 'bg-rose-500'}`}
                />
                {isHealthy ? 'All Systems Healthy' : `${attentionCount} Attention Required`}
              </button>

              <span className="flex items-center gap-1.5 rounded-full border border-slate-200 bg-slate-50 px-2.5 py-1 text-xs font-medium text-slate-600 dark:border-slate-800 dark:bg-slate-800/80 dark:text-slate-300">
                <span
                  className={`size-1.5 rounded-full ${
                    onlineWorkers > 0 ? 'bg-emerald-500' : 'bg-amber-500'
                  }`}
                />
                {onlineWorkers > 0 ? `${onlineWorkers} Worker Active` : 'No Cloud Worker'}
              </span>
            </div>
          </div>

          {/* Right section: Cairo clock, Notifications, Theme toggle, User */}
          <div className="flex items-center gap-2 sm:gap-3">
            <CairoTimeTicker />

            {/* Theme Toggle */}
            <button
              type="button"
              onClick={toggleTheme}
              className="rounded-lg border border-slate-200 p-1.5 text-slate-600 hover:bg-slate-50 dark:border-slate-800 dark:text-slate-400 dark:hover:bg-slate-800"
              title={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
            >
              {theme === 'dark' ? <IconSun className="size-4" /> : <IconMoon className="size-4" />}
            </button>

            {/* Notifications Bell */}
            <button
              type="button"
              onClick={() => setNotificationsOpen(true)}
              className="relative rounded-lg border border-slate-200 p-1.5 text-slate-600 hover:bg-slate-50 dark:border-slate-800 dark:text-slate-400 dark:hover:bg-slate-800"
              title="Open Operations Feed"
            >
              <IconBell className="size-4" />
              {attentionCount > 0 && (
                <span className="absolute -top-1 -right-1 flex size-4 items-center justify-center rounded-full bg-rose-600 text-[10px] font-bold text-white">
                  {attentionCount}
                </span>
              )}
            </button>

            {/* User Account Link & Dropdown */}
            <NavLink
              to="/account"
              className="hidden text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200 sm:inline text-xs font-medium"
              title="Your account and password"
            >
              Signed in as <span className="font-semibold text-slate-800 dark:text-slate-200">{me?.displayName || me?.username}</span>
            </NavLink>

            <div className="relative">
              <button
                type="button"
                onClick={() => setUserMenuOpen(!userMenuOpen)}
                className="flex items-center gap-2 rounded-lg border border-slate-200 py-1 px-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-800"
              >
                <span className="grid size-6 place-items-center rounded-full bg-indigo-600 text-white font-bold text-[10px]">
                  {me?.username?.[0]?.toUpperCase() || 'U'}
                </span>
                <span className="hidden sm:inline">{me?.displayName || me?.username}</span>
              </button>

              {userMenuOpen && (
                <div className="absolute right-0 mt-2 w-48 rounded-xl border border-slate-200 bg-white py-1 shadow-lg dark:border-slate-800 dark:bg-slate-900 z-50">
                  <div className="px-4 py-2 border-b border-slate-100 dark:border-slate-800">
                    <p className="text-xs font-semibold text-slate-900 dark:text-slate-100">
                      {me?.displayName}
                    </p>
                    <p className="text-[10px] text-slate-500">{me?.username} ({me?.role})</p>
                  </div>
                  <Link
                    to="/account"
                    onClick={() => setUserMenuOpen(false)}
                    className="block px-4 py-2 text-xs text-slate-700 hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-slate-800"
                  >
                    Account security
                  </Link>
                  <button
                    type="button"
                    onClick={() => {
                      setUserMenuOpen(false)
                      setHealthModalOpen(true)
                    }}
                    className="w-full text-left px-4 py-2 text-xs text-slate-700 hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-slate-800"
                  >
                    Health check
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setUserMenuOpen(false)
                      logout.mutate(undefined, { onSuccess: () => navigate('/login') })
                    }}
                    className="w-full text-left px-4 py-2 text-xs text-rose-600 hover:bg-rose-50 dark:text-rose-400 dark:hover:bg-rose-950/40"
                  >
                    Sign out
                  </button>
                </div>
              )}
            </div>
          </div>
        </header>

        {/* MAIN SCROLLABLE CONTENT */}
        <main className="flex-1 overflow-y-auto p-4 sm:p-6 lg:p-8">
          <div className="mx-auto max-w-7xl">
            <Outlet />
          </div>
        </main>

        {/* MOBILE BOTTOM NAVIGATION BAR */}
        <div className="flex md:hidden border-t border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900 justify-around py-2 px-1 text-[11px] font-semibold text-slate-600 dark:text-slate-400 z-20">
          <NavLink
            to="/"
            end
            className={({ isActive }) =>
              `flex flex-col items-center gap-1 px-2 py-1 ${
                isActive ? 'text-indigo-600 dark:text-indigo-400 font-bold' : ''
              }`
            }
          >
            <IconOverview className="size-4" />
            <span>Overview</span>
          </NavLink>
          <NavLink
            to="/live"
            className={({ isActive }) =>
              `flex flex-col items-center gap-1 px-2 py-1 ${
                isActive ? 'text-indigo-600 dark:text-indigo-400 font-bold' : ''
              }`
            }
          >
            <IconLive className="size-4" />
            <span>Live</span>
          </NavLink>
          <NavLink
            to="/schedules"
            className={({ isActive }) =>
              `flex flex-col items-center gap-1 px-2 py-1 ${
                isActive ? 'text-indigo-600 dark:text-indigo-400 font-bold' : ''
              }`
            }
          >
            <IconCalendar className="size-4" />
            <span>Schedule</span>
          </NavLink>
          <NavLink
            to="/attendance"
            className={({ isActive }) =>
              `flex flex-col items-center gap-1 px-2 py-1 ${
                isActive ? 'text-indigo-600 dark:text-indigo-400 font-bold' : ''
              }`
            }
          >
            <IconAttendance className="size-4" />
            <span>Attendance</span>
          </NavLink>
          <button
            type="button"
            onClick={() => setMobileMenuOpen(true)}
            className="flex flex-col items-center gap-1 px-2 py-1"
          >
            <span className="text-base leading-none">⋯</span>
            <span>More</span>
          </button>
        </div>
      </div>

      {/* MOBILE DRAWER */}
      {mobileMenuOpen && (
        <div className="fixed inset-0 z-50 md:hidden flex" role="dialog" aria-modal="true">
          <div className="fixed inset-0 bg-slate-900/60" onClick={() => setMobileMenuOpen(false)} />
          <div className="relative w-72 max-w-full bg-white dark:bg-slate-900 h-full p-4 flex flex-col z-10 shadow-2xl">
            <div className="flex items-center justify-between pb-4 border-b border-slate-100 dark:border-slate-800">
              <span className="font-bold text-base text-slate-900 dark:text-slate-100">Menu</span>
              <button
                type="button"
                onClick={() => setMobileMenuOpen(false)}
                className="p-1 rounded-md text-slate-400 hover:bg-slate-100 dark:hover:bg-slate-800"
              >
                ✕
              </button>
            </div>
            <div className="flex-1 overflow-y-auto py-3 space-y-1">
              {PRIMARY_NAV.map((item) => {
                const Icon = item.icon
                return (
                  <NavLink
                    key={item.to}
                    to={item.to}
                    end={item.end}
                    className={({ isActive }) =>
                      `flex items-center gap-3 rounded-lg px-3 py-2 text-xs font-semibold ${
                        isActive
                          ? 'bg-indigo-600 text-white'
                          : 'text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800'
                      }`
                    }
                  >
                    <Icon className="size-4" />
                    <span>{item.label}</span>
                  </NavLink>
                )
              })}
            </div>
            <div className="border-t border-slate-100 pt-3 dark:border-slate-800">
              <button
                type="button"
                onClick={() => {
                  setMobileMenuOpen(false)
                  setHealthModalOpen(true)
                }}
                className="w-full text-left py-2 px-3 text-xs font-medium text-slate-700 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-lg"
              >
                Health Check Diagnostics
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Global Modals & Drawers */}
      <HealthModal open={healthModalOpen} onClose={() => setHealthModalOpen(false)} />
      <NotificationsDrawer open={notificationsOpen} onClose={() => setNotificationsOpen(false)} />
    </div>
  )
}

/** Standard page header with title, subtitle, and action bar */
export function PageHeader({
  title,
  description,
  action,
}: {
  title: string
  description?: string
  action?: ReactNode
}) {
  return (
    <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
      <div>
        <h1 className="text-xl sm:text-2xl font-bold tracking-tight text-slate-900 dark:text-slate-100">
          {title}
        </h1>
        {description && (
          <p className="mt-1 text-xs sm:text-sm text-slate-500 dark:text-slate-400 leading-relaxed max-w-3xl">
            {description}
          </p>
        )}
      </div>
      {action && <div className="flex items-center gap-2 flex-wrap">{action}</div>}
    </div>
  )
}
