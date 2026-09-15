import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router'
import { describe, expect, it } from 'vitest'
import type { AttendanceRecordView, ParticipantView, SessionDetails } from '../api/attendance'
import { fakeBackend, groupSummary, renderPage, type Call } from '../test/helpers'
import { AttendancePage } from './AttendancePage'
import { AttendanceSessionPage, minutes } from './AttendanceSessionPage'
import { StudentsPage } from './StudentsPage'

const SID = 's-1'
const groups = { body: { groups: [groupSummary()], count: 1 } }

const record = (overrides: Partial<AttendanceRecordView>): AttendanceRecordView => ({
  studentId: 'st-1', fullName: 'Mohab Osama Sayed', email: null, order: 1, active: true, status: 'absent', confidence: 0,
  source: 'none', reason: null, manual: false, participant: null, extraNames: [], joinTime: null, leaveTime: null, durationSeconds: null,
  ...overrides,
})
const participant = (overrides: Partial<ParticipantView>): ParticipantView => ({
  id: 'p-1', name: 'Mohab', firstSeenAt: '2026-09-14T15:00:00Z', lastSeenAt: '2026-09-14T16:00:00Z', sightings: 4,
  presentSeconds: 3600, ignored: false, assignedTo: null, candidates: [], ...overrides,
})

function details(aiAvailable = false): SessionDetails {
  return {
    session: { id: SID, group: 'CAI5_AIS4_S7', date: '2026-09-14', startTime: '18:00', title: null, source: 'agent', status: 'open',
      recordingId: null, startedAt: null, endedAt: null, matchedAt: null, finalizedAt: null, createdAt: '', updatedAt: '' },
    records: [
      record({ studentId: 'st-1', fullName: 'Mohab Osama Sayed', status: 'present', confidence: 100, source: 'exact',
        participant: { id: 'p-1', name: 'Mohab Osama Sayed' }, joinTime: '2026-09-14T15:00:00Z', leaveTime: '2026-09-14T16:30:00Z', durationSeconds: 5400 }),
      record({ studentId: 'st-2', fullName: 'Ahmed Ali Hassan', order: 2, status: 'needs_review', confidence: 60, source: 'fuzzy',
        reason: "Only 'Ahmed' was seen", participant: { id: 'p-2', name: 'Ahmed' } }),
      record({ studentId: 'st-3', fullName: 'Sara Mahmoud Adel', order: 3 }),
    ],
    participants: [
      participant({ id: 'p-1', name: 'Mohab Osama Sayed', assignedTo: 'st-1' }),
      participant({ id: 'p-2', name: 'Ahmed', assignedTo: 'st-2', candidates: [{ studentId: 'st-2', score: 60 }] }),
      participant({ id: 'p-3', name: 'sara m', candidates: [{ studentId: 'st-3', score: 72 }] }),
      participant({ id: 'p-4', name: 'Tech support' }),
    ],
    snapshots: { count: 4, lastCapturedAt: '2026-09-14T16:00:00Z' },
    summary: { present: 1, needsReview: 1, absent: 1, students: 3, unmatched: 2 },
    aiAvailable,
  }
}

/** A session backend: every change answers with the whole session, as the real one does. */
function sessionBackend(options: { ai?: boolean } = {}) {
  const d = details(options.ai)
  const recount = () => {
    const owned = new Set(d.records.flatMap((r) => (r.participant ? [r.participant.id] : [])))
    for (const p of d.participants) p.assignedTo = d.records.find((r) => r.participant?.id === p.id)?.studentId ?? null
    d.summary = { present: d.records.filter((r) => r.status === 'present').length, needsReview: d.records.filter((r) => r.status === 'needs_review').length,
      absent: d.records.filter((r) => r.status === 'absent').length, students: d.records.length,
      unmatched: d.participants.filter((p) => !p.ignored && !owned.has(p.id)).length }
  }
  const calls = fakeBackend((call: Call) => {
    const base = `/api/v1/dashboard/attendance/sessions/${SID}`
    if (call.method === 'GET' && call.url === base) return { body: structuredClone(d) }
    if (call.method === 'PUT' && call.url.startsWith(`${base}/records/`)) {
      const r = d.records.find((x) => call.url.endsWith(x.studentId))!
      const body = JSON.parse(call.body!)
      if (body.participantId) {
        for (const other of d.records) if (other.participant?.id === body.participantId) { other.participant = null; other.status = 'absent' }
        r.participant = { id: body.participantId, name: d.participants.find((p) => p.id === body.participantId)!.name }
        r.status = 'present'
      } else if (body.status) {
        r.status = body.status
        if (body.status === 'absent') r.participant = null
      }
      r.manual = true
      r.source = 'manual'
      recount()
      return { body: structuredClone(d) }
    }
    if (call.method === 'POST' && call.url.endsWith('/ignore')) {
      d.participants.find((p) => call.url.includes(`/participants/${p.id}/`))!.ignored = JSON.parse(call.body!).ignored
      recount()
      return { body: structuredClone(d) }
    }
    if (call.method === 'POST' && call.url === `${base}/finalize`) { d.session.status = 'finalized'; return { body: structuredClone(d) } }
    if (call.method === 'POST' && call.url === `${base}/reopen`) { d.session.status = 'closed'; return { body: structuredClone(d) } }
    if (call.method === 'POST' && call.url === `${base}/participants`) {
      for (const name of JSON.parse(call.body!).names) d.participants.push(participant({ id: `p-${d.participants.length + 1}`, name }))
      recount()
      return { status: 201, body: structuredClone(d) }
    }
    if (call.method === 'POST' && call.url === `${base}/match`) {
      return { body: { ...structuredClone(d), aiSuggestions: JSON.parse(call.body!).useAi
        ? [{ studentId: 'st-3', participantId: 'p-3', name: 'sara m', confidence: 70, applied: false }] : [] } }
    }
    return undefined
  })
  return { calls, d }
}

