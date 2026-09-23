import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { navFor, SECTIONS } from '../components/Layout'
import { adminMe, coordinatorMe, fakeBackend, renderPage, signedInAs, type Call } from '../test/helpers'
import { ActivityPage, describeKind, outcomeTone } from './ActivityPage'
import { LmsAccountsPage } from './LmsAccountsPage'
import { DAYS, newId, parseDays, SchedulesPage, toTimeField, toTimeValue, writeDays } from './SchedulesPage'
import { listPeople, parsePeople, SessionRolesPage } from './SessionRolesPage'
import { POLICY_DEFAULTS, SettingsPage } from './SettingsPage'
import { OverviewPage } from './OverviewPage'
import { ZoomAccountsPage } from './ZoomAccountsPage'

// Made-up passwords, for these tests only.
const ZOOM_PASSWORD = 'test-only zoom 123'
const LMS_PASSWORD = 'test-only lms 456'

const zoomAccount = (overrides: Record<string, unknown> = {}) => ({
  id: 'z1',
  accountId: 'eyouth-1',
  label: 'eyouth-1',
  zoomEmail: 'coordinator@eyouth.example.com',
  group: 'CAI5_AIS4_S7',
  meetingUrl: 'https://zoom.us/j/1234567890',
  preferredEngine: 'web',
  hasPassword: true,
  active: true,
  updatedAt: '2026-09-22T10:00:00Z',
  ...overrides,
})

const zoomAccounts = (...accounts: unknown[]) => (call: Call) =>
  call.url === '/api/v1/me/zoom-accounts' ? { body: { accounts } } : undefined

const lmsAccounts = (accounts: unknown[], canKeepPasswords = true) => (call: Call) =>
  call.url === '/api/v1/me/lms-accounts' && call.method === 'GET' ? { body: { accounts, canKeepPasswords } } : undefined

const lmsAccount = (overrides: Record<string, unknown> = {}) => ({
  id: 'l1',
  label: 'Coordinator',
  email: 'omar@eyouth.example.com',
  role: 'coordinator',
  active: true,
  updatedAt: '2026-09-22T09:00:00Z',
  ...overrides,
})

const schedule = (overrides: Record<string, unknown> = {}) => ({
  id: '7f1f0a8e-0000-4000-8000-000000000001',
  name: 'CAI5_AIS4_S7 Tuesday',
  meetingUrl: 'https://zoom.us/j/1234567890',
  accountId: 'eyouth-1',
  time: '19:00:00',
  days: 'Tuesday, Thursday',
  enabled: true,
  occurrenceDate: null,
  groupName: 'CAI5_AIS4_S7',
  preferredEngine: 'Web',
  lastTriggeredDate: '2026-09-17',
  ...overrides,
})

describe('the menu', () => {
  it('holds every part of the work, in sections, and keeps other people out of a coordinator’s', () => {
    const admin = navFor('admin', 3)
    const mine = navFor('coordinator')
    // Everything the app does has a page here.
    for (const page of ['/', '/sessions', '/activity', '/runs', '/attendance', '/recordings', '/students', '/groups',
      '/users', '/zoom-accounts', '/lms-accounts', '/schedules', '/session-roles', '/agents', '/settings']) {
      expect(admin.map((item) => item.to)).toContain(page)
    }
    expect(admin.find((item) => item.to === '/users')?.badge).toBe(3)
    // A coordinator sets their own class up, and sees nothing of the machines or other people.
    for (const page of ['/zoom-accounts', '/lms-accounts', '/schedules', '/session-roles', '/activity', '/settings']) {
      expect(mine.map((item) => item.to)).toContain(page)
    }
    expect(mine.map((item) => item.to)).not.toContain('/users')
    expect(mine.map((item) => item.to)).not.toContain('/agents')
    expect(mine.map((item) => item.to)).not.toContain('/runs')
    // Every item belongs to one of the sections the sidebar draws.
    for (const item of [...admin, ...mine]) expect(SECTIONS).toContain(item.section)
  })
})

