import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { Recording } from '../api/types'
import { LmsStatusBadge, SourceBadge } from '../components/ui'
import { fakeBackend, recording, renderPage, type Call } from '../test/helpers'
import { RecordingsPage } from './RecordingsPage'

const JOB_ID = '55e61782-e19b-4738-8869-f3f5572c8aab'

/** A small in-memory backend: PATCH and attach really change what the next GET returns. */
function operationsBackend(options: { patchError?: { status: number; body: unknown }; attachError?: { status: number; body: unknown }; cancelError?: { status: number; body: unknown } } = {}) {
  const rows: Recording[] = [
    recording() as unknown as Recording,
    recording({ id: 'r2', group: 'CAI5_AIS4_S8', date: '2026-09-12', fileName: 'zoom-only.mp4', driveLink: null,
      zoomLink: 'https://us06web.zoom.us/rec/share/FAKE', source: 'zoom',
      linkStatus: { link: 'zoom', lms: 'pending', label: 'Zoom only · pending' } }) as unknown as Recording,
  ]
  const byId = (url: string) => rows.find((r) => url.includes(`/recordings/${r.id}`))!
  const calls = fakeBackend((call: Call) => {
    if (call.url.startsWith('/api/v1/dashboard/groups')) {
      return { body: { groups: [{ id: 'g1', group: 'CAI5_AIS4_S7', displayName: null, archived: false, recordings: 1, lastSessionDate: '2026-09-11', lastUpdatedAt: '', pending: 1, onLms: 0, missingLink: 0 }], count: 1 } }
    }
    if (call.method === 'PATCH') {
      if (options.patchError) return options.patchError
      const row = byId(call.url)
      const changes = JSON.parse(call.body!)
      if (changes.fileName !== undefined) row.fileName = changes.fileName
      if (changes.link !== undefined) {
        row.driveLink = changes.link
        row.lmsStatus = 'pending'
      }
      row.updatedAt = new Date().toISOString()
      return { body: { ...row, changed: Object.keys(changes) } }
    }
    if (call.method === 'POST' && call.url.endsWith('/attach')) {
      if (options.attachError) return options.attachError
      const row = byId(call.url)
      const body = JSON.parse(call.body!)
      row.lastJob = { jobId: JOB_ID, type: 'recording.process', status: 'queued', group: row.group, date: row.date, attempts: 0,
        createdAt: new Date().toISOString(), assignedAt: null, startedAt: null, finishedAt: null, errorCode: null, alreadyExists: null,
        recordingId: row.id, dryRun: body.dryRun, replaceExisting: body.replaceExisting }
      return { status: 202, body: { recordingId: row.id, jobId: JOB_ID, status: 'queued', dryRun: body.dryRun, replaceExisting: body.replaceExisting } }
    }
    if (call.method === 'POST' && call.url.endsWith('/cancel')) {
      if (options.cancelError) return options.cancelError
      const row = byId(call.url)
      row.lastJob = { ...row.lastJob!, status: 'cancelled', finishedAt: new Date().toISOString() }
      return { body: { recordingId: row.id, jobId: row.lastJob.jobId, status: 'cancelled' } }
    }
    const detail = call.url.match(/^\/api\/v1\/dashboard\/recordings\/([^/?]+)$/)
    if (detail) {
      const row = rows.find((r) => r.id === detail[1])!
      return { body: { recording: row, jobs: row.lastJob ? [row.lastJob] : [], audit: [{ action: 'recording.update', username: 'admin', details: { fields: ['fileName'] }, createdAt: new Date().toISOString() }] } }
    }
    if (call.url.startsWith('/api/v1/dashboard/recordings')) {
      return { body: { items: rows.map((r) => ({ ...r })), total: rows.length, page: 1, pageSize: 25, sort: 'updated' } }
    }
    return undefined
  })
  const listCalls = () => calls.filter((c) => c.method === 'GET' && /^\/api\/v1\/dashboard\/recordings(\?|$)/.test(c.url)).length
  return { calls, rows, listCalls }
}

const rowOf = async (group: string) => (await screen.findByRole('button', { name: new RegExp(`Details of ${group}`) })).closest('tr')!

