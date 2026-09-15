import type { RosterState } from './types'

// Outside the app (vite dev, a screenshot) the page runs on made-up names so it can be looked at.
const person = (order: number, name: string, aliases: string[] = []) => ({ id: `d${order}-${name}`, order, name, aliases, email: '' })
let state: RosterState = {
  groups: [
    { id: 'CAI5_AIS4_S7', name: 'CAI5_AIS4_S7', mine: true, students: [person(1, 'Sample Student One', ['Sample One']), person(2, 'Sample Student Two'), person(3, 'Sample Student Three')] },
    { id: 'CAI5_AIS4_S8', name: 'CAI5_AIS4_S8', mine: true, students: [] },
  ],
  lmsGroups: ['CAI5_AIS4_S7', 'CAI5_AIS4_S8', 'CAI5_AIS4_S9'],
  account: { label: 'Admin', email: 'admin@example.com' },
  me: { name: 'Demo', role: 'admin' },
  reading: null,
  status: '',
  pages: { attendance: 8, dashboard: 17 },
}
const listeners = new Set<(s: RosterState) => void>()
const publish = () => { for (const l of listeners) l(state) }

export const demoRoster = {
  subscribe(listener: (s: RosterState) => void) { listeners.add(listener); return () => { listeners.delete(listener) } },
  async call<T>(method: string, params: Record<string, unknown>): Promise<T> {
    const wait = (ms: number) => new Promise((r) => setTimeout(r, ms))
    if (method === 'state') return state as T
    if (method === 'lmsRoster') {
      const group = String(params.group)
      state = { ...state, reading: group }; publish()
      await wait(2200)
      const names = ['Sample Student One', 'Sample Student Two', 'Sample Student Three', 'Sample Student Four']
      const existing = state.groups.find((g) => g.id === group)
      const students = names.map((n, i) => existing?.students.find((s) => s.name === n) ?? person(i + 1, n))
      state = { ...state, reading: null, groups: existing ? state.groups.map((g) => (g.id === group ? { ...g, students } : g)) : [...state.groups, { id: group, name: group, mine: true, students }] }
      publish()
      return { ok: true, message: `Demo: ${names.length} students read from the LMS for ${group}.`, group } as T
    }
    await wait(300)
    return { ok: true, message: `Demo: ${method}.` } as T
  },
}