describe('Zoom accounts', () => {
  it('lists the accounts, says which have a password, and which one is in use', async () => {
    fakeBackend(signedInAs(adminMe), zoomAccounts(zoomAccount(), zoomAccount({ id: 'z2', accountId: 'eyouth-2', group: 'CAI5_AIS4_S8', hasPassword: false, active: false })))
    renderPage(<ZoomAccountsPage />, '/zoom-accounts')

    const first = (await screen.findByText('eyouth-1')).closest('tr')!
    expect(within(first).getByText('In use')).toBeInTheDocument()
    expect(within(first).getByText('Saved')).toBeInTheDocument()
    expect(within(first).getByText('CAI5_AIS4_S7')).toBeInTheDocument()
    expect(within(first).getByText('Zoom in a browser')).toBeInTheDocument()
    const second = screen.getByText('eyouth-2').closest('tr')!
    expect(within(second).getByText('None')).toBeInTheDocument()       // a server cannot sign in as it
    expect(screen.queryByText(ZOOM_PASSWORD)).toBeNull()
  })

  it('adds an account with its password, and sends the whole set so nothing already there is lost', async () => {
    const calls = fakeBackend(signedInAs(adminMe), zoomAccounts(zoomAccount()), (call) =>
      call.url === '/api/v1/me/zoom-accounts' && call.method === 'PUT' ? { body: { accounts: [] } } : undefined)
    renderPage(<ZoomAccountsPage />, '/zoom-accounts')
    await screen.findByText('eyouth-1')

    await userEvent.click(screen.getByRole('button', { name: 'Add an account' }))
    const form = screen.getByRole('form', { name: 'Zoom account' })
    await userEvent.type(within(form).getByLabelText('Account name'), 'eyouth-3')
    await userEvent.type(within(form).getByLabelText('Zoom email'), 'third@eyouth.example.com')
    await userEvent.type(within(form).getByLabelText('Group it hosts'), 'CAI5_AIS4_S9')
    await userEvent.type(within(form).getByLabelText('Meeting link'), 'https://zoom.us/j/999')
    await userEvent.type(within(form).getByLabelText('Zoom password'), ZOOM_PASSWORD)
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))

    const put = await waitFor(() => calls.find((call) => call.method === 'PUT')!)
    const sent = JSON.parse(put.body!) as { accounts: { accountId: string; password?: string }[] }
    expect(sent.accounts.map((account) => account.accountId)).toEqual(['eyouth-1', 'eyouth-3'])
    // The one that was already there keeps its password: none is sent for it.
    expect(sent.accounts[0].password).toBeUndefined()
    expect(sent.accounts[1].password).toBe(ZOOM_PASSWORD)
    expect(put.headers.get('X-Dashboard-Request')).toBe('1')
  })

  it('refuses a meeting link that is not https, and a name already taken', async () => {
    fakeBackend(signedInAs(adminMe), zoomAccounts(zoomAccount()))
    renderPage(<ZoomAccountsPage />, '/zoom-accounts')
    await screen.findByText('eyouth-1')

    await userEvent.click(screen.getByRole('button', { name: 'Add an account' }))
    const form = screen.getByRole('form', { name: 'Zoom account' })
    await userEvent.type(within(form).getByLabelText('Account name'), 'eyouth-1')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    expect(await screen.findByText('You already have an account with that name.')).toBeInTheDocument()

    await userEvent.clear(within(form).getByLabelText('Account name'))
    await userEvent.type(within(form).getByLabelText('Account name'), 'eyouth-4')
    await userEvent.type(within(form).getByLabelText('Meeting link'), 'zoom.us/j/1')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    expect(await screen.findByText('A meeting link must start with https://.')).toBeInTheDocument()
  })

  it('removes an account by leaving it out of the set it sends', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const calls = fakeBackend(signedInAs(adminMe), zoomAccounts(zoomAccount(), zoomAccount({ id: 'z2', accountId: 'eyouth-2', active: false })), (call) =>
      call.url === '/api/v1/me/zoom-accounts' && call.method === 'PUT' ? { body: { accounts: [] } } : undefined)
    renderPage(<ZoomAccountsPage />, '/zoom-accounts')
    await screen.findByText('eyouth-2')

    await userEvent.click(screen.getByRole('button', { name: 'Remove eyouth-2' }))
    const put = await waitFor(() => calls.find((call) => call.method === 'PUT')!)
    expect((JSON.parse(put.body!) as { accounts: { accountId: string }[] }).accounts.map((a) => a.accountId)).toEqual(['eyouth-1'])
  })
})