describe('status badges', () => {
  const base = recording() as unknown as Recording
  const cases: [Partial<Recording>, string, string][] = [
    [{ lmsStatus: 'pending' }, 'Pending', 'amber'],
    [{ lmsStatus: 'attached' }, 'Attached', 'emerald'],
    [{ lmsStatus: 'failed' }, 'Failed', 'rose'],
    [{ lmsStatus: 'pending', lastJob: { status: 'running' } as Recording['lastJob'] }, 'Processing', 'sky'],
  ]
  it.each(cases)('shows %o as %s', (overrides, label, colour) => {
    render(<LmsStatusBadge recording={{ ...base, ...overrides }} />)
    const badge = screen.getByText(label).closest('span')!
    expect(badge.className).toContain(colour)
  })

  it('names the source Drive, Zoom or Missing', () => {
    const { rerender } = render(<SourceBadge recording={base} />)
    expect(screen.getByText('Drive')).toBeInTheDocument()
    rerender(<SourceBadge recording={{ ...base, linkStatus: { link: 'zoom', lms: 'pending', label: '' } }} />)
    expect(screen.getByText('Zoom')).toBeInTheDocument()
    rerender(<SourceBadge recording={{ ...base, linkStatus: { link: 'missing', lms: 'pending', label: '' } }} />)
    expect(screen.getByText('Missing')).toBeInTheDocument()
  })
})

