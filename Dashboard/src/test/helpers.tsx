import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import type { ReactElement } from 'react'
import { MemoryRouter } from 'react-router'
import { vi } from 'vitest'
import { ToastProvider } from '../components/Toast'

export interface Call {
  url: string
  method: string
  headers: Headers
  body: string | undefined
  credentials: RequestCredentials | undefined
}

type Handler = (call: Call) => { status?: number; body: unknown } | undefined

/** A fake backend: each request goes to the first handler that answers it; the calls are kept. */
export function fakeBackend(...handlers: Handler[]) {
  const calls: Call[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const call: Call = {
      url: String(input),
      method: init.method ?? 'GET',
      headers: new Headers(init.headers),
      body: typeof init.body === 'string' ? init.body : undefined,
      credentials: init.credentials,
    }
    calls.push(call)
    for (const handler of handlers) {
      const answer = handler(call)
      if (answer) {
        return new Response(JSON.stringify(answer.body), {
          status: answer.status ?? 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
    }
    return new Response(JSON.stringify({ error: 'Not found' }), { status: 404 })
  })
  vi.stubGlobal('fetch', fetchMock)
  return calls
}

export function renderPage(ui: ReactElement, route = '/') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[route]}>
        <ToastProvider>{ui}</ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

export const recording = (overrides: Record<string, unknown> = {}) => ({
  id: 'r1',
  group: 'CAI5_AIS4_S7',
  date: '2026-09-11',
  startTime: '18:00',
  fileName: 'session.mp4',
  type: 'recording',
  driveLink: 'https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view',
  zoomLink: null,
  source: 'drive',
  lmsStatus: 'pending',
  lmsUpdatedAt: null,
  createdAt: '2026-09-13T14:19:07Z',
  updatedAt: '2026-09-13T14:19:07Z',
  linkStatus: { link: 'drive', lms: 'pending', label: 'Drive · pending' },
  ...overrides,
})

/** GET /api/v1/auth/me for the admin and for a coordinator. */
export const adminMe = { id: 'u-admin', username: 'admin', displayName: 'The Admin', role: 'admin', allGroups: true, groups: null, expiresAt: '2026-09-13T23:00:00Z' }
export const coordinatorMe = (groups: string[] = ['CAI5_AIS4_S7']) => ({
  id: 'u-omar',
  username: 'omar',
  displayName: 'Omar',
  role: 'coordinator',
  allGroups: false,
  groups: groups.map((name, i) => ({ id: `g${i + 1}`, name, displayName: null, archived: false })),
  expiresAt: '2026-09-13T23:00:00Z',
})

/** A handler answering /api/v1/auth/me with this user (or 401 for null). */
export const signedInAs = (me: unknown) => (call: Call) =>
  call.url === '/api/v1/auth/me' ? (me ? { body: me } : { status: 401, body: { error: 'Unauthorized' } }) : undefined

export const groupSummary = (overrides: Record<string, unknown> = {}) => ({
  id: 'g1',
  group: 'CAI5_AIS4_S7',
  displayName: null,
  archived: false,
  recordings: 2,
  lastSessionDate: '2026-09-11',
  lastUpdatedAt: '2026-09-13T14:19:07Z',
  pending: 1,
  onLms: 1,
  missingLink: 0,
  ...overrides,
})