describe('LMS sign-ins', () => {
  it('saves a sign-in with its password and says it will be used', async () => {
    const calls = fakeBackend(signedInAs(coordinatorMe()), lmsAccounts([]), (call) =>
      call.url === '/api/v1/me/lms-accounts' && call.method === 'POST' ? { body: lmsAccount(), status: 201 } : undefined)
    renderPage(<LmsAccountsPage />, '/lms-accounts')
    await screen.findByText(/No LMS sign-in yet/)

    await userEvent.click(screen.getByRole('button', { name: 'Add a sign-in' }))
    const form = screen.getByRole('form', { name: 'LMS sign-in' })
    await userEvent.type(within(form).getByLabelText('Email'), 'omar@eyouth.example.com')
    await userEvent.type(within(form).getByLabelText('Password'), LMS_PASSWORD)
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))

    const post = await waitFor(() => calls.find((call) => call.method === 'POST')!)
    expect(JSON.parse(post.body!)).toMatchObject({ email: 'omar@eyouth.example.com', password: LMS_PASSWORD, active: true, role: 'coordinator' })
  })

  it('will not save a sign-in without its password', async () => {
    fakeBackend(signedInAs(coordinatorMe()), lmsAccounts([]))
    renderPage(<LmsAccountsPage />, '/lms-accounts')
    await screen.findByText(/No LMS sign-in yet/)

    await userEvent.click(screen.getByRole('button', { name: 'Add a sign-in' }))
    const form = screen.getByRole('form', { name: 'LMS sign-in' })
    await userEvent.type(within(form).getByLabelText('Email'), 'omar@eyouth.example.com')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    expect(await screen.findByText(/password is required/)).toBeInTheDocument()
  })

  it('says a server with no encryption key cannot keep a sign-in, and does not offer to save one', async () => {
    fakeBackend(signedInAs(coordinatorMe()), lmsAccounts([], false))
    renderPage(<LmsAccountsPage />, '/lms-accounts')
    expect(await screen.findByText(/cannot keep passwords/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Add a sign-in' }))
    const form = screen.getByRole('form', { name: 'LMS sign-in' })
    expect(within(form).getByRole('button', { name: 'Save' })).toBeDisabled()
  })

  it('switches which sign-in classes go up under', async () => {
    const calls = fakeBackend(
      signedInAs(coordinatorMe()),
      lmsAccounts([lmsAccount(), lmsAccount({ id: 'l2', email: 'omar.admin@eyouth.example.com', active: false })]),
      (call) => call.url.endsWith('/use') ? { body: lmsAccount({ id: 'l2' }) } : undefined,
    )
    renderPage(<LmsAccountsPage />, '/lms-accounts')
    await screen.findByText('omar.admin@eyouth.example.com')

    await userEvent.click(screen.getByRole('button', { name: 'Use omar.admin@eyouth.example.com' }))
    await waitFor(() => expect(calls.some((call) => call.url === '/api/v1/me/lms-accounts/l2/use' && call.method === 'POST')).toBe(true))
  })
})

