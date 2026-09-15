import { describe, expect, it } from 'vitest'
import { fakeBackend } from '../test/helpers'
import { api, ApiError, query, UnauthorizedError } from './client'

describe('api client', () => {
  it('reads with the session cookie and no API key', async () => {
    const calls = fakeBackend(() => ({ body: { ok: true } }))
    await api('/api/v1/dashboard/overview')
    expect(calls[0].credentials).toBe('same-origin')
    expect(calls[0].headers.get('X-Dashboard-Request')).toBeNull()
    expect(calls[0].headers.get('X-API-Key')).toBeNull()
  })

  it('marks every write with the dashboard header', async () => {
    const calls = fakeBackend(() => ({ body: { username: 'admin' } }))
    await api('/api/v1/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'x' }) })
    expect(calls[0].headers.get('X-Dashboard-Request')).toBe('1')
    expect(calls[0].headers.get('Content-Type')).toBe('application/json')
  })

  it('turns a 401 into UnauthorizedError, other failures into ApiError', async () => {
    fakeBackend(() => ({ status: 401, body: { error: 'Unauthorized' } }))
    await expect(api('/api/v1/dashboard/agents')).rejects.toBeInstanceOf(UnauthorizedError)

    fakeBackend(() => ({ status: 400, body: { error: 'Invalid request', details: "'date' must be yyyy-MM-dd." } }))
    const error = (await api('/api/v1/dashboard/recordings').catch((e: unknown) => e)) as ApiError
    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(400)
    expect(error.details).toContain('yyyy-MM-dd')
  })

  it('leaves empty filters out of the query string', () => {
    expect(query({ group: 'CAI5_AIS4_S7', date: '', status: undefined, page: 2 })).toBe('?group=CAI5_AIS4_S7&page=2')
    expect(query({ group: '' })).toBe('')
  })
})
