import type { Row, State, Step } from './types'

// Sample data for looking at the page outside the app. Nothing here is real.
const s = (key: Step['key'], label: string, state: Step['state'], text: string, detail?: string): Step => ({ key, label, state, text, detail })

function row(group: string, date: string, start: string, title: string, tone: Row['tone'], next: string, linkKind: string, steps: Step[], lmsStatus = 'finished'): Row {
  return {
    key: `${group}|${date}|${start}`, group, date, start, title, tone, next, zoom: 'Opened', lmsStatus, lmsReadAt: 'Mon 22:05',
    lmsUrl: 'https://example.com/session', linkKind, recordLink: null, steps, details: '',
  }
}

export function demoState(): State {
  const today = new Date()
  const iso = (offset: number) => new Date(today.getTime() + offset * 86_400_000).toISOString().slice(0, 10)
  const rows: Row[] = [
    row('CAI5_AIS4_S8', iso(0), '19:00', 'Week 8 - Session 2', 'warn', 'Waiting for the Drive link', 'zoom', [
      s('zoom', 'Zoom', 'done', 'Opened'), s('run', 'Run', 'done', 'Done 18:46'), s('attendance', 'Attendance', 'done', 'Done 20:30', '19 joined, 6 not joined'),
      s('correct', 'Late joiners', 'done', 'Done 22:00', '3 rows changed'), s('complete', 'Complete', 'done', 'Done 22:01'), s('record', 'Zoom recording', 'done', 'Done 22:27'), s('drive', 'Drive', 'due', 'Waiting for Drive'),
    ]),
    row('CAI5_AIS4_S8', iso(2), '19:00', 'Week 8 - Session 3', 'future', 'Opens 18:45', '', [
      s('zoom', 'Zoom', 'future', 'Opens 18:45'), s('run', 'Run', 'future', 'At start'), s('attendance', 'Attendance', 'future', 'Later'),
      s('correct', 'Late joiners', 'future', 'Later'), s('complete', 'Complete', 'future', 'Later'), s('record', 'Zoom recording', 'future', 'After class'), s('drive', 'Drive', 'future', 'After class'),
    ], 'pending'),
    row('CAI5_AIS4_S7', iso(1), '19:00', 'Week 10 - Session 2', 'future', 'Opens 18:45', '', [
      s('zoom', 'Zoom', 'future', 'Opens 18:45'), s('run', 'Run', 'future', 'At start'), s('attendance', 'Attendance', 'future', 'Later'),
      s('correct', 'Late joiners', 'future', 'Later'), s('complete', 'Complete', 'future', 'Later'), s('record', 'Zoom recording', 'future', 'After class'), s('drive', 'Drive', 'future', 'After class'),
    ], 'pending'),
    row('CAI5_AIS4_S7', iso(-1), '19:00', 'Week 9 - Session 3', 'bad', 'Add the record link', 'none', [
      s('zoom', 'Zoom', 'done', 'Opened'), s('run', 'Run', 'lms', 'On the LMS'), s('attendance', 'Attendance', 'lms', 'On the LMS'),
      s('correct', 'Late joiners', 'none', 'Not done'), s('complete', 'Complete', 'lms', 'On the LMS'), s('record', 'Zoom recording', 'failed', 'No link'), s('drive', 'Drive', 'due', 'Not in sheet yet'),
    ]),
    row('CAI5_AIS4_S8', iso(-5), '19:00', 'Week 7 - Session 3', 'warn', 'Waiting for the Drive link', 'zoom', [
      s('zoom', 'Zoom', 'failed', 'Did not open'), s('run', 'Run', 'lms', 'On the LMS'), s('attendance', 'Attendance', 'lms', 'On the LMS'),
      s('correct', 'Late joiners', 'none', 'Not done'), s('complete', 'Complete', 'lms', 'On the LMS'), s('record', 'Zoom recording', 'done', 'Done 22:27'), s('drive', 'Drive', 'due', 'Waiting for Drive'),
    ]),
    row('CAI5_AIS4_S7', iso(-3), '14:00', 'Week 9 - Session 1', 'done', 'Done', 'drive', [
      s('zoom', 'Zoom', 'done', 'Opened'), s('run', 'Run', 'done', 'Done 13:46'), s('attendance', 'Attendance', 'done', 'Done 15:30'),
      s('correct', 'Late joiners', 'done', 'Done 17:00'), s('complete', 'Complete', 'done', 'Done 17:01'), s('record', 'Zoom recording', 'lms', 'Drive instead'), s('drive', 'Drive', 'done', 'Done 01:12'),
    ]),
    row('CAI5_AIS4_S8', iso(-7), '19:00', 'Week 7 - Session 1', 'bad', 'Attendance Retry 20:45 (3)', '', [
      s('zoom', 'Zoom', 'done', 'Opened'), s('run', 'Run', 'done', 'Done 18:47'), s('attendance', 'Attendance', 'retry', 'Retry 20:45 · 3×', 'The session page offers no Take Session Attendance.'),
      s('correct', 'Late joiners', 'none', 'Not done'), s('complete', 'Complete', 'none', 'Not done'), s('record', 'Zoom recording', 'none', 'Not done'), s('drive', 'Drive', 'future', 'After class'),
    ], 'running'),
  ]
  return {
    now: today.toISOString(), status: 'Demo data — outside the app nothing is real.', busy: false, lastRead: 'Mon 22:05',
    stats: { running: 1, today: 1, attention: 2, drivePending: 2, done: 1 },
    account: 'Coordinator — coordinator@example.com (coordinator)',
    accounts: [
      { id: 'main', label: 'Coordinator', email: 'coordinator@example.com', role: 'coordinator', active: true },
      { id: 'admin', label: 'Admin', email: 'admin@example.com', role: 'admin', active: false },
    ],
    sheet: { url: '', tabs: {}, groups: ['CAI5_AIS4_S7', 'CAI5_AIS4_S8'] },
    working: [],
    rows,
  }
}