describe('classes that open by themselves', () => {
  it('reads and writes the days exactly as the app spells them', () => {
    expect(parseDays('Tuesday, Thursday')).toEqual(['Tuesday', 'Thursday'])
    expect(parseDays('EveryDay')).toEqual([...DAYS])
    expect(parseDays('None')).toEqual([])
    expect(parseDays(null)).toEqual([])
    expect(writeDays(['Thursday', 'Tuesday'])).toBe('Tuesday, Thursday')     // always in week order
    expect(writeDays([])).toBe('None')
    expect(writeDays([...DAYS])).toBe('EveryDay')
    expect(toTimeField('19:00:00')).toBe('19:00')
    expect(toTimeValue('19:00')).toBe('19:00:00')
  })

  it('gives a new class an id of the app’s own shape, with or without a secure browser', () => {
    const shape = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/
    expect(newId()).toMatch(shape)
    // A dashboard opened over plain http has no crypto.randomUUID; adding a class still works.
    const real = globalThis.crypto
    vi.stubGlobal('crypto', { getRandomValues: real.getRandomValues.bind(real) })
    try {
      expect(newId()).toMatch(shape)
      expect(newId()).not.toBe(newId())
    } finally {
      vi.stubGlobal('crypto', real)
    }
  })

  it('lists them with their days and time, and turns one off without touching the others', async () => {
    const calls = fakeBackend(signedInAs(coordinatorMe()), (call) =>
      call.url === '/api/v1/me/schedules' && call.method === 'GET'
        ? { body: { schedules: [schedule(), schedule({ id: 'b', name: 'One-off', days: 'None', occurrenceDate: '2026-10-01', enabled: false })], count: 2, deviceName: 'OMAR-PC', updatedAt: '2026-09-22T08:00:00Z' } }
        : call.url === '/api/v1/me/schedules' && call.method === 'PUT' ? { body: { count: 2, updatedAt: '' } } : undefined)
    renderPage(<SchedulesPage />, '/schedules')

    const weekly = (await screen.findByText('CAI5_AIS4_S7 Tuesday')).closest('tr')!
    expect(within(weekly).getByText('19:00')).toBeInTheDocument()
    expect(within(weekly).getByText('Tue, Thu')).toBeInTheDocument()
    expect(within(weekly).getByText('Zoom in a browser')).toBeInTheDocument()
    const once = screen.getByText('One-off').closest('tr')!
    expect(within(once).getByText('Once, on 2026-10-01')).toBeInTheDocument()
    expect(within(once).getByText('Off')).toBeInTheDocument()
    expect(screen.getByText(/last from OMAR-PC/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Turn off CAI5_AIS4_S7 Tuesday' }))
    const put = await waitFor(() => calls.find((call) => call.method === 'PUT')!)
    const sent = JSON.parse(put.body!) as { schedules: { name: string; enabled: boolean }[]; deviceName: string }
    expect(sent.schedules).toHaveLength(2)
    expect(sent.schedules.find((row) => row.name === 'CAI5_AIS4_S7 Tuesday')!.enabled).toBe(false)
    expect(sent.schedules.find((row) => row.name === 'One-off')!.enabled).toBe(false)
    expect(sent.deviceName).toBe('OMAR-PC')
  })

  it('adds one in the app’s own shape, and asks for a day or a date', async () => {
    const calls = fakeBackend(signedInAs(coordinatorMe()), (call) =>
      call.url === '/api/v1/me/schedules' && call.method === 'GET' ? { body: { schedules: [], count: 0, deviceName: null, updatedAt: null } }
        : call.url === '/api/v1/me/schedules' && call.method === 'PUT' ? { body: { count: 1, updatedAt: '' } } : undefined)
    renderPage(<SchedulesPage />, '/schedules')
    await screen.findByText(/Nothing opens by itself yet/)

    await userEvent.click(screen.getByRole('button', { name: 'Add a class' }))
    const form = screen.getByRole('form', { name: 'Class that opens by itself' })
    await userEvent.type(within(form).getByLabelText('Name'), 'S9 Sunday')
    await userEvent.type(within(form).getByLabelText('Meeting link'), 'https://zoom.us/j/555')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    expect(await screen.findByText('Pick the days it repeats on, or a single date.')).toBeInTheDocument()

    await userEvent.click(within(form).getByRole('button', { name: 'Sun' }))
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    const put = await waitFor(() => calls.find((call) => call.method === 'PUT')!)
    const row = (JSON.parse(put.body!) as { schedules: Record<string, unknown>[] }).schedules[0]
    expect(row).toMatchObject({ name: 'S9 Sunday', days: 'Sunday', time: '19:00:00', enabled: true, occurrenceDate: null })
    expect(typeof row.id).toBe('string')
  })
})

describe('who is made co-host', () => {
  const roles = (profiles: unknown[], updatedAt: string | null = '2026-09-22T07:00:00Z') => (call: Call) =>
    call.url === '/api/v1/settings/sessionRoles' && call.method === 'GET'
      ? { body: { key: 'sessionRoles', value: { profiles }, updatedAt } }
      : undefined
  const profile = {
    sessionType: 'Technical',
    keywords: ['python', 'data'],
    accounts: [],
    people: [{ name: 'Nada Instructor', role: 'Instructor', aliases: ['Nada I.'] }, { name: 'Mona Samir', role: 'CoHost' }],
  }

  it('turns a list of names into people of the right role', () => {
    expect(parsePeople('Nada Instructor, Mona Samir\nNada Instructor', 'Instructor')).toEqual([
      { name: 'Nada Instructor', role: 'Instructor' },
      { name: 'Mona Samir', role: 'Instructor' },
    ])
    expect(listPeople(profile as never, 'CoHost').map((person) => person.name)).toEqual(['Mona Samir'])
  })

  it('shows the instructor of each kind of session, and says which profile covers everything', async () => {
    fakeBackend(signedInAs(adminMe), roles([profile, { sessionType: 'Anything', keywords: [], accounts: [], people: [{ name: 'Hany', role: 'Instructor' }] }]))
    renderPage(<SessionRolesPage />, '/session-roles')

    const technical = (await screen.findByText('Technical')).closest('tr')!
    expect(within(technical).getByText('Nada Instructor')).toBeInTheDocument()
    expect(within(technical).getByText('Mona Samir')).toBeInTheDocument()
    expect(within(technical).getByText(/python, data/)).toBeInTheDocument()
    expect(within(screen.getByText('Anything').closest('tr')!).getByText('Every meeting')).toBeInTheDocument()
  })

  it('keeps the Zoom names a machine already learnt for somebody when the admin edits a profile', async () => {
    const calls = fakeBackend(signedInAs(adminMe), roles([profile]), (call) =>
      call.url === '/api/v1/settings/sessionRoles' && call.method === 'PUT' ? { body: { key: 'sessionRoles', value: null, updatedAt: '' } } : undefined)
    renderPage(<SessionRolesPage />, '/session-roles')
    await screen.findByText('Technical')

    await userEvent.click(screen.getByRole('button', { name: 'Edit Technical' }))
    const form = screen.getByRole('form', { name: 'Session type' })
    await userEvent.type(within(form).getByLabelText('Anybody else who may be co-host'), ', Sara K')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))

    const put = await waitFor(() => calls.find((call) => call.method === 'PUT')!)
    const sent = JSON.parse(put.body!) as { value: { profiles: { people: { name: string; role: string; aliases?: string[] }[] }[] } }
    const people = sent.value.profiles[0].people
    expect(people.find((person) => person.name === 'Nada Instructor')!.aliases).toEqual(['Nada I.'])
    expect(people.map((person) => person.name)).toContain('Sara K')
  })

  it('lets a coordinator read them but not change them', async () => {
    fakeBackend(signedInAs(coordinatorMe()), roles([profile]))
    renderPage(<SessionRolesPage />, '/session-roles')
    await screen.findByText('Technical')
    expect(screen.getByText(/only the admin changes them/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add a session type' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Edit Technical' })).toBeNull()
  })
})