const renderSession = () =>
  renderPage(<Routes><Route path="/attendance/:id" element={<AttendanceSessionPage />} /></Routes>, `/attendance/${SID}`)
const rowOf = async (name: string) => (await screen.findByText(name, { selector: 'span.font-medium' })).closest('tr')!
const writes = (calls: Call[]) => calls.filter((c) => c.method !== 'GET')

describe('attendance session page', () => {
  it('shows who attended, how sure the match is and how long they stayed', async () => {
    sessionBackend()
    renderSession()
    const mohab = await rowOf('Mohab Osama Sayed')
    expect(within(mohab).getByText('Present')).toBeInTheDocument()
    expect(within(mohab).getByText(/100%/)).toBeInTheDocument()
    expect(within(mohab).getByText('90 min')).toBeInTheDocument()
    expect(within(await rowOf('Ahmed Ali Hassan')).getByText('Needs review')).toBeInTheDocument()
    expect(within(await rowOf('Sara Mahmoud Adel')).getByText('Absent')).toBeInTheDocument()
    expect(screen.getByText('Unmatched Zoom names').parentElement).toHaveTextContent('2')
    expect(screen.getByRole('link', { name: 'Export CSV' })).toHaveAttribute('href', `/api/v1/dashboard/attendance/sessions/${SID}/export.csv`)
    expect(screen.queryByRole('button', { name: 'Match with AI' })).not.toBeInTheDocument()
  })

  it('confirms a doubtful match in one click, with the dashboard header', async () => {
    const { calls } = sessionBackend()
    renderSession()
    await userEvent.click(await screen.findByRole('button', { name: 'Confirm Ahmed Ali Hassan' }))
    await waitFor(() => expect(within(screen.getByText('Ahmed Ali Hassan', { selector: 'span.font-medium' }).closest('tr')!).getByText('Present')).toBeInTheDocument())
    const put = writes(calls)[0]
    expect(put.url).toBe(`/api/v1/dashboard/attendance/sessions/${SID}/records/st-2`)
    expect(JSON.parse(put.body!)).toEqual({ status: 'present' })
    expect(put.headers.get('X-Dashboard-Request')).toBe('1')
  })

  it('"Not them" marks the student absent', async () => {
    const { calls } = sessionBackend()
    renderSession()
    await userEvent.click(await screen.findByRole('button', { name: 'Not Ahmed Ali Hassan' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(JSON.parse(writes(calls)[0].body!)).toEqual({ status: 'absent' })
  })

  it('gives a student a Zoom name by hand, remembered unless unticked', async () => {
    const { calls } = sessionBackend()
    renderSession()
    await userEvent.click(await screen.findByRole('button', { name: 'Change Sara Mahmoud Adel' }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.selectOptions(within(dialog).getByRole('combobox'), 'p-3')
    await userEvent.click(within(dialog).getByRole('checkbox'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Use this name' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(JSON.parse(writes(calls)[0].body!)).toEqual({ participantId: 'p-3', remember: false })
    expect(within(await rowOf('Sara Mahmoud Adel')).getByText('sara m')).toBeInTheDocument()
  })

  it('the change dialog says when a name already belongs to someone else', async () => {
    sessionBackend()
    renderSession()
    await userEvent.click(await screen.findByRole('button', { name: 'Change Sara Mahmoud Adel' }))
    expect(within(await screen.findByRole('dialog')).getByRole('option', { name: 'Ahmed (now Ahmed Ali Hassan)' })).toBeInTheDocument()
  })

  it('the review tab offers the best student for an unmatched name, and hides non-students', async () => {
    const { calls } = sessionBackend()
    renderSession()
    await userEvent.click(await screen.findByRole('tab', { name: /Review/ }))
    expect(screen.queryByText('Mohab Osama Sayed', { selector: 'span.font-medium' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Give sara m to Sara Mahmoud Adel' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(JSON.parse(writes(calls)[0].body!)).toEqual({ participantId: 'p-3' })
    const support = (await screen.findByText('Tech support')).closest('li')!
    await userEvent.click(within(support).getByRole('button', { name: 'Not a student' }))
    await waitFor(() => expect(screen.queryByText('Tech support')).not.toBeInTheDocument())
    expect(writes(calls)[1].url).toBe(`/api/v1/dashboard/attendance/sessions/${SID}/participants/p-4/ignore`)
    expect(JSON.parse(writes(calls)[1].body!)).toEqual({ ignored: true })
  })

  it('the Zoom names tab lists every name and can include an ignored one again', async () => {
    const { d, calls } = sessionBackend()
    d.participants[3].ignored = true
    renderSession()
    await userEvent.click(await screen.findByRole('tab', { name: /Zoom names/ }))
    const row = (await screen.findByText('Tech support')).closest('tr')!
    await userEvent.click(within(row).getByRole('button', { name: 'Include' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(JSON.parse(writes(calls)[0].body!)).toEqual({ ignored: false })
  })

  it('adds pasted names, one per line', async () => {
    const { calls } = sessionBackend()
    renderSession()
    await userEvent.click(await screen.findByRole('button', { name: 'Add names' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Zoom names' }), 'Omar Khaled{enter}{enter}  Nour  ')
    await userEvent.click(screen.getByRole('button', { name: 'Add 2' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(JSON.parse(writes(calls)[0].body!)).toEqual({ names: ['Omar Khaled', 'Nour'] })
  })

  it('finalizing locks every change until the session is reopened', async () => {
    const { calls } = sessionBackend()
    renderSession()
    await userEvent.click(await screen.findByRole('button', { name: 'Finalize' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Finalize' }))
    expect(await screen.findByText(/Finalized\. Reopen it/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Change Mohab Osama Sayed' })).toBeDisabled()
    expect(screen.getByRole('button', { name: /Match again/ })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Confirm Ahmed Ali Hassan' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reopen' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Reopen' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Change Mohab Osama Sayed' })).toBeEnabled())
    expect(writes(calls).map((c) => c.url.split('/').pop())).toEqual(['finalize', 'reopen'])
  })

  it('AI matching is offered only when the server has it, and its guesses wait for a person', async () => {
    const { calls } = sessionBackend({ ai: true })
    renderSession()
    await userEvent.click(await screen.findByRole('button', { name: 'Match with AI' }))
    const card = (await screen.findByText('AI suggestions to review')).closest('section')!
    expect(card).toHaveTextContent('sara m may be Sara Mahmoud Adel (70%)')
    expect(JSON.parse(writes(calls)[0].body!)).toEqual({ useAi: true })
    await userEvent.click(within(card).getByRole('button', { name: 'Accept' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(2))
    expect(JSON.parse(writes(calls)[1].body!)).toEqual({ participantId: 'p-3' })
  })

  it('formats time in the meeting', () => {
    expect(minutes(null)).toBe('—')
    expect(minutes(89)).toBe('1 min')
    expect(minutes(5400)).toBe('90 min')
  })
})

describe('attendance list', () => {
  it('lists sessions with their counts and creates one by hand', async () => {
    const calls = fakeBackend((call) => {
      if (call.url.startsWith('/api/v1/dashboard/groups')) return groups
      if (call.method === 'POST' && call.url === '/api/v1/dashboard/attendance/sessions') {
        return { status: 201, body: { ...details().session, id: 's-new', ...JSON.parse(call.body!) } }
      }
      if (call.url.startsWith('/api/v1/dashboard/attendance/sessions')) {
        return { body: { items: [{ ...details().session, present: 12, needsReview: 2, absent: 3, lastCapturedAt: null }], total: 1, page: 1, pageSize: 25 } }
      }
      return undefined
    })
    renderPage(<Routes><Route path="/attendance" element={<AttendancePage />} /><Route path="/attendance/:id" element={<p>session page</p>} /></Routes>, '/attendance')
    const row = (await screen.findByText('CAI5_AIS4_S7', { selector: 'td' })).closest('tr')!
    expect(row).toHaveTextContent('Live')
    expect(within(row).getByText('12')).toBeInTheDocument()
    expect(within(row).getByRole('link', { name: 'Open' })).toHaveAttribute('href', `/attendance/${SID}`)

    await userEvent.click(screen.getByRole('button', { name: 'New session' }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText('Title (optional)'), 'Make-up class')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }))
    expect(await screen.findByText('session page')).toBeInTheDocument()
    const body = JSON.parse(calls.find((c) => c.method === 'POST')!.body!)
    expect(body).toMatchObject({ group: 'CAI5_AIS4_S7', title: 'Make-up class' })
  })

  it('filters by group and status through the address', async () => {
    const calls = fakeBackend((call) => {
      if (call.url.startsWith('/api/v1/dashboard/groups')) return groups
      if (call.url.startsWith('/api/v1/dashboard/attendance/sessions')) return { body: { items: [], total: 0, page: 1, pageSize: 25 } }
      return undefined
    })
    renderPage(<AttendancePage />, '/attendance?group=CAI5_AIS4_S7&status=finalized')
    expect(await screen.findByText(/No sessions yet/)).toBeInTheDocument()
    const url = calls.find((c) => c.url.startsWith('/api/v1/dashboard/attendance/sessions'))!.url
    expect(url).toContain('group=CAI5_AIS4_S7')
    expect(url).toContain('status=finalized')
  })
})

describe('students page', () => {
  const student = { id: 'st-1', group: 'CAI5_AIS4_S7', fullName: 'Mohab Osama Sayed', email: 'mohab@example.com', externalId: null, order: 1,
    aliases: ['Mo'], active: true, createdAt: '', updatedAt: '' }

  it('shows the roster of the first group', async () => {
    const calls = fakeBackend((call) => {
      if (call.url.startsWith('/api/v1/dashboard/groups')) return groups
      if (call.url.startsWith('/api/v1/dashboard/students')) return { body: { students: [student], count: 1 } }
      return undefined
    })
    renderPage(<StudentsPage />, '/students')
    expect(await screen.findByText('Mohab Osama Sayed')).toBeInTheDocument()
    expect(screen.getByText('Also: Mo')).toBeInTheDocument()
    expect(calls.some((c) => c.url.startsWith('/api/v1/dashboard/students?') && c.url.includes('group=CAI5_AIS4_S7'))).toBe(true)
  })

  it('imports a pasted roster after a preview', async () => {
    const calls = fakeBackend((call) => {
      if (call.url.startsWith('/api/v1/dashboard/groups')) return groups
      if (call.url === '/api/v1/dashboard/students/import') {
        const body = JSON.parse(call.body!)
        return { body: { group: body.group, dryRun: body.dryRun, created: 2, updated: 0, unchanged: 0, invalidRows: ['Row 4: no name'], duplicateRows: ['Row 5: duplicate'], skipped: ['Row 4: no name', 'Row 5: duplicate'], createdNames: ['A B C', 'D E F'], updatedNames: [] } }
      }
      if (call.url.startsWith('/api/v1/dashboard/students')) return { body: { students: [], count: 0 } }
      return undefined
    })
    renderPage(<StudentsPage />, '/students')
    await userEvent.click(await screen.findByRole('button', { name: 'Import roster' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Roster' }), 'Name{enter}A B C{enter}D E F')
    await userEvent.click(screen.getByRole('button', { name: 'Preview' }))
    expect(await screen.findByText('Row 4: no name')).toBeInTheDocument()
    expect(screen.getByText(/New: A B C, D E F/)).toBeInTheDocument()
    expect(screen.getByText('Invalid rows: 1. Duplicate rows: 1.')).toBeInTheDocument()
    expect(writes(calls)).toHaveLength(1)
    await userEvent.click(screen.getByRole('button', { name: 'Import' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    const imports = calls.filter((c) => c.url === '/api/v1/dashboard/students/import').map((c) => JSON.parse(c.body!))
    expect(imports.map((b) => b.dryRun)).toEqual([true, false])
    expect(imports[1]).toEqual({ group: 'CAI5_AIS4_S7', text: 'Name\nA B C\nD E F', dryRun: false })
  })

  it('adds a student with other names split on commas', async () => {
    const calls = fakeBackend((call) => {
      if (call.url.startsWith('/api/v1/dashboard/groups')) return groups
      if (call.method === 'POST' && call.url === '/api/v1/dashboard/students') return { status: 201, body: student }
      if (call.url.startsWith('/api/v1/dashboard/students')) return { body: { students: [], count: 0 } }
      return undefined
    })
    renderPage(<StudentsPage />, '/students')
    await userEvent.click(await screen.findByRole('button', { name: 'Add student' }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText('Full name'), 'Mohab Osama Sayed')
    await userEvent.type(within(dialog).getByLabelText('Other names (comma-separated)'), 'Mo, محمد أسامة ,')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    const body = JSON.parse(calls.find((c) => c.method === 'POST')!.body!)
    expect(body).toMatchObject({ group: 'CAI5_AIS4_S7', fullName: 'Mohab Osama Sayed', aliases: ['Mo', 'محمد أسامة'] })
  })
})
