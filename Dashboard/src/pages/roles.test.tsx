import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { App } from '../App'
import { EditRecordingModal } from '../components/EditRecordingModal'
import { navFor } from '../components/Layout'
import { adminMe, coordinatorMe, fakeBackend, groupSummary, recording, renderPage, signedInAs, type Call } from '../test/helpers'
import { AccountPage } from './AccountPage'
import { LoginPage } from './LoginPage'
import { RegisterPage } from './RegisterPage'
import { GroupsPage } from './GroupsPage'
import { UsersPage } from './UsersPage'

// Made-up passwords, for these tests only.
const PASSWORD = 'test-only password 123'

const overview = (body: Record<string, unknown>) => (call: Call) =>
  call.url === '/api/v1/dashboard/overview' ? { body: { recordings: { total: 2, pending: 2, onLms: 0, missingLink: 1 }, serverTime: '', ...body } } : undefined
const noRecordings = (call: Call) =>
  call.url.startsWith('/api/v1/dashboard/recordings') ? { body: { items: [], total: 0, page: 1, pageSize: 10, sort: 'updated' } } : undefined

const user = (overrides: Record<string, unknown> = {}) => ({
  id: 'u-sara',
  username: 'sara',
  displayName: 'Sara K',
  role: 'coordinator',
  status: 'active',
  createdAt: '2026-09-13T10:00:00Z',
  approvedAt: null,
  lastLoginAt: null,
  groups: [],
  ...overrides,
})
const userList = (users: unknown[]) => (call: Call) =>
  call.url.startsWith('/api/v1/admin/users') && call.method === 'GET'
    ? { body: { users, count: users.length, counts: { pending: users.filter((u) => (u as { status: string }).status === 'pending').length, active: 1, rejected: 0, disabled: 0 } } }
    : undefined
const adminGroups = (call: Call) =>
  call.url === '/api/v1/admin/groups' && call.method === 'GET'
    ? {
        body: {
          groups: [
            { ...groupSummary({ id: 'g1', group: 'CAI5_AIS4_S7' }), coordinators: [{ id: 'u-omar', username: 'omar', displayName: 'Omar', status: 'active' }] },
            { ...groupSummary({ id: 'g2', group: 'CAI5_AIS4_S8', recordings: 0, lastSessionDate: null, lastUpdatedAt: null, pending: 0, onLms: 0 }), coordinators: [] },
            { ...groupSummary({ id: 'g3', group: 'OLD_GROUP', archived: true }), coordinators: [] },
          ],
          count: 3,
        },
      }
    : undefined

describe('menus by role', () => {
  it('gives the admin users and agents, a coordinator only their groups', () => {
    expect(navFor('admin', 2).map((i) => i.label)).toEqual(['Overview', 'Sessions', 'Live sessions', 'What machines did', 'Run classes', 'Attendance', 'Recordings', 'Students', 'Groups', 'Users', 'Zoom accounts', 'LMS sign-ins', 'Opens by itself', 'Who is co-host', 'Agents', 'Settings'])
    expect(navFor('admin', 2).find((i) => i.label === 'Users')?.badge).toBe(2)
    expect(navFor('coordinator').map((i) => i.label)).toEqual(['Overview', 'Sessions', 'Live sessions', 'What machines did', 'Attendance', 'Recordings', 'Students', 'My groups', 'Zoom accounts', 'LMS sign-ins', 'Opens by itself', 'Who is co-host', 'Settings'])
  })

  it('shows a coordinator their own overview, without agents or jobs', async () => {
    const calls = fakeBackend(signedInAs(coordinatorMe()), overview({ agents: null, jobs: null, groups: 1 }), noRecordings)
    renderPage(<App />, '/')
    expect(await screen.findByText('My groups', { selector: 'p' })).toBeInTheDocument()
    expect(screen.queryByText('Agents online')).toBeNull()
    expect(screen.queryByRole('link', { name: 'Agents' })).toBeNull()
    expect(screen.queryByRole('link', { name: /Users/ })).toBeNull()
    expect(screen.getByText('Coordinator')).toBeInTheDocument()
    expect(calls.some((c) => c.url.startsWith('/api/v1/admin/'))).toBe(false)     // never asks for admin data
  })

  it('tells a coordinator without groups why the dashboard is empty', async () => {
    fakeBackend(signedInAs(coordinatorMe([])), overview({ agents: null, jobs: null, groups: 0 }), noRecordings)
    renderPage(<App />, '/')
    expect(await screen.findByText(/You have no groups yet/)).toBeInTheDocument()
  })

  it('sends a coordinator away from the admin pages', async () => {
    const calls = fakeBackend(signedInAs(coordinatorMe()), overview({ agents: null, jobs: null, groups: 1 }), noRecordings)
    renderPage(<App />, '/users')
    expect(await screen.findByRole('heading', { name: 'Overview' })).toBeInTheDocument()
    renderPage(<App />, '/agents')
    await waitFor(() => expect(screen.getAllByRole('heading', { name: 'Overview' }).length).toBe(2))
    expect(calls.some((c) => c.url.includes('/admin/users') || c.url.includes('/dashboard/agents'))).toBe(false)
  })

  it('shows the admin how many registrations wait', async () => {
    fakeBackend(signedInAs(adminMe), userList([user({ status: 'pending' }), user({ id: 'u2', username: 'ali', status: 'pending' })]),
      overview({ agents: { total: 0, online: 0, busy: 0 }, jobs: { queued: 0, assigned: 0, running: 0, succeededLast24h: 0, failedLast24h: 0 }, groups: 3 }), noRecordings)
    renderPage(<App />, '/')
    expect(await screen.findAllByLabelText('2 waiting')).not.toHaveLength(0)
    expect(screen.getAllByRole('link', { name: /Agents/ }).length).toBeGreaterThan(0)
  })
})