describe('editing a recording', () => {
  it('loads the current values, refuses an empty group, then saves only what changed', async () => {
    const backend = operationsBackend()
    renderPage(<RecordingsPage />, '/recordings')
    const row = await rowOf('CAI5_AIS4_S7')
    await userEvent.click(within(row).getByRole('button', { name: /^Edit/ }))

    const dialog = await screen.findByRole('dialog', { name: 'Edit recording' })
    expect(within(dialog).getByLabelText(/Group/)).toHaveValue('CAI5_AIS4_S7')
    expect(within(dialog).getByLabelText(/Date/)).toHaveValue('2026-09-11')
    expect(within(dialog).getByLabelText(/Start time/)).toHaveValue('18:00')
    expect(within(dialog).getByLabelText(/File name/)).toHaveValue('session.mp4')
    expect(within(dialog).getByLabelText(/^Link/)).toHaveValue(recording().driveLink)

    await userEvent.clear(within(dialog).getByLabelText(/Group/))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))
    expect(within(dialog).getByText('Group is required.')).toBeInTheDocument()
    expect(backend.calls.some((c) => c.method === 'PATCH')).toBe(false)

    await userEvent.type(within(dialog).getByLabelText(/Group/), 'CAI5_AIS4_S7')
    const file = within(dialog).getByLabelText(/File name/)
    await userEvent.clear(file)
    await userEvent.type(file, 'session-final.mp4')
    const before = backend.listCalls()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Edit recording' })).toBeNull())   // closed
    const patch = backend.calls.find((c) => c.method === 'PATCH')!
    expect(patch.url).toBe('/api/v1/dashboard/recordings/r1')
    expect(patch.headers.get('X-Dashboard-Request')).toBe('1')
    expect(patch.headers.get('X-API-Key')).toBeNull()
    expect(JSON.parse(patch.body!)).toEqual({ fileName: 'session-final.mp4' })                          // only the change
    expect(await screen.findByText(/Saved CAI5_AIS4_S7/)).toBeInTheDocument()                             // success message
    await waitFor(() => expect(backend.listCalls()).toBeGreaterThan(before))                              // list refreshed
    expect(await within(await rowOf('CAI5_AIS4_S7')).findByText('session-final.mp4')).toBeInTheDocument()
  })

  it('warns that a new link resets the LMS status, and shows a refusal from the server', async () => {
    operationsBackend({ patchError: { status: 400, body: { error: 'Invalid request', details: "'link' must be a Google Drive link to one file." } } })
    renderPage(<RecordingsPage />, '/recordings')
    await userEvent.click(within(await rowOf('CAI5_AIS4_S7')).getByRole('button', { name: /^Edit/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit recording' })
    const link = within(dialog).getByLabelText(/^Link/)
    await userEvent.clear(link)
    await userEvent.type(link, 'https://example.com/video.mp4')
    expect(within(dialog).getByText(/puts the LMS status back to/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent("'link' must be a Google Drive link")
    expect(screen.getByRole('dialog', { name: 'Edit recording' })).toBeInTheDocument()                    // stays open
  })
})

describe('attaching a recording to the LMS', () => {
  it('confirms, sends replaceExisting false / dryRun false, shows the job and refreshes the row', async () => {
    const backend = operationsBackend()
    renderPage(<RecordingsPage />, '/recordings')
    await userEvent.click(within(await rowOf('CAI5_AIS4_S7')).getByRole('button', { name: /^Attach/ }))

    const dialog = await screen.findByRole('dialog', { name: 'Attach this recording to LMS?' })
    expect(within(dialog).getByText('CAI5_AIS4_S7')).toBeInTheDocument()
    expect(within(dialog).getByText(/11.*Sep.*2026/)).toBeInTheDocument()
    expect(within(dialog).getByText('session.mp4')).toBeInTheDocument()
    expect(within(dialog).getByText('Drive')).toBeInTheDocument()
    const before = backend.listCalls()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Attach to LMS' }))

    const done = await screen.findByRole('dialog', { name: 'Job created' })
    expect(within(done).getByText(JOB_ID)).toBeInTheDocument()
    expect(within(done).getByText('queued')).toBeInTheDocument()
    const post = backend.calls.find((c) => c.method === 'POST')!
    expect(post.url).toBe('/api/v1/dashboard/recordings/r1/attach')
    expect(post.headers.get('X-Dashboard-Request')).toBe('1')
    expect(JSON.parse(post.body!)).toEqual({ replaceExisting: false, dryRun: false })

    await waitFor(() => expect(backend.listCalls()).toBeGreaterThan(before))
    await userEvent.click(within(done).getByRole('button', { name: 'Done' }))
    const row = await rowOf('CAI5_AIS4_S7')
    expect(await within(row).findByText('Processing')).toBeInTheDocument()                                 // the job is in flight
    expect(within(row).getByRole('button', { name: /^Attach/ })).toBeDisabled()
  })

  it('shows why the backend refused, and cannot even start for a Zoom-only recording', async () => {
    operationsBackend({ attachError: { status: 409, body: { error: 'Cannot attach', details: { reason: 'jobInProgress', message: 'An attach job for this recording is already queued or running.', jobId: JOB_ID } } } })
    renderPage(<RecordingsPage />, '/recordings')
    expect(within(await rowOf('CAI5_AIS4_S8')).getByRole('button', { name: /^Attach/ })).toBeDisabled()   // Zoom only

    await userEvent.click(within(await rowOf('CAI5_AIS4_S7')).getByRole('button', { name: /^Attach/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Attach this recording to LMS?' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Attach to LMS' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('already queued or running (job 55e61782)')
  })
})

describe('recording details', () => {
  it('opens from the row with the recording and job information, and Escape closes the top overlay first', async () => {
    const backend = operationsBackend()
    backend.rows[0].lastJob = { jobId: JOB_ID, type: 'recording.process', status: 'succeeded', group: 'CAI5_AIS4_S7', date: '2026-09-11',
      attempts: 1, createdAt: '2026-09-13T14:00:00Z', assignedAt: null, startedAt: null, finishedAt: '2026-09-13T14:01:00Z',
      errorCode: null, alreadyExists: false, recordingId: 'r1', dryRun: false, replaceExisting: false }
    renderPage(<RecordingsPage />, '/recordings')
    await userEvent.click(within(await rowOf('CAI5_AIS4_S7')).getByText('session.mp4'))                   // the row itself

    const drawer = await screen.findByRole('dialog', { name: 'CAI5_AIS4_S7' })
    expect(await within(drawer).findByText('Link status')).toBeInTheDocument()
    expect(within(drawer).getByText('Drive · pending')).toBeInTheDocument()
    expect(within(drawer).getByText(JOB_ID)).toBeInTheDocument()
    expect(within(drawer).getByText('succeeded')).toBeInTheDocument()
    expect(within(drawer).getByText(/link attached/)).toBeInTheDocument()
    expect(within(drawer).getByText(/admin edited file name/)).toBeInTheDocument()
    expect(within(drawer).getByText('Created')).toBeInTheDocument()

    await userEvent.click(within(drawer).getByRole('button', { name: 'Edit' }))
    expect(await screen.findByRole('dialog', { name: 'Edit recording' })).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Edit recording' })).toBeNull())
    expect(screen.getByRole('dialog', { name: 'CAI5_AIS4_S7' })).toBeInTheDocument()                       // the drawer stays
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'CAI5_AIS4_S7' })).toBeNull())
  })
})

describe('loading and errors', () => {
  it('shows skeleton rows while loading and an error banner when the list fails', async () => {
    fakeBackend((call) => (call.url.startsWith('/api/v1/dashboard/recordings') ? { status: 500, body: { error: 'Internal error' } } : { body: { groups: [], count: 0 } }))
    const { container } = renderPage(<RecordingsPage />, '/recordings')
    expect(container.querySelectorAll('tr.animate-pulse').length).toBeGreaterThan(0)
    expect(await screen.findByRole('alert')).toHaveTextContent('Internal error')
  })
})

const queuedJob = (overrides: Record<string, unknown> = {}) => ({
  jobId: JOB_ID, type: 'recording.process', status: 'queued', group: 'CAI5_AIS4_S7', date: '2026-09-11', attempts: 0,
  createdAt: new Date().toISOString(), assignedAt: null, startedAt: null, finishedAt: null, errorCode: null, alreadyExists: null,
  recordingId: 'r1', dryRun: false, replaceExisting: true, ...overrides,
})

describe('cancelling an attach job', () => {
  it('offers Cancel only while the job waits, confirms, and frees the row to attach again', async () => {
    const { calls, rows } = operationsBackend()
    rows[0].lastJob = queuedJob() as Recording['lastJob']
    rows[1].lastJob = queuedJob({ jobId: 'j2', status: 'running', recordingId: 'r2' }) as Recording['lastJob']
    renderPage(<RecordingsPage />, '/recordings')

    const row = await rowOf('CAI5_AIS4_S7')
    expect(within(row).getByText('Processing')).toBeInTheDocument()
    expect(within(row).getByRole('button', { name: /Attach CAI5_AIS4_S7/ })).toBeDisabled()
    const running = await rowOf('CAI5_AIS4_S8')
    expect(within(running).queryByRole('button', { name: /Cancel the attach job/ })).toBeNull()   // an agent has it

    await userEvent.click(within(row).getByRole('button', { name: 'Cancel the attach job of CAI5_AIS4_S7 on 2026-09-11' }))
    const dialog = await screen.findByRole('dialog', { name: 'Cancel the attach job?' })
    expect(dialog).toHaveTextContent('a real attach, set to replace an existing link')
    expect(dialog).toHaveTextContent(JOB_ID)
    expect(calls.some((c) => c.url.endsWith('/cancel'))).toBe(false)                                   // nothing until confirmed
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel the job' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    const sent = calls.find((c) => c.url.endsWith('/cancel'))!
    expect(sent.url).toBe('/api/v1/dashboard/recordings/r1/cancel')
    expect(sent.method).toBe('POST')
    expect(sent.headers.get('X-Dashboard-Request')).toBe('1')
    expect(await screen.findByText(/Cancelled: CAI5_AIS4_S7/)).toBeInTheDocument()
    const after = await rowOf('CAI5_AIS4_S7')
    await waitFor(() => expect(within(after).getByRole('button', { name: /Attach CAI5_AIS4_S7/ })).toBeEnabled())
    expect(within(after).queryByRole('button', { name: /Cancel the attach job/ })).toBeNull()
    expect(within(after).getByText('Pending')).toBeInTheDocument()
  })

  it('keeps the job when the dialog is dismissed, and shows why the server refused', async () => {
    const { calls, rows } = operationsBackend({ cancelError: { status: 409, body: { error: 'Cannot cancel', details: { reason: 'alreadyStarted', message: 'An agent has already taken this job (assigned); it can no longer be cancelled.' } } } })
    rows[0].lastJob = queuedJob() as Recording['lastJob']
    renderPage(<RecordingsPage />, '/recordings')
    const row = await rowOf('CAI5_AIS4_S7')

    await userEvent.click(within(row).getByRole('button', { name: /Cancel the attach job/ }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Keep it' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(calls.some((c) => c.url.endsWith('/cancel'))).toBe(false)

    await userEvent.click(within(row).getByRole('button', { name: /Cancel the attach job/ }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel the job' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('An agent has already taken this job')
  })

  it('is offered in the details drawer too', async () => {
    const { rows } = operationsBackend()
    rows[0].lastJob = queuedJob({ dryRun: true, replaceExisting: false }) as Recording['lastJob']
    renderPage(<RecordingsPage />, '/recordings?recording=r1')
    const drawer = await screen.findByRole('dialog', { name: 'CAI5_AIS4_S7' })
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Cancel job' }))
    expect(await screen.findByRole('dialog', { name: 'Cancel the attach job?' })).toHaveTextContent('a dry run.')
  })
})
