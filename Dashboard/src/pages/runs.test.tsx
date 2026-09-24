import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { navFor } from '../components/Layout'
import { adminMe, fakeBackend, renderPage, signedInAs, type Call } from '../test/helpers'
import { RunsPage } from './RunsPage'

const lmsAccount = (email: string) => ({ id: `a-${email}`, label: 'Coordinator', email, role: 'coordinator', active: true })

const zoomAccount = (accountId: string, overrides: Record<string, unknown> = {}) => ({
  id: `z-${accountId}`,
  accountId,
  label: accountId,
  zoomEmail: `${accountId.toLowerCase()}@zoom.example.com`,
  group: accountId,
  meetingUrl: 'https://zoom.us/j/91473108490',
  preferredEngine: null,
  active: true,
  ...overrides,
})

const person = (overrides: Record<string, unknown> = {}) => ({
  coordinatorId: 'u-mona',
  username: 'mona',
  displayName: 'Mona',
  status: 'active',
  enabled: false,
  groups: [{ id: 'g1', name: 'CAI5_AIS4_S7', displayName: null, archived: false }],
  lmsAccount: lmsAccount('mona@example.com'),
  lmsAccounts: [lmsAccount('mona@example.com')],
  zoomAccountId: 'z-CAI5_AIS4_S7',
  zoomAccount: 'CAI5_AIS4_S7',
  zoomAccounts: [zoomAccount('CAI5_AIS4_S7')],
  classes: { planned: 0, done: 0, skipped: 0, needsLink: 0 },
  updatedAt: null,
  ...overrides,
})

const lesson = (overrides: Record<string, unknown> = {}) => ({
  id: 'c1',
  coordinatorId: 'u-mona',
  group: 'CAI5_AIS4_S7',
  date: '2026-09-22',
  startTime: '19:00',
  title: '36 • Technical',
  meetingUrl: 'https://zoom.us/j/91473108490',
  zoomAccount: 'CAI5_AIS4_S7',
  preferredEngine: null,
  source: 'lms',
  status: 'planned',
  note: null,
  needsLink: false,
  importedAt: '2026-09-19T10:00:00Z',
  updatedAt: '2026-09-19T10:00:00Z',
  ...overrides,
})

const delegations = (people: unknown[]) => (call: Call) =>
  call.url === '/api/v1/admin/delegations' && call.method === 'GET' ? { body: { delegations: people } } : undefined

const runPlan = (classes: unknown[]) => (call: Call) =>
  call.url.startsWith('/api/v1/admin/run-plan?') && call.method === 'GET'
    ? { body: { classes, coordinators: [] } }
    : undefined

const accepted = (call: Call) =>
  call.url.startsWith('/api/v1/admin/') && call.method !== 'GET'
    ? { body: { coordinatorId: 'u-mona', enabled: true, alsoInGroup: 2, ...JSON.parse(call.body ?? '{}') } }
    : undefined

