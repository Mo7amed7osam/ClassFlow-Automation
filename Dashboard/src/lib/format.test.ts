import { describe, expect, it } from 'vitest'
import { formatDateTime, formatSessionDate, lmsLabel, sourceLabel, timeAgo } from './format'

describe('format', () => {
  const now = Date.parse('2026-09-13T15:00:00Z')

  it('says how long ago something happened', () => {
    expect(timeAgo('2026-09-13T14:59:40Z', now)).toBe('just now')
    expect(timeAgo('2026-09-13T14:57:00Z', now)).toBe('3 min ago')
    expect(timeAgo('2026-09-13T12:00:00Z', now)).toBe('3 h ago')
    expect(timeAgo('2026-09-12T15:00:00Z', now)).toBe('1 day ago')
    expect(timeAgo('2026-09-09T15:00:00Z', now)).toBe('4 days ago')
    expect(timeAgo(null, now)).toBe('never')
  })

  it('shows times in Cairo time', () => {
    // 14:19 UTC is 17:19 in Cairo (UTC+3 in September).
    expect(formatDateTime('2026-09-13T14:19:07Z')).toContain('17:19')
    expect(formatDateTime(null)).toBe('—')
  })

  it('never shifts a session date across midnight', () => {
    expect(formatSessionDate('2026-09-11')).toMatch(/Fri.*11.*Sep.*2026/)
    expect(formatSessionDate(null)).toBe('—')
  })

  it('names sources and LMS statuses', () => {
    expect(sourceLabel('drive')).toBe('Drive')
    expect(sourceLabel('zoom')).toBe('Zoom')
    expect(lmsLabel('attached')).toBe('On LMS')
    expect(lmsLabel('something-new')).toBe('something-new')
  })
})