describe('settings', () => {
  const policy = (value: unknown, updatedAt: string | null = null) => (call: Call) =>
    call.url === '/api/v1/settings/cloudPolicy' && call.method === 'GET'
      ? { body: { key: 'cloudPolicy', value, updatedAt } }
      : undefined
  const sheet = (call: Call) =>
    call.url === '/api/v1/settings/recordingsSheet' && call.method === 'GET'
      ? { body: { key: 'recordingsSheet', value: { spreadsheetId: 'sheet-1' }, updatedAt: null } }
      : undefined

  it('treats an unsaved setting as the app’s own defaults', () => {
    expect(POLICY_DEFAULTS).toEqual({ autoCoHost: true, autoEnd: true })
  })

  it('sends the whole set of switches when one is changed, so none is lost', async () => {
    const calls = fakeBackend(signedInAs(adminMe), policy({ autoCoHost: true, autoEnd: true }), sheet, (call) =>
      call.url === '/api/v1/settings/cloudPolicy' && call.method === 'PUT' ? { body: { key: 'cloudPolicy', value: null, updatedAt: '' } } : undefined)
    renderPage(<SettingsPage />, '/settings')

    const ending = await screen.findByLabelText(/End a class when it is over/)
    await userEvent.click(ending)
    const put = await waitFor(() => calls.find((call) => call.method === 'PUT')!)
    expect(JSON.parse(put.body!)).toEqual({ value: { autoCoHost: true, autoEnd: false } })
  })

  it('shows a coordinator what the machines do without letting them change it', async () => {
    fakeBackend(signedInAs(coordinatorMe()), policy(null))
    renderPage(<SettingsPage />, '/settings')

    expect(await screen.findByLabelText(/Make the instructor co-host/)).toBeDisabled()
    expect(screen.getByText(/Only the admin changes these/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /enrollment token/i })).toBeNull()
  })

  it('says whether attendance matching can ask an AI, and never asks for its key', async () => {
    fakeBackend(signedInAs(adminMe), policy(null), sheet, (call) =>
      call.url === '/api/v1/dashboard/ai' ? { body: { available: true, model: 'openai/gpt-4o-mini' } } : undefined)
    renderPage(<SettingsPage />, '/settings')

    expect(await screen.findByText('openai/gpt-4o-mini')).toBeInTheDocument()
    expect(screen.getByText(/never sent to a browser/)).toBeInTheDocument()
    expect(screen.queryByLabelText(/api key/i)).toBeNull()
  })

  it('gives the admin a token for a machine to join with, once', async () => {
    fakeBackend(signedInAs(adminMe), policy(null), sheet, (call) =>
      call.url === '/api/v1/me/devices/enroll' ? { body: { enrollmentToken: 'zaae_test_token', expiresInSeconds: 900 } } : undefined)
    renderPage(<SettingsPage />, '/settings')

    await userEvent.type(await screen.findByLabelText('A name for it'), 'worker-1')
    await userEvent.click(screen.getByRole('button', { name: 'Make an enrollment token' }))
    expect(await screen.findByText('zaae_test_token')).toBeInTheDocument()
    expect(screen.getByText(/works once/)).toBeInTheDocument()
  })
})

