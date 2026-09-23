import { Navigate, Outlet, Route, Routes, useLocation } from 'react-router'
import { useMe } from './api/hooks'
import { Layout } from './components/Layout'
import { AccountPage } from './pages/AccountPage'
import { ActivityPage } from './pages/ActivityPage'
import { AgentsPage } from './pages/AgentsPage'
import { AttendancePage } from './pages/AttendancePage'
import { AttendanceSessionPage } from './pages/AttendanceSessionPage'
import { GroupsPage } from './pages/GroupsPage'
import { LmsAccountsPage } from './pages/LmsAccountsPage'
import { LoginPage } from './pages/LoginPage'
import { OverviewPage } from './pages/OverviewPage'
import { SessionsPage } from './pages/SessionsPage'
import { RecordingsPage } from './pages/RecordingsPage'
import { RegisterPage } from './pages/RegisterPage'
import { RunsPage } from './pages/RunsPage'
import { SchedulesPage } from './pages/SchedulesPage'
import { SessionRolesPage } from './pages/SessionRolesPage'
import { SettingsPage } from './pages/SettingsPage'
import { StudentsPage } from './pages/StudentsPage'
import { UsersPage } from './pages/UsersPage'
import { ZoomAccountsPage } from './pages/ZoomAccountsPage'

/** Any signed-in user: the admin or a coordinator. */
function RequireSignIn() {
  const { data: me, isLoading, error } = useMe()
  const location = useLocation()
  if (isLoading) return <p className="p-8 text-sm text-slate-500">Loading…</p>
  if (error) return <p role="alert" className="p-8 text-sm text-rose-700">The backend cannot be reached. It will be retried shortly.</p>
  if (!me) return <Navigate to={`/login?next=${encodeURIComponent(location.pathname + location.search)}`} replace />
  return <Layout />
}

/** The admin's pages. A coordinator who opens one (an old bookmark) lands on the overview; the
 * backend refuses their requests anyway. */
function RequireAdmin() {
  const { data: me } = useMe()
  return me?.role === 'admin' ? <Outlet /> : <Navigate to="/" replace />
}

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route element={<RequireSignIn />}>
        <Route index element={<OverviewPage />} />
        <Route path="sessions" element={<SessionsPage />} />
        <Route path="recordings" element={<RecordingsPage />} />
        <Route path="attendance" element={<AttendancePage />} />
        <Route path="attendance/:id" element={<AttendanceSessionPage />} />
        <Route path="students" element={<StudentsPage />} />
        <Route path="groups" element={<GroupsPage />} />
        <Route path="activity" element={<ActivityPage />} />
        <Route path="zoom-accounts" element={<ZoomAccountsPage />} />
        <Route path="lms-accounts" element={<LmsAccountsPage />} />
        <Route path="schedules" element={<SchedulesPage />} />
        <Route path="session-roles" element={<SessionRolesPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="account" element={<AccountPage />} />
        {/* Where the accounts and the logs used to live: a bookmark still opens what it meant. */}
        <Route path="accounts" element={<Navigate to="/zoom-accounts" replace />} />
        <Route path="logs" element={<Navigate to="/activity" replace />} />
        <Route element={<RequireAdmin />}>
          <Route path="agents" element={<AgentsPage />} />
          <Route path="users" element={<UsersPage />} />
          <Route path="runs" element={<RunsPage />} />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  )
}
