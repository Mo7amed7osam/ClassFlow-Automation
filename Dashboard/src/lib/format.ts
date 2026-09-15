// Times are shown in Cairo time, where the sessions happen. A session's own date and start time
// are shown exactly as stored - they are already local and must not be shifted.

export const CAIRO = 'Africa/Cairo'

const dateTime = new Intl.DateTimeFormat('en-GB', {
  timeZone: CAIRO,
  day: '2-digit',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
})

const sessionDate = new Intl.DateTimeFormat('en-GB', {
  timeZone: 'UTC',
  weekday: 'short',
  day: '2-digit',
  month: 'short',
  year: 'numeric',
})

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const when = new Date(iso)
  return Number.isNaN(when.getTime()) ? '—' : dateTime.format(when)
}

/** "2026-09-11" -> "Fri, 11 Sept 2026", with no time-zone shift. */
export function formatSessionDate(day: string | null | undefined): string {
  if (!day || !/^\d{4}-\d{2}-\d{2}$/.test(day)) return day ?? '—'
  return sessionDate.format(new Date(`${day}T00:00:00Z`))
}

export function timeAgo(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return 'never'
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return '—'
  const seconds = Math.round((now - then) / 1000)
  if (seconds < 0) return 'just now'
  if (seconds < 45) return 'just now'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} h ago`
  const days = Math.round(hours / 24)
  return days === 1 ? '1 day ago' : `${days} days ago`
}

export function sourceLabel(source: string): string {
  if (source === 'drive') return 'Drive'
  if (source === 'zoom') return 'Zoom'
  return source
}

const FIELD_LABELS: Record<string, string> = {
  group: 'group', date: 'date', startTime: 'start time', fileName: 'file name', type: 'type',
  link: 'link', driveLink: 'Drive link', zoomLink: 'Zoom link', lmsStatus: 'LMS status',
}

/** The API's field names in words: "fileName" -> "file name". */
export function fieldLabel(name: string): string {
  return FIELD_LABELS[name] ?? name
}

export function lmsLabel(status: string): string {
  return ({ pending: 'Pending', attached: 'On LMS', failed: 'LMS failed' } as Record<string, string>)[status] ?? status
}
