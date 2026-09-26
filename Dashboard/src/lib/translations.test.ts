import { describe, expect, it } from 'vitest'
import { formatHumanActivity } from './translations'

describe('formatHumanActivity', () => {
  it('does not describe failed Zoom or LMS activity as successful', () => {
    expect(formatHumanActivity('class.run', 'Zoom sign-in timed out', null, 'failed'))
      .toBe('Zoom meeting run failed: Zoom sign-in timed out')
    expect(formatHumanActivity('lms.run_session', 'session already running', null, 'failed'))
      .toBe('LMS session start failed: session already running')
  })

  it('labels skipped activity and preserves success wording for completed events', () => {
    expect(formatHumanActivity('class.run', 'policy prevented start', null, 'skipped'))
      .toBe('Zoom meeting run skipped: policy prevented start')
    expect(formatHumanActivity('class.run', 'Meeting held', null, 'done'))
      .toBe('Zoom meeting run')
  })
})
