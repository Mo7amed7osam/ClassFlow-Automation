import type { DashState } from './types'

// Sample data for looking at the Dashboard outside the app. Nothing here is real.
export function demoDash(): DashState {
  const today = new Date().toISOString().slice(0, 10)
  // "?coordinator" shows a coordinator's PC (connected to the server) with an update waiting.
  const coordinator = typeof location !== 'undefined' && location.search.includes('coordinator')
  return {
    server: coordinator
      ? { up: true, text: 'Connected to the server', detail: 'mohab-pc', clientMode: true }
      : { up: true, text: 'Running on this PC', detail: 'Port 8780', clientMode: false },
    ...(coordinator ? { update: { current: '1.26.916.2244', version: '1.26.916.2349', sizeMb: 169, status: '', progress: null, working: false } } : {}),
    status: '', busy: false, savedLogin: true,
    known: [{ username: 'admin', displayName: 'Admin', role: 'admin', hasSession: true, lastUsed: 'Tue 15 Sep' }, { username: 'sara.c', displayName: 'Sara', role: 'coordinator', hasSession: false, lastUsed: 'Sun 13 Sep' }],
    me: coordinator
      ? { username: 'sara.c', displayName: 'Sara', role: 'coordinator', allGroups: false, groups: ['CAI5_AIS4_S8'] }
      : { username: 'admin', displayName: 'Admin', role: 'admin', allGroups: true, groups: ['CAI5_AIS4_S7', 'CAI5_AIS4_S8'] },
    lms: {
      active: 'main', onServer: true, canKeepPasswords: true,
      accounts: [
        { id: 'main', label: 'Coordinator', email: 'coordinator@example.com', role: 'coordinator' },
        { id: 'admin', label: 'Admin', email: 'admin@example.com', role: 'admin' },
      ],
    },
    sessions: {
      running: 0, today: 1, attention: 1, drivePending: 2, done: 6, total: 14, lastRead: 'Mon 22:05', status: '',
      next: { group: 'CAI5_AIS4_S7', title: 'Week 10 - Session 2', date: today, start: '19:00' },
      attentionRows: [{ group: 'CAI5_AIS4_S7', title: 'Week 9 - Session 3', date: today, start: '19:00', next: 'Add the record link' }],
    },
    recordings: { total: 46, onLms: 40, pending: 3, drive: 38, zoomOnly: 3, missing: 5 },
    users: [
      { id: '1', username: 'admin', displayName: 'Admin', role: 'admin', status: 'active', lastLogin: 'Mon 21:00', groups: [] },
      { id: '2', username: 'sara.c', displayName: 'Sara', role: 'coordinator', status: 'active', lastLogin: 'Sun 18:40', groups: [{ id: 'g7', name: 'CAI5_AIS4_S7' }] },
      { id: '3', username: 'omar.c', displayName: 'Omar', role: 'coordinator', status: 'pending', lastLogin: null, groups: [] },
    ],
    groups: [{ id: 'g7', name: 'CAI5_AIS4_S7' }, { id: 'g8', name: 'CAI5_AIS4_S8' }],
    allGroups: [
      { id: 'g7', name: 'CAI5_AIS4_S7', displayName: 'AI S7', archived: false, recordings: 18, lastSession: '2026-09-13', pending: 1, onLms: 16, missing: 1, coordinators: ['Sara'] },
      { id: 'g8', name: 'CAI5_AIS4_S8', displayName: null, archived: false, recordings: 17, lastSession: '2026-09-14', pending: 0, onLms: 16, missing: 1, coordinators: [] },
      { id: 'g1', name: 'AST5DAT1_S1', displayName: null, archived: true, recordings: 11, lastSession: '2026-07-30', pending: 0, onLms: 11, missing: 0, coordinators: [] },
    ],
    pages: { sessions: 15, recordings: 16, coordinators: 5, server: 14 },
  }
}
