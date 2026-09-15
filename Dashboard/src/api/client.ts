// The only way the dashboard talks to the backend: same origin, the session cookie (HttpOnly -
// this code never sees it), and the X-Dashboard-Request header on anything that is not a GET.
// No API key ever reaches the browser.

export class ApiError extends Error {
  readonly status: number
  readonly details: unknown

  constructor(status: number, message: string, details?: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.details = details
  }
}

/** The session is missing or over: the app sends the user to the login page. */
export class UnauthorizedError extends ApiError {
  constructor() {
    super(401, 'Your session has ended. Please sign in again.')
    this.name = 'UnauthorizedError'
  }
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const method = (init.method ?? 'GET').toUpperCase()
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (method !== 'GET') {
    headers.set('X-Dashboard-Request', '1')
    if (init.body !== undefined) headers.set('Content-Type', 'application/json')
  }

  let response: Response
  try {
    response = await fetch(path, { ...init, method, headers, credentials: 'same-origin', cache: 'no-store' })
  } catch {
    throw new ApiError(0, 'The backend cannot be reached.')
  }

  // A wrong password is a 401 too, but it is an answer for the login form, not a lost session.
  if (response.status === 401 && !path.endsWith('/auth/login')) throw new UnauthorizedError()
  const body = await response.json().catch(() => null)
  if (!response.ok) {
    const error = (body as { error?: string; details?: unknown } | null) ?? {}
    throw new ApiError(response.status, error.error ?? `Request failed (${response.status})`, error.details)
  }
  return body as T
}

export function query(params: Record<string, string | number | undefined | null>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value))
  }
  const text = search.toString()
  return text ? `?${text}` : ''
}
