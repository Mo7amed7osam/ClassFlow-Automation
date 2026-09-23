import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { adminMe, coordinatorMe, fakeBackend, renderPage, signedInAs, type Call } from '../test/helpers'
import { SessionsPage, STAGE_JOBS } from './SessionsPage'

/** The eleven marks of a class card, as the backend sends them and the Windows app draws them. */
const stages = (overrides: Record<string, Record<string, unknown>> = {}) => [
  { key: 'zoom', label: 'Zoom', caption: 'Opens 16:45', state: 'done' },
  { key: 'run', label: 'Run', caption: 'At start', state: 'done' },
  { key: 'attendance', label: 'Attendance', caption: 'Later', state: 'failed', detail: 'the session list did not load', retryable: true },
  { key: 'lateJoiners', label: 'Late joiners', caption: 'Later', state: 'later' },
  { key: 'complete', label: 'Complete', caption: 'Later', state: 'later' },
  { key: 'ended', label: 'Ended', caption: 'After class', state: 'done' },
  { key: 'zoomReport', label: 'Zoom report', caption: 'Later', state: 'later' },
  { key: 'zoomRecording', label: 'Zoom recording', caption: 'Later', state: 'later' },
  { key: 'drive', label: 'Drive', caption: 'After class', state: 'waiting' },
  { key: 'material', label: 'Material', caption: 'None', state: 'missing' },
  { key: 'assignment', label: 'Assignment', caption: 'None', state: 'missing' },
].map((stage) => ({ ...stage, ...(overrides[stage.key] ?? {}) }))

const aClass = (overrides: Record<string, unknown> = {}) => ({
  classPlanId: 'p1',
  group: 'CAI5_IND1_G1',
  title: 'CAI5_IND1_G1 • 30.0 • Technical',
  date: '2026-09-23',
  startTime: '18:00',
  startsAt: null,
  coordinator: { id: 'u1', name: 'Mona' },
  meetingUrl: 'https://zoom.us/j/91473108491',
  planStatus: 'opened',
  headline: 'needsAttention',
  stages: stages(),
  ...overrides,
})

const sessions = (classes: unknown[]) => (call: Call) =>
  call.url.startsWith('/api/v1/dashboard/sessions')
    ? {
      body: {
        from: '2026-09-23', to: '2026-09-29', classes, groups: ['CAI5_IND1_G1'],
        counters: { runningOnTheLms: 0, classesToday: classes.length, needAttention: 1, blocked: 0, fullyDone: 0 },
      },
    }
    : undefined

describe('the class card', () => {
  it('draws every stage of a class, the same eleven the app does', async () => {
    fakeBackend(signedInAs(adminMe), sessions([aClass()]))
    renderPage(<SessionsPage />, '/sessions')

    await screen.findByText('CAI5_IND1_G1 • 30.0 • Technical')
    for (const label of ['Zoom', 'Run', 'Attendance', 'Late joiners', 'Complete', 'Ended',
      'Zoom report', 'Zoom recording', 'Drive', 'Material', 'Assignment']) {
      expect(screen.getByText(label)).toBeInTheDocument()
    }
    // A stage that failed says why, where it is read.
    expect(screen.getByText('the session list did not load')).toBeInTheDocument()
  })

  it('links to everything else about that class', async () => {
    fakeBackend(signedInAs(adminMe), sessions([aClass()]))
    renderPage(<SessionsPage />, '/sessions')

    const card = (await screen.findByText('CAI5_IND1_G1 • 30.0 • Technical')).closest('article')!
    expect(within(card).getByRole('link', { name: 'Who was there' })).toHaveAttribute('href', '/attendance?group=CAI5_IND1_G1&date=2026-09-23')
    expect(within(card).getByRole('link', { name: 'Recording' })).toHaveAttribute('href', '/recordings?group=CAI5_IND1_G1&date=2026-09-23')
    expect(within(card).getByRole('link', { name: 'What the machine did' })).toHaveAttribute('href', '/activity?group=CAI5_IND1_G1')
    const meeting = within(card).getByRole('link', { name: 'Open the meeting' })
    expect(meeting).toHaveAttribute('href', 'https://zoom.us/j/91473108491')
    expect(meeting).toHaveAttribute('rel', expect.stringContaining('noopener'))
  })

  it('has a machine do a stage that failed, from the card itself', async () => {
    const calls = fakeBackend(signedInAs(adminMe), sessions([aClass()]), (call) =>
      call.url.endsWith('/run') ? { status: 202, body: { jobId: 'j1', type: 'lms.attendance', status: 'queued', created: true, group: 'CAI5_IND1_G1', date: '2026-09-23', attempts: 0, createdAt: '', assignedAt: null, startedAt: null, finishedAt: null, errorCode: null, alreadyExists: null } } : undefined)
    renderPage(<SessionsPage />, '/sessions')

    const failed = await screen.findByRole('button', { name: /^Attendance: Failed/ })
    await userEvent.click(failed)

    const sent = await waitFor(() => calls.find((call) => call.url.endsWith('/run'))!)
    expect(sent.url).toBe('/api/v1/admin/run-plan/p1/run')
    expect(JSON.parse(sent.body!)).toEqual({ stage: 'lms.attendance' })
    expect(await screen.findByText('Attendance: queued')).toBeInTheDocument()
  })

  it('asks before doing a stage that is already done, so a meeting is not opened twice', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const calls = fakeBackend(signedInAs(adminMe), sessions([aClass()]))
    renderPage(<SessionsPage />, '/sessions')

    await userEvent.click(await screen.findByRole('button', { name: /^Zoom: Done/ }))
    expect(confirm).toHaveBeenCalled()
    expect(calls.some((call) => call.url.endsWith('/run'))).toBe(false)
    confirm.mockRestore()
  })

  it('offers nothing to press on a stage no machine runs, or to a coordinator', async () => {
    fakeBackend(signedInAs(adminMe), sessions([aClass()]))
    const admin = renderPage(<SessionsPage />, '/sessions')
    await screen.findByText('Material')
    // "Ended" is how the meeting stage finished, and Material and Assignment are not run from here.
    for (const label of ['Ended', 'Material', 'Assignment', 'Drive']) {
      expect(screen.queryByRole('button', { name: new RegExp(`^${label}:`) })).toBeNull()
    }
    expect(STAGE_JOBS.ended).toBeUndefined()
    admin.unmount()

    fakeBackend(signedInAs(coordinatorMe(['CAI5_IND1_G1'])), sessions([aClass()]))
    renderPage(<SessionsPage />, '/sessions')
    await screen.findByText('Attendance')
    expect(screen.queryByRole('button', { name: /^Attendance:/ })).toBeNull()
    expect(screen.queryByText(/Press a stage/)).toBeNull()
  })

  it('says why it could not run a stage instead of looking as though it did', async () => {
    fakeBackend(signedInAs(adminMe), sessions([aClass()]), (call) =>
      call.url.endsWith('/run')
        ? { status: 409, body: { error: 'Cannot run', details: 'Open the meeting and hold it: no Zoom link on the class.' } }
        : undefined)
    renderPage(<SessionsPage />, '/sessions')

    await userEvent.click(await screen.findByRole('button', { name: /^Attendance: Failed/ }))
    expect(await screen.findByText('Open the meeting and hold it: no Zoom link on the class.')).toBeInTheDocument()
  })
})
