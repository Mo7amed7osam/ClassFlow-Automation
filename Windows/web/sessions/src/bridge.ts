import type { Result, State } from './types'
import { demoState } from './demo'

// The page talks to the app through WebView2: { id, method, params } out, { id, result | error }
// back, and { push } messages whenever something changed. Outside the app (vite dev, a screenshot)
// it runs on demo data so the page can be looked at without touching anything real.

interface WebView {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void
}

const webview: WebView | undefined = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview
export const inApp = Boolean(webview)

type Pending = { resolve: (value: unknown) => void; reject: (error: Error) => void }
const pending = new Map<number, Pending>()
let nextId = 1
const stateListeners = new Set<(state: never) => void>()

if (webview) {
  webview.addEventListener('message', (event) => {
    const message = event.data as { id?: number; result?: unknown; error?: string; push?: string; state?: never; dark?: boolean }
    if (message?.id && pending.has(message.id)) {
      const waiting = pending.get(message.id)!
      pending.delete(message.id)
      if (message.error) waiting.reject(new Error(message.error))
      else waiting.resolve(message.result)
    } else if (message?.push === 'state' && message.state) {
      for (const listener of stateListeners) listener(message.state)
    } else if (message?.push === 'theme') {
      document.documentElement.dataset.theme = message.dark ? 'dark' : 'light'
    }
  })
}

/** LMS work can take minutes (sign-in, pages, a whole sheet), so the wait is long. */
export function call<T>(method: string, params: Record<string, unknown> = {}, timeoutMs = 15 * 60_000): Promise<T> {
  if (!webview) return demoCall<T>(method, params)
  return new Promise<T>((resolve, reject) => {
    const id = nextId++
    pending.set(id, { resolve: resolve as (value: unknown) => void, reject })
    webview.postMessage({ id, method, params })
    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id)
        reject(new Error('The app did not answer.'))
      }
    }, timeoutMs)
  })
}

/** The app pushes the page's state whenever it changes; each page knows its own shape. */
export function onState<T = State>(listener: (state: T) => void): () => void {
  const wrapped = listener as unknown as (state: never) => void
  stateListeners.add(wrapped)
  return () => stateListeners.delete(wrapped)
}

export const api = {
  ready: () => call<boolean>('ready'),
  state: () => call<State>('state'),
  refresh: () => call<boolean>('refresh'),
  check: (full: boolean, from?: string, to?: string) => call<Result>('check', { full, from: from ?? '', to: to ?? '' }),
  step: (group: string, date: string, start: string, step: string, link?: string) => call<Result>('step', { group, date, start, step, link: link ?? '' }),
  sheetAll: () => call<Result>('sheetAll'),
  saveSheet: (url: string, tabs: Record<string, string>) => call<Result>('saveSheet', { url, tabs }),
  open: (url: string) => call<boolean>('open', { url }),
  materialFolder: (group: string, date: string, start: string, clear = false) => call<Result>('materialFolder', { group, date, start, clear }),
  trackFolder: (track: string) => call<Result>('trackFolder', { track }),
  setAssignment: (group: string, date: string, start: string, title: string, deadline: string, none: boolean, description = '', file = '') =>
    call<Result>('setAssignment', { group, date, start, title, deadline, none, description, file }),
  assignmentFile: (group: string, date: string) => call<{ ok: boolean; path: string; name: string }>('assignmentFile', { group, date }),
  useAccount: (id: string) => call<Result>('useAccount', { id }),
  removeAccount: (id: string) => call<Result>('removeAccount', { id }),
  saveAccount: (label: string, email: string, role: string, password: string, makeActive: boolean) =>
    call<Result>('saveAccount', { label, email, role, password, makeActive }),
}

// ------------------------------------------------------------------ demo (outside the app only)
let demo = demoState()
function demoCall<T>(method: string, params: Record<string, unknown>): Promise<T> {
  const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))
  const answer = async (): Promise<unknown> => {
    switch (method) {
      case 'ready':
      case 'refresh':
      case 'open':
        return true
      case 'state':
        return demo
      case 'step': {
        const key = `${params.group}|${params.date}|${params.start}|${params.step}`
        demo = { ...demo, working: [...demo.working, key] }
        for (const listener of stateListeners) listener(demo as never)
        await wait(1600)
        demo = { ...demo, working: demo.working.filter((k) => k !== key) }
        for (const listener of stateListeners) listener(demo as never)
        return { ok: true, message: `Demo: ${params.step} for ${params.group} would run on the LMS here.` }
      }
      default:
        await wait(600)
        return { ok: true, message: `Demo: ${method}.` }
    }
  }
  return answer() as Promise<T>
}