describe('the overview', () => {
  const sessions = (classes: unknown[]) => (call: Call) =>
    call.url.startsWith('/api/v1/dashboard/sessions')
      ? { body: { from: '2026-09-23', to: '2026-09-23', classes, counters: { runningOnTheLms: 0, classesToday: classes.length, needAttention: 0, blocked: 0, fullyDone: 0 }, groups: [] } }
      : undefined
  const noRecordings = (call: Call) =>
    call.url.startsWith('/api/v1/dashboard/recordings') ? { body: { items: [], total: 0, page: 1, pageSize: 10, sort: 'updated' } } : undefined
  const overview = (call: Call) =>
    call.url === '/api/v1/dashboard/overview'
      ? { body: { agents: { total: 1, online: 1, busy: 1 }, recordings: { total: 0, pending: 0, onLms: 0, missingLink: 0 }, jobs: { queued: 0, assigned: 0, running: 1, succeededLast24h: 0, failedLast24h: 0 }, groups: 2, serverTime: '' } }
      : undefined
  const aClass = (overrides: Record<string, unknown> = {}) => ({
    classPlanId: 'p1', group: 'CAI5_AIS4_S7', title: 'Freelancing Skills', date: '2026-09-23',
    startTime: '19:00', startsAt: null, coordinator: { id: 'u1', name: 'Mona' }, meetingUrl: null,
    planStatus: 'planned', headline: 'running',
    stages: [{ key: 'class.run', label: 'The meeting', caption: 'Opens', state: 'running' }],
    ...overrides,
  })

  it('puts what is happening now first, in the words of the stage doing it', async () => {
    fakeBackend(signedInAs(adminMe), overview, noRecordings, sessions([
      aClass(),
      aClass({ classPlanId: 'p2', group: 'CAI5_AIS4_S8', headline: 'needsAttention',
        stages: [{ key: 'lms.attendance', label: 'The attendance', caption: 'After class', state: 'failed' }] }),
      aClass({ classPlanId: 'p3', group: 'CAI5_AIS4_S9', headline: 'planned',
        stages: [{ key: 'class.run', label: 'The meeting', caption: 'Opens', state: 'later' }] }),
    ]))
    renderPage(<OverviewPage />, '/')

    const running = (await screen.findByText('CAI5_AIS4_S7')).closest('tr')!
    expect(within(running).getByText('The meeting')).toBeInTheDocument()
    expect(within(running).getByText('Running')).toBeInTheDocument()
    const failed = screen.getByText('CAI5_AIS4_S8').closest('tr')!
    expect(within(failed).getByText('The attendance failed')).toBeInTheDocument()
    expect(within(failed).getByText('Needs somebody')).toBeInTheDocument()
    // A class that has not started yet is not "right now".
    expect(screen.queryByText('CAI5_AIS4_S9')).toBeNull()
  })

  it('says plainly when nothing today wants anybody', async () => {
    fakeBackend(signedInAs(adminMe), overview, noRecordings, sessions([
      aClass({ headline: 'done', stages: [{ key: 'lms.complete', label: 'Complete', caption: 'After class', state: 'done' }] }),
    ]))
    renderPage(<OverviewPage />, '/')
    expect(await screen.findByText(/none of them running or waiting for anybody/)).toBeInTheDocument()
  })
})

