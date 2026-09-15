import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { App } from '../App'
import { linkTone } from '../components/ui'
import { adminMe, coordinatorMe, fakeBackend, groupSummary, recording, renderPage, signedInAs } from '../test/helpers'
import { AgentsPage } from './AgentsPage'
import { GroupsPage } from './GroupsPage'
import { LoginPage } from './LoginPage'
import { readQuery, RecordingsPage } from './RecordingsPage'

const me = signedInAs(adminMe)
const groups = (call: { url: string }) =>
  call.url.startsWith('/api/v1/dashboard/groups') ? { body: { groups: [groupSummary()], count: 1 } } : undefined

describe('link status', () => {
  it('colours the combination of link and LMS status', () => {
    expect(linkTone({ link: 'drive', lms: 'attached', label: 'Drive · on LMS' })).toBe('green')
    expect(linkTone({ link: 'drive', lms: 'pending', label: 'Drive · pending' })).toBe('amber')
    expect(linkTone({ link: 'zoom', lms: 'pending', label: 'Zoom only · pending' })).toBe('blue')
    expect(linkTone({ link: 'missing', lms: 'pending', label: 'Missing · pending' })).toBe('red')
    expect(linkTone({ link: 'drive', lms: 'failed', label: 'Drive · LMS failed' })).toBe('red')
  })
})

describe('recordings page', () => {
  it('lists recordings with their link status and a link that opens safely', async () => {
    fakeBackend(groups, (call) =>
      call.url.startsWith('/api/v1/dashboard/recordings')
        ? { body: { items: [recording(), recording({ id: 'r2', group: 'CAI5_AIS4_S8', fileName: null, driveLink: null, source: 'zoom', linkStatus: { link: 'missing', lms: 'pending', label: 'Missing · pending' } })], total: 2, page: 1, pageSize: 25, sort: 'updated' } }
        : undefined,
    )
    renderPage(<RecordingsPage />, '/recordings')

    const withLink = (await screen.findByRole('button', { name: /Details of CAI5_AIS4_S7/ })).closest('tr')!
    expect(within(withLink).getByText('Drive')).toBeInTheDocument()
    expect(within(withLink).getByText('Pending')).toBeInTheDocument()
    expect(within(withLink).getByText('session.mp4')).toBeInTheDocument()
    expect(within(withLink).getByRole('button', { name: /Attach CAI5_AIS4_S7/ })).toBeEnabled()
    const withoutLink = screen.getByRole('button', { name: /Details of CAI5_AIS4_S8/ }).closest('tr')!
    expect(within(withoutLink).getByText('Missing')).toBeInTheDocument()          // no link: source "Missing"
    expect(within(withoutLink).queryByText('Zoom')).toBeNull()
    expect(within(withoutLink).getByRole('button', { name: /Attach CAI5_AIS4_S8/ })).toBeDisabled()
    expect(screen.queryByText(/1AbCdEfGhIjKlMnOp/)).toBeNull()                    // the link itself is not printed
    expect(screen.getByText('2 recordings')).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'Actions' })).toBeInTheDocument()
  })

  it('sends the filters to the backend and keeps them in the address', async () => {
    const calls = fakeBackend(groups, (call) =>
      call.url.startsWith('/api/v1/dashboard/recordings') ? { body: { items: [], total: 0, page: 1, pageSize: 25, sort: 'updated' } } : undefined,
    )
    renderPage(<RecordingsPage />, '/recordings?status=pending')
    await screen.findByText('No recordings match these filters.')
    expect(calls.some((c) => c.url.includes('status=pending') && c.url.includes('sort=updated'))).toBe(true)

    await screen.findByRole('option', { name: 'CAI5_AIS4_S7' })
    await userEvent.selectOptions(screen.getByLabelText('Group'), 'CAI5_AIS4_S7')
    await userEvent.selectOptions(screen.getByLabelText('Sort by'), 'created')
    fireEvent.change(screen.getByLabelText('Session date'), { target: { value: '2026-09-11' } })
    await waitFor(() =>
      expect(calls.some((c) => /group=CAI5_AIS4_S7/.test(c.url) && /sort=created/.test(c.url) && /date=2026-09-11/.test(c.url))).toBe(true),
    )
  })

  it('reads the defaults from an empty address', () => {
    expect(readQuery(new URLSearchParams())).toEqual({ group: '', date: '', status: '', link: '', sort: 'updated', page: 1 })
    expect(readQuery(new URLSearchParams('sort=bogus&page=-3')).sort).toBe('updated')
  })
})