describe('running other coordinators’ classes', () => {
  it('is a page only the admin has', () => {
    expect(navFor('admin').map((item) => item.to)).toContain('/runs')
    expect(navFor('coordinator').map((item) => item.to)).not.toContain('/runs')
  })

  it('shows each coordinator with their groups and the LMS account their classes would go up under', async () => {
    fakeBackend(
      signedInAs(adminMe),
      delegations([
        person(),
        person({
          coordinatorId: 'u-sami', username: 'sami', displayName: 'Sami', enabled: true,
          groups: [{ id: 'g2', name: 'CAI5_AIS4_S8', displayName: null, archived: false }],
          lmsAccount: lmsAccount('sami@example.com'),
        }),
      ]),
      runPlan([]),
    )
    renderPage(<RunsPage />)

    expect(await screen.findByText('Mona')).toBeInTheDocument()
    expect(screen.getByText(/CAI5_AIS4_S7 · mona@example.com/)).toBeInTheDocument()
    expect(screen.getByText(/CAI5_AIS4_S8 · sami@example.com/)).toBeInTheDocument()
    expect(screen.getByLabelText("Run mona's classes")).toBeInTheDocument()
    expect(screen.getByLabelText("Stop running sami's classes")).toBeInTheDocument()
  })

  it('turning a coordinator on asks the server to run theirs', async () => {
    const calls = fakeBackend(signedInAs(adminMe), delegations([person()]), runPlan([]), accepted)
    renderPage(<RunsPage />)

    await userEvent.click(await screen.findByLabelText("Run mona's classes"))

    await waitFor(() => {
      const put = calls.find((call) => call.url === '/api/v1/admin/delegations/u-mona' && call.method === 'PUT')
      expect(put).toBeDefined()
      // Nothing is chosen: every group of theirs opens with its own account.
      expect(JSON.parse(put!.body!)).toMatchObject({ enabled: true, zoomAccountId: null })
      expect(put!.headers.get('X-Dashboard-Request')).toBe('1')
    })
    expect(await screen.findByText("Running Mona's classes")).toBeInTheDocument()
  })

  it('every group of theirs runs, each shown with the Zoom account that opens it', async () => {
    fakeBackend(
      signedInAs(adminMe),
      delegations([person({
        enabled: true,
        groups: [
          { id: 'g1', name: 'CAI5_AIS4_S7', displayName: null, archived: false },
          { id: 'g2', name: 'CAI5_AIS4_S8', displayName: null, archived: false },
          { id: 'g3', name: 'CAI5_AIS4_S9', displayName: null, archived: false },
        ],
        zoomAccounts: [zoomAccount('CAI5_AIS4_S7'), zoomAccount('CAI5_AIS4_S8')],
        groupsWithoutZoom: ['CAI5_AIS4_S9'],
      })]),
      runPlan([]),
    )
    renderPage(<RunsPage />)

    const list = await screen.findByLabelText("Mona's groups and their Zoom accounts")
    expect(within(list).getAllByRole('listitem').map((item) => item.textContent)).toEqual([
      'CAI5_AIS4_S7', 'CAI5_AIS4_S8', 'CAI5_AIS4_S9 · no Zoom account',
    ])
    expect(screen.queryByRole('combobox', { name: /opens with/i })).not.toBeInTheDocument()
  })

  it('an account with no link of its own says so', async () => {
    fakeBackend(
      signedInAs(adminMe),
      delegations([person({ enabled: true, zoomAccounts: [zoomAccount('CAI5_AIS4_S7', { meetingUrl: null })] })]),
      runPlan([]),
    )
    renderPage(<RunsPage />)

    expect(await screen.findByText('CAI5_AIS4_S7 — no link yet')).toBeInTheDocument()
  })

  it('a coordinator with no Zoom account of their own is told where it comes from', async () => {
    fakeBackend(signedInAs(adminMe), delegations([person({ zoomAccounts: [], zoomAccountId: null, zoomAccount: null })]), runPlan([]))
    renderPage(<RunsPage />)

    expect(await screen.findByText(/they add it in their own copy of the app/)).toBeInTheDocument()
  })

  it('a class with no Zoom link says so, because it cannot open without one', async () => {
    fakeBackend(
      signedInAs(adminMe),
      delegations([person({ enabled: true, classes: { planned: 2, done: 0, skipped: 0, needsLink: 1 } })]),
      runPlan([lesson(), lesson({ id: 'c2', date: '2026-09-29', meetingUrl: null, needsLink: true })]),
    )
    renderPage(<RunsPage />)

    expect(await screen.findByText('1 still need a Zoom link')).toBeInTheDocument()
    expect(screen.getByText('Without a link this class cannot open by itself.')).toBeInTheDocument()
    expect(screen.getByText(/1 of their classes still need a Zoom link/)).toBeInTheDocument()
  })

  it('one link can be written across a whole group at once', async () => {
    const calls = fakeBackend(signedInAs(adminMe), delegations([person({ enabled: true })]), runPlan([lesson({ meetingUrl: null, needsLink: true })]), accepted)
    renderPage(<RunsPage />)

    const box = await screen.findByLabelText('Zoom link for CAI5_AIS4_S7 on 2026-09-22')
    await userEvent.type(box, 'https://zoom.us/j/91473108490')
    await userEvent.click(screen.getByRole('button', { name: 'Whole group' }))

    await waitFor(() => {
      const patch = calls.find((call) => call.url === '/api/v1/admin/run-plan/c1' && call.method === 'PATCH')
      expect(JSON.parse(patch!.body!)).toEqual({ meetingUrl: 'https://zoom.us/j/91473108490', applyToGroup: true })
    })
    expect(await screen.findByText('2 more CAI5_AIS4_S7 class(es) use this link too.')).toBeInTheDocument()
  })

  it('what a class opens with is kept on the class', async () => {
    const calls = fakeBackend(signedInAs(adminMe), delegations([person({ enabled: true })]), runPlan([lesson()]), accepted)
    renderPage(<RunsPage />)

    await userEvent.selectOptions(await screen.findByLabelText('What CAI5_AIS4_S7 on 2026-09-22 opens with'), 'web')

    await waitFor(() => {
      const patch = calls.find((call) => call.method === 'PATCH')
      expect(JSON.parse(patch!.body!)).toEqual({ preferredEngine: 'web' })
    })
  })

  it('a class can be skipped and run again', async () => {
    const calls = fakeBackend(signedInAs(adminMe), delegations([person({ enabled: true })]), runPlan([lesson()]), accepted)
    renderPage(<RunsPage />)

    await userEvent.click(await screen.findByLabelText('Skip CAI5_AIS4_S7 on 2026-09-22'))

    await waitFor(() => {
      const patch = calls.find((call) => call.method === 'PATCH')
      expect(JSON.parse(patch!.body!)).toEqual({ status: 'skipped' })
    })
  })

  it('with more than one coordinator running, the list narrows to the ones picked', async () => {
    const calls = fakeBackend(
      signedInAs(adminMe),
      delegations([person({ enabled: true }), person({ coordinatorId: 'u-sami', username: 'sami', displayName: 'Sami', enabled: true })]),
      runPlan([lesson()]),
    )
    renderPage(<RunsPage />)

    const filter = within(await screen.findByRole('group', { name: 'Show only these coordinators' }))
    await userEvent.click(filter.getByRole('button', { name: 'Sami' }))

    await waitFor(() => {
      expect(calls.some((call) => call.url.includes('coordinator=u-sami'))).toBe(true)
    })
    expect(filter.getByRole('button', { name: 'Sami' })).toHaveAttribute('aria-pressed', 'true')
    expect(filter.getByRole('button', { name: 'Everyone' })).toHaveAttribute('aria-pressed', 'false')
  })

  it('with nobody turned on it says what to do first', async () => {
    fakeBackend(signedInAs(adminMe), delegations([person()]), runPlan([]))
    renderPage(<RunsPage />)

    expect(await screen.findByText(/Turn a coordinator on above/)).toBeInTheDocument()
  })

  it('never shows a password, only which account', async () => {
    const calls = fakeBackend(signedInAs(adminMe), delegations([person({ enabled: true })]), runPlan([lesson()]))
    renderPage(<RunsPage />)

    await screen.findAllByText('Mona')
    // The page reads the coordinator list and the plan, and nothing that carries a sign-in.
    expect(calls.every((call) => !call.url.includes('/secret'))).toBe(true)
    expect(document.body.textContent).toContain('mona@example.com')
  })

  it('a class whose group the app cannot match has its Zoom account chosen by hand', async () => {
    // A class on the coordinator's own LMS is run even when nobody gave them that group here, so
    // the account that opens it is picked on the class itself.
    const calls = fakeBackend(
      signedInAs(adminMe),
      delegations([person({ enabled: true, zoomAccounts: [zoomAccount('CAI5_AIS4_S7'), zoomAccount('CAI5_AIS4_S9')] })]),
      runPlan([lesson({ group: 'CAI5_AIS4_S9', zoomAccount: null })]),
      accepted,
    )
    renderPage(<RunsPage />)

    const choice = await screen.findByLabelText('Zoom account that opens CAI5_AIS4_S9 on 2026-09-22')
    await userEvent.selectOptions(choice, 'CAI5_AIS4_S9')

    await waitFor(() => {
      const patch = calls.find((call) => call.method === 'PATCH')
      expect(patch).toBeDefined()
      expect(JSON.parse(patch!.body!)).toMatchObject({ zoomAccount: 'CAI5_AIS4_S9', applyToGroup: true })
    })
  })
})