describe('signing in and registering', () => {
  it('explains an account that waits for approval', async () => {
    fakeBackend(signedInAs(null), (call) =>
      call.url.endsWith('/auth/login') ? { status: 403, body: { error: 'Account awaiting approval', details: { reason: 'pending' } } } : undefined)
    renderPage(<LoginPage />, '/login')
    await userEvent.type(screen.getByLabelText('Username'), 'sara')
    await userEvent.type(screen.getByLabelText('Password'), PASSWORD)
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent("waiting for the admin's approval")
    expect(screen.getByRole('link', { name: 'Request one' })).toHaveAttribute('href', '/register')
  })

  it('checks the request before sending it', async () => {
    const calls = fakeBackend(signedInAs(null))
    renderPage(<RegisterPage />, '/register')
    await userEvent.type(await screen.findByLabelText('Username'), 'No Spaces')
    await userEvent.type(screen.getByLabelText('Password'), 'short')
    await userEvent.click(screen.getByRole('button', { name: 'Send request' }))
    expect(screen.getByText('Your name is required.')).toBeInTheDocument()
    expect(screen.getByText(/3-50 characters/)).toBeInTheDocument()
    expect(screen.getByText('At least 12 characters.', { selector: 'p.text-rose-700' })).toBeInTheDocument()
    expect(calls.some((c) => c.url.endsWith('/auth/register'))).toBe(false)
  })

  it('sends a registration and says it waits for approval', async () => {
    const calls = fakeBackend(signedInAs(null), (call) =>
      call.url.endsWith('/auth/register') ? { status: 201, body: { username: 'sara.k', status: 'pending', message: '' } } : undefined)
    renderPage(<RegisterPage />, '/register')
    await userEvent.type(await screen.findByLabelText('Your name'), 'Sara K')
    await userEvent.type(screen.getByLabelText('Username'), 'Sara.K')
    await userEvent.type(screen.getByLabelText('Password'), PASSWORD)
    await userEvent.type(screen.getByLabelText('Password again'), PASSWORD)
    await userEvent.click(screen.getByRole('button', { name: 'Send request' }))

    expect(await screen.findByRole('status')).toHaveTextContent('sara.k')
    const sent = calls.find((c) => c.url.endsWith('/auth/register'))!
    expect(sent.headers.get('X-Dashboard-Request')).toBe('1')
    expect(JSON.parse(sent.body!)).toEqual({ username: 'sara.k', displayName: 'Sara K', password: PASSWORD })
  })

  it('says when a username is taken', async () => {
    fakeBackend(signedInAs(null), (call) =>
      call.url.endsWith('/auth/register') ? { status: 409, body: { error: 'Conflict', details: 'That username is taken.' } } : undefined)
    renderPage(<RegisterPage />, '/register')
    await userEvent.type(await screen.findByLabelText('Your name'), 'Admin Two')
    await userEvent.type(screen.getByLabelText('Username'), 'admin')
    await userEvent.type(screen.getByLabelText('Password'), PASSWORD)
    await userEvent.type(screen.getByLabelText('Password again'), PASSWORD)
    await userEvent.click(screen.getByRole('button', { name: 'Send request' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('That username is taken. Choose another.')
    expect(screen.getByLabelText('Password')).toHaveValue('')
  })
})

describe('users page (admin)', () => {
  it('approves a registration, then asks for its groups', async () => {
    const calls = fakeBackend(
      signedInAs(adminMe),
      adminGroups,
      (call) => (call.url === '/api/v1/admin/users/u-sara/approve' ? { body: user({ status: 'active' }) } : undefined),
      (call) => (call.url === '/api/v1/admin/users/u-sara/groups' ? { body: user({ status: 'active', groups: [{ id: 'g2', name: 'CAI5_AIS4_S8', displayName: null, archived: false }] }) } : undefined),
      userList([{ ...adminMe, status: 'active', createdAt: '', approvedAt: null, lastLoginAt: null, groups: [] }, user({ status: 'pending' })]),
    )
    renderPage(<UsersPage />, '/users')
    const waiting = await screen.findByText(/Waiting for approval \(1\)/)
    await userEvent.click(within(waiting.closest('section')!).getByRole('button', { name: 'Approve sara' }))

    const dialog = await screen.findByRole('dialog', { name: 'Groups of Sara K' })
    expect(within(dialog).queryByText('OLD_GROUP')).toBeNull()                   // archived groups are not offered
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /CAI5_AIS4_S8/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save groups' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())

    const approve = calls.find((c) => c.url.endsWith('/approve'))!
    expect(approve.method).toBe('POST')
    expect(approve.headers.get('X-Dashboard-Request')).toBe('1')
    const groups = calls.find((c) => c.url.endsWith('/u-sara/groups'))!
    expect(groups.method).toBe('PUT')
    expect(JSON.parse(groups.body!)).toEqual({ groupIds: ['g2'] })
    expect(screen.getByText('Groups saved for Sara K')).toBeInTheDocument()
  })

  it('creates a coordinator with groups', async () => {
    const calls = fakeBackend(
      signedInAs(adminMe),
      adminGroups,
      (call) => (call.url === '/api/v1/admin/users' && call.method === 'POST' ? { status: 201, body: user({ username: 'omar2' }) } : undefined),
      userList([]),
    )
    renderPage(<UsersPage />, '/users')
    await userEvent.click(await screen.findByRole('button', { name: 'New coordinator' }))
    const dialog = await screen.findByRole('dialog', { name: 'New coordinator' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Omar Two')
    await userEvent.type(within(dialog).getByLabelText('Username'), 'omar2')
    await userEvent.type(within(dialog).getByLabelText('Password'), PASSWORD)
    await userEvent.type(within(dialog).getByLabelText('Password again'), PASSWORD)
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: /CAI5_AIS4_S7/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create account' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    const sent = calls.find((c) => c.url === '/api/v1/admin/users' && c.method === 'POST')!
    expect(JSON.parse(sent.body!)).toEqual({ username: 'omar2', displayName: 'Omar Two', password: PASSWORD, groupIds: ['g1'] })
  })

  it('disables a coordinator only after a confirmation', async () => {
    const calls = fakeBackend(
      signedInAs(adminMe),
      (call) => (call.url === '/api/v1/admin/users/u-sara' && call.method === 'PATCH' ? { body: user({ status: 'disabled' }) } : undefined),
      userList([user({ groups: [{ id: 'g1', name: 'CAI5_AIS4_S7', displayName: null, archived: false }] })]),
    )
    renderPage(<UsersPage />, '/users')
    const row = (await screen.findByText('Sara K')).closest('tr')!
    expect(within(row).getByText('CAI5_AIS4_S7')).toBeInTheDocument()
    await userEvent.click(within(row).getByRole('button', { name: 'Disable sara' }))
    const dialog = await screen.findByRole('dialog', { name: 'Disable Sara K?' })
    expect(calls.some((c) => c.method === 'PATCH')).toBe(false)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Disable' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(JSON.parse(calls.find((c) => c.method === 'PATCH')!.body!)).toEqual({ status: 'disabled' })
  })

  it('never offers actions on the admin account itself', async () => {
    fakeBackend(signedInAs(adminMe), userList([{ ...adminMe, status: 'active', createdAt: '', approvedAt: null, lastLoginAt: null, groups: [] }]))
    renderPage(<UsersPage />, '/users')
    const row = (await screen.findByText('The Admin')).closest('tr')!
    expect(within(row).getByText('You')).toBeInTheDocument()
    expect(within(row).queryByRole('button')).toBeNull()
  })
})

describe('groups page (admin)', () => {
  it('shows who coordinates each group and archives on request', async () => {
    const calls = fakeBackend(
      signedInAs(adminMe),
      (call) => (call.url === '/api/v1/admin/groups/g1' && call.method === 'PATCH' ? { body: { id: 'g1', name: 'CAI5_AIS4_S7', displayName: null, archived: true } } : undefined),
      adminGroups,
    )
    renderPage(<App />, '/groups')
    const row = (await screen.findByRole('link', { name: 'CAI5_AIS4_S7' })).closest('tr')!
    expect(within(row).getByText('Omar')).toBeInTheDocument()
    const empty = screen.getByRole('link', { name: 'CAI5_AIS4_S8' }).closest('tr')!
    expect(within(empty).getByText('Nobody')).toBeInTheDocument()
    expect(within(empty).getAllByText('—')).toHaveLength(2)                     // no recording yet
    expect(screen.queryByRole('link', { name: 'OLD_GROUP' })).toBeNull()          // archived: hidden until asked
    await userEvent.click(screen.getByLabelText(/Show archived groups/))
    expect(screen.getByRole('link', { name: 'OLD_GROUP' })).toBeInTheDocument()

    await userEvent.click(within(row).getByRole('button', { name: 'Archive CAI5_AIS4_S7' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true))
    expect(JSON.parse(calls.find((c) => c.method === 'PATCH')!.body!)).toEqual({ archived: true })
  })
})

describe('account page', () => {
  it('changes the password with the current one', async () => {
    const calls = fakeBackend(signedInAs(coordinatorMe(['CAI5_AIS4_S7', 'CAI5_AIS4_S8'])), (call) =>
      call.url === '/api/v1/auth/password' ? { body: { status: 'changed' } } : undefined)
    renderPage(<AccountPage />, '/account')
    expect(await screen.findByText('CAI5_AIS4_S8')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Current password'), PASSWORD)
    await userEvent.type(screen.getByLabelText('New password'), `${PASSWORD} new`)
    await userEvent.type(screen.getByLabelText('New password again'), `${PASSWORD} typo`)
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(screen.getByText('The two new passwords differ.')).toBeInTheDocument()
    expect(calls.some((c) => c.url.endsWith('/auth/password'))).toBe(false)

    await userEvent.clear(screen.getByLabelText('New password again'))
    await userEvent.type(screen.getByLabelText('New password again'), `${PASSWORD} new`)
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    await waitFor(() => expect(calls.some((c) => c.url.endsWith('/auth/password'))).toBe(true))
    expect(JSON.parse(calls.find((c) => c.url.endsWith('/auth/password'))!.body!)).toEqual({ currentPassword: PASSWORD, newPassword: `${PASSWORD} new` })
    expect(await screen.findByText('Password changed')).toBeInTheDocument()
    expect(screen.getByLabelText('Current password')).toHaveValue('')
  })
})

describe('editing as a coordinator', () => {
  it('offers only their own groups to move a recording to', async () => {
    fakeBackend(signedInAs(coordinatorMe(['CAI5_AIS4_S7', 'CAI5_AIS4_S9'])))
    renderPage(<EditRecordingModal recording={recording() as never} onClose={() => {}} />)
    const select = await screen.findByRole('combobox', { name: /Group/ })
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(['CAI5_AIS4_S7', 'CAI5_AIS4_S9'])
  })
})


it('links each assigned group to its students, attendance and recordings', async () => {
  fakeBackend(signedInAs(coordinatorMe()), (call) => call.url === '/api/v1/dashboard/groups'
    ? { body: { groups: [groupSummary()], count: 1 } } : undefined)
  renderPage(<GroupsPage />, '/groups')
  const students = await screen.findByRole('link', { name: 'Students' })
  expect(students).toHaveAttribute('href', '/students?group=CAI5_AIS4_S7')
  expect(screen.getByRole('link', { name: 'Attendance' })).toHaveAttribute('href', '/attendance?group=CAI5_AIS4_S7')
  expect(screen.getByRole('link', { name: 'Recordings' })).toHaveAttribute('href', '/recordings?group=CAI5_AIS4_S7')
})