describe('what the machines did', () => {
  const activity = (items: unknown[]) => (call: Call) =>
    call.url.startsWith('/api/v1/dashboard/activity') ? { body: { items } } : undefined
  const groups = (call: Call) =>
    call.url.startsWith('/api/v1/dashboard/groups') ? { body: { groups: [], count: 0 } } : undefined

  it('reads a machine’s own words for what it did', () => {
    expect(describeKind('class.ended')).toBe('Class ended')
    expect(describeKind('lms_attendance')).toBe('Lms attendance')
    expect(outcomeTone('done')).toBe('green')
    expect(outcomeTone('failed')).toBe('red')
    expect(outcomeTone('skipped')).toBe('slate')
  })

  it('lists what happened, with the class and the machine, and counts what failed', async () => {
    fakeBackend(signedInAs(adminMe), groups, activity([
      { id: 2, device: 'worker-1', deviceId: 'd1', at: '2026-09-22T16:05:00Z', kind: 'class.ended', outcome: 'done', group: 'CAI5_AIS4_S7', date: '2026-09-22', summary: 'nobody but the host is left', detail: { reason: 'host alone' } },
      { id: 1, device: null, deviceId: 'd2', at: '2026-09-22T13:00:00Z', kind: 'lms.attendance', outcome: 'failed', group: 'CAI5_AIS4_S8', date: '2026-09-22', summary: 'the session list did not load', detail: null },
    ]))
    renderPage(<ActivityPage />, '/activity')

    const ended = (await screen.findByText('Class ended')).closest('tr')!
    expect(within(ended).getByText('CAI5_AIS4_S7')).toBeInTheDocument()
    expect(within(ended).getByText('worker-1')).toBeInTheDocument()
    expect(within(ended).getByText('Done')).toBeInTheDocument()
    expect(within(ended).getByText('nobody but the host is left')).toBeInTheDocument()
    const failed = screen.getByText('Lms attendance').closest('tr')!
    expect(within(failed).getByText('Failed')).toBeInTheDocument()
    expect(within(failed).getByText('gone')).toBeInTheDocument()          // the machine was removed since
    expect(screen.getByText('1 of these failed.')).toBeInTheDocument()
  })

  it('narrows what is on screen to what a person is looking for', async () => {
    fakeBackend(signedInAs(adminMe), groups, activity([
      { id: 2, device: 'worker-1', deviceId: 'd1', at: '2026-09-22T16:05:00Z', kind: 'cohost.assigned', outcome: 'done', group: 'CAI5_AIS4_S7', date: '2026-09-22', summary: 'Nada Instructor was made co-host', detail: null },
      { id: 1, device: 'worker-1', deviceId: 'd1', at: '2026-09-22T13:00:00Z', kind: 'lms.attendance', outcome: 'done', group: 'CAI5_AIS4_S7', date: '2026-09-22', summary: 'wrote up 23 students', detail: null },
    ]))
    renderPage(<ActivityPage />, '/activity')
    await screen.findByText('Cohost assigned')

    await userEvent.type(screen.getByLabelText('Look for'), 'co-host')
    expect(screen.getByText('Cohost assigned')).toBeInTheDocument()
    expect(screen.queryByText('Lms attendance')).toBeNull()
    expect(screen.getByText('1 thing done')).toBeInTheDocument()

    await userEvent.clear(screen.getByLabelText('Look for'))
    await userEvent.type(screen.getByLabelText('Look for'), 'nothing like this')
    expect(await screen.findByText(/Nothing here matches/)).toBeInTheDocument()
  })

  it('asks only for the group that was picked', async () => {
    const calls = fakeBackend(signedInAs(coordinatorMe()), (call) =>
      call.url.startsWith('/api/v1/dashboard/groups') ? { body: { groups: [{ id: 'g1', group: 'CAI5_AIS4_S7', displayName: null, archived: false, recordings: 0, lastSessionDate: null, lastUpdatedAt: null, pending: 0, onLms: 0, missingLink: 0 }], count: 1 } } : undefined,
      activity([]))
    renderPage(<ActivityPage />, '/activity')

    await screen.findByRole('option', { name: 'CAI5_AIS4_S7' })
    await userEvent.selectOptions(screen.getByLabelText('Group'), 'CAI5_AIS4_S7')
    await waitFor(() => expect(calls.some((call) => call.url.includes('group=CAI5_AIS4_S7'))).toBe(true))
  })
})
