import type { WelcomeState } from './types'

// Outside the app (vite dev, a screenshot) the steps run on made-up data; nothing real is touched.

let state: WelcomeState = {
  server: 'https://central.example.net/',
  busy: false,
  status: '',
  me: null,
  lms: { onServer: true, accounts: [] },
  zoom: [],
  ai: { model: 'openai/gpt-4.1-mini', models: ['openai/gpt-4.1-mini', 'openai/gpt-5.4-mini', 'openai/gpt-4.1'], ready: false, status: '' },
  pages: { dashboard: 17, schedules: 3, sessions: 15 },
}
const listeners = new Set<(s: WelcomeState) => void>()
const publish = (next: WelcomeState) => { state = next; for (const l of listeners) l(state) }
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

export const demoWelcome = {
  subscribe(listener: (s: WelcomeState) => void) { listeners.add(listener); return () => listeners.delete(listener) },
  async call<T>(method: string, p: Record<string, unknown>): Promise<T> {
    const answer = async (): Promise<unknown> => {
      switch (method) {
        case 'ready': return true
        case 'state': return state
        case 'signIn':
          await wait(700)
          publish({ ...state, me: { username: String(p.username), displayName: 'Demo Coordinator', role: 'coordinator', allGroups: false, groups: ['CAI5_AIS4_S7', 'CAI5_AIS4_S8'] } })
          return { ok: true, message: `Signed in as Demo Coordinator (${p.username}, coordinator).` }
        case 'signOut': publish({ ...state, me: null }); return { ok: true, message: 'Signed out.' }
        case 'saveLms':
          await wait(700)
          publish({ ...state, lms: { ...state.lms, accounts: [...state.lms.accounts, { id: String(Date.now()), label: String(p.label || 'Demo Coordinator'), email: String(p.email), active: true }] } })
          return { ok: true, message: 'Saved (the password encrypted on the server).' }
        case 'saveZoom': {
          await wait(500)
          const group = String(p.group)
          const rest = state.zoom.filter((z) => z.group !== group)
          publish({ ...state, zoom: [...rest, { id: group, group, displayName: String(p.displayName || group), email: String(p.email), link: String(p.link), hasPassword: Boolean(p.password) }] })
          return { ok: true, message: `${group}: saved.` }
        }
        case 'testAi':
          await wait(900)
          publish({ ...state, ai: { ...state.ai, model: String(p.model), ready: true } })
          return { ok: true, message: `Done — Valid. OpenRouter / ${p.model} answered. Key saved securely.` }
        default: await wait(300); return { ok: true, message: `Demo: ${method}.` }
      }
    }
    return answer() as Promise<T>
  },
}