describe('groups page', () => {
  it('shows a coordinator their groups with counts and links to the recordings', async () => {
    fakeBackend(signedInAs(coordinatorMe()), groups)
    renderPage(<GroupsPage />, '/groups')
    const link = await screen.findByRole('link', { name: 'CAI5_AIS4_S7' })
    expect(link).toHaveAttribute('href', '/recordings?group=CAI5_AIS4_S7')
    const row = link.closest('tr')!
    expect(within(row).getByText('2')).toBeInTheDocument()
    expect(within(row).getByText(/11.*Sep.*2026/)).toBeInTheDocument()
  })
})

describe('agents page', () => {
  it('shows online and offline devices and their assigned jobs', async () => {
    fakeBackend((call) =>
      call.url === '/api/v1/dashboard/agents'
        ? {
            body: {
              count: 2,
              online: 1,
              agents: [
                { deviceId: 'd1-aaaa-bbbb', name: 'PC-A', status: 'online', connected: true, agentState: 'busy', version: '1.0.0', capabilities: [], lastHeartbeat: new Date().toISOString(), lastAssignedAt: null, revoked: false, createdAt: '', updatedAt: '', jobsLast24h: { succeeded: 3, failed: 1 },
                  activeJobs: [{ jobId: 'j1', type: 'recording.process', status: 'running', group: 'CAI5_AIS4_S8', date: '2026-09-12', attempts: 1, createdAt: '', assignedAt: null, startedAt: null, finishedAt: null, errorCode: null, alreadyExists: null }] },
                { deviceId: 'd2-cccc-dddd', name: 'PC-B', status: 'offline', connected: false, agentState: null, version: '1.0.0', capabilities: [], lastHeartbeat: null, lastAssignedAt: null, revoked: false, createdAt: '', updatedAt: '', jobsLast24h: { succeeded: 0, failed: 0 }, activeJobs: [] },
              ],
            },
          }
        : undefined,
    )
    renderPage(<AgentsPage />, '/agents')
    const a = (await screen.findByText('PC-A')).closest('tr')!
    expect(within(a).getByText('Online')).toBeInTheDocument()
    expect(within(a).getByText('running')).toBeInTheDocument()
    expect(within(a).getByText('CAI5_AIS4_S8')).toBeInTheDocument()
    expect(within(a).getByText('just now')).toBeInTheDocument()
    const b = screen.getByText('PC-B').closest('tr')!
    expect(within(b).getByText('Offline')).toBeInTheDocument()
    expect(within(b).getByText('never')).toBeInTheDocument()
    expect(within(b).getByText('None')).toBeInTheDocument()
    expect(screen.getByText(/1 of 2 online/)).toBeInTheDocument()
  })
})

describe('signing in', () => {
  it('sends the user to the login page when there is no session', async () => {
    fakeBackend(signedInAs(null))
    renderPage(<App />, '/agents')
    expect(await screen.findByRole('form', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('posts the credentials with the dashboard header and shows a refusal', async () => {
    const calls = fakeBackend(
      signedInAs(null),
      (call) => (call.url.endsWith('/auth/login') ? { status: 401, body: { error: 'Invalid username or password' } } : undefined),
    )
    renderPage(<LoginPage />, '/login')
    await userEvent.type(screen.getByLabelText('Username'), 'admin')
    await userEvent.type(screen.getByLabelText('Password'), 'not-the-password')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid username or password.')
    const login = calls.find((c) => c.url.endsWith('/auth/login'))!
    expect(login.method).toBe('POST')
    expect(login.headers.get('X-Dashboard-Request')).toBe('1')
    expect(JSON.parse(login.body!)).toEqual({ username: 'admin', password: 'not-the-password' })
    expect(screen.getByLabelText('Password')).toHaveValue('')   // cleared after a failed attempt
  })

  it('shows the dashboard once signed in', async () => {
    fakeBackend(me, (call) =>
      call.url === '/api/v1/dashboard/overview'
        ? { body: { agents: { total: 1, online: 1, busy: 0 }, recordings: { total: 4, pending: 3, onLms: 1, missingLink: 1 }, jobs: { queued: 0, assigned: 0, running: 0, succeededLast24h: 2, failedLast24h: 0 }, groups: 3, serverTime: '' } }
        : call.url.startsWith('/api/v1/dashboard/recordings')
          ? { body: { items: [recording()], total: 1, page: 1, pageSize: 10, sort: 'updated' } }
          : undefined,
    )
    renderPage(<App />, '/')
    expect(await screen.findByText('1 / 1')).toBeInTheDocument()
    expect(screen.getByText('Signed in as')).toBeInTheDocument()
    const latest = (await screen.findByText('session.mp4')).closest('tr')!
    expect(within(latest).getByText('Pending')).toBeInTheDocument()
    const open = within(latest).getByRole('link', { name: /open/i })             // the overview keeps its read-only link
    expect(open).toHaveAttribute('target', '_blank')
    expect(open).toHaveAttribute('rel', 'noopener noreferrer')
    expect(screen.queryByRole('button', { name: /^Edit/ })).toBeNull()           // no operations on the overview
  })
})
