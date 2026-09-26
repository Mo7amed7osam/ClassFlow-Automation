import type { Action, Row, StepKey } from './types'

export const startOf = (row: Row) => new Date(`${row.date}T${row.start}:00`)
const dayKey = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

export type Bucket = 'today' | 'upcoming' | 'earlier'
export function bucketOf(row: Row, now: Date): Bucket {
  if (row.date === dayKey(now)) return 'today'
  return startOf(row) > now ? 'upcoming' : 'earlier'
}

export const weekday = (row: Row) => startOf(row).toLocaleDateString('en-GB', { weekday: 'short' })
export const dayNumber = (row: Row) => startOf(row).getDate()
export const month = (row: Row) => startOf(row).toLocaleDateString('en-GB', { month: 'short' })

/** The last part of a group name (S7, S8) is what people call it. */
export const shortGroup = (group: string) => group.split('_').pop() ?? group

/** Each group keeps one colour everywhere on the page. */
export function groupHue(group: string): number {
  let hash = 0
  for (const c of group) hash = (hash * 31 + c.charCodeAt(0)) >>> 0
  const hues = [262, 190, 330, 24, 150, 210]
  return hues[hash % hues.length]
}

export function relative(target: Date, now: Date): string {
  const minutes = Math.round((target.getTime() - now.getTime()) / 60_000)
  const abs = Math.abs(minutes)
  const text = abs < 60 ? `${abs} min` : abs < 48 * 60 ? `${Math.floor(abs / 60)} h ${abs % 60 ? `${abs % 60} min` : ''}`.trim() : `${Math.round(abs / 1440)} days`
  return minutes >= 0 ? `in ${text}` : `${text} ago`
}

const stepOf = (row: Row, key: StepKey) => row.steps.find((s) => s.key === key)
const isDone = (row: Row, key: StepKey) => ['done', 'lms'].includes(stepOf(row, key)?.state ?? '')

export interface ActionInfo {
  action: Action
  label: string
  /** What it will do on the LMS, for the confirmation. */
  explain: string
  changesLms: boolean
}

export const ACTIONS: Record<Action, ActionInfo> = {
  noRecording: {
    action: 'noRecording', label: 'No Zoom recording', changesLms: false,
    explain: 'This physical session was not recorded on Zoom: its recording is not looked for again and is not shown as failed.',
  },
  inRoom: {
    action: 'inRoom', label: 'Held in the room', changesLms: false,
    explain: 'Mark this class as a physical session: no Zoom meeting is opened and no attendance is taken from Zoom. It is still run and completed on the LMS, and its recording goes up.',
  },
  onZoom: {
    action: 'onZoom', label: 'On Zoom after all', changesLms: false,
    explain: 'Mark this class as online, whatever the LMS says: its meeting opens on Zoom and attendance is taken from it.',
  },
  zoomAgain: {
    action: 'zoomAgain', label: 'Start the meeting again', changesLms: false,
    explain: 'Stop what this PC is running for this class and open its meeting afresh - for when the meeting dropped but the app still shows it as running.',
  },
  yesThem: {
    action: 'yesThem', label: 'Yes, that is them', changesLms: false,
    explain: 'Count this student as present in this class. It goes to the LMS with the next upload, not now.',
  },
  notThem: {
    action: 'notThem', label: 'No, not them', changesLms: false,
    explain: 'Leave this student out of this class. It goes to the LMS with the next upload, not now.',
  },
  zoom: {
    action: 'zoom', label: 'Open Zoom now', changesLms: false,
    explain: 'Open this class’s Zoom meeting on this PC now, with its own account and link. A class whose meeting is already running here is left alone.',
  },
  run: { action: 'run', label: 'Run Session', explain: 'Press "Run Session" for this class on the LMS.', changesLms: true },
  attendance: { action: 'attendance', label: 'Take attendance', explain: 'Fill in Take Session Attendance with who the Attendance page shows as present. If the sheet is already there, it is checked against this class instead.', changesLms: true },
  correct: { action: 'correct', label: 'Correct late joiners', explain: 'Move whoever joined late from Not-joined to Joined on the LMS attendance, from what this PC saw of the meeting.', changesLms: true },
  report: { action: 'report', label: 'Apply Zoom report', explain: "Read Zoom's own participants report for this meeting (it exists only after the meeting ended), add anyone the snapshots missed to the LMS attendance, and name whoever stayed under an hour.", changesLms: true },
  complete: { action: 'complete', label: 'Complete session', explain: 'Press "Complete Session" on the LMS. This cannot be undone there.', changesLms: true },
  zoomRecording: { action: 'zoomRecording', label: 'Get Zoom recording', explain: "Copy the class's Zoom cloud recording link (even while Zoom is still processing it) and add it as the record link. A link already on the session is kept.", changesLms: true },
  sheet: { action: 'sheet', label: 'Check sheet for Drive', explain: "Look this class up in the recordings sheet and put its Google Drive link on the LMS, replacing a Zoom link.", changesLms: true },
  recording: { action: 'recording', label: 'Get the recording', explain: 'Put the recording on the LMS: its Google Drive link when n8n / the sheet has it (that always wins), otherwise its Zoom recording, which the Drive link replaces later.', changesLms: true },
  link: { action: 'link', label: 'Paste a link…', explain: 'Put a Google Drive or Zoom recording link you paste on this session. A Drive link replaces what is there.', changesLms: true },
  material: { action: 'material', label: 'Upload material', explain: "Add each of this class's files to its LMS session (Add Attachment: the file's name as the title, type File), and create its assignment when it has one with a deadline. Files the session already shows are skipped.", changesLms: true },
  assignment: { action: 'assignment', label: 'Create the assignment', explain: 'Create the assignment on this LMS session (Add Assignment) with the deadline shown, after adding any material that is still missing.', changesLms: true },
}

/** Material can go up whenever it is ready, before or after the class. */
export const hasMaterial = (row: Row) => (row.material?.files.length ?? 0) > 0

/** The one thing this class most needs now, if anything. */
export function primaryAction(row: Row, now: Date): Action | null {
  const start = startOf(row)
  // A class whose day has passed is Completed: only its recording can still be owed.
  if (isPastDay(row, now)) return isDone(row, 'drive') || row.linkKind === 'other' ? null : isDone(row, 'record') ? 'sheet' : 'recording'
  const hours = (now.getTime() - start.getTime()) / 3_600_000
  if (hours < -0.25) return null
  // Its meeting did not open, or is no longer running: that comes before anything on the LMS.
  if (stepOf(row, 'zoom')?.state === 'failed' || (hours >= 0 && hours < 3 && row.live === false && !isDone(row, 'complete'))) return 'zoom'
  if (!isDone(row, 'run') && row.lmsStatus !== 'running' && row.lmsStatus !== 'finished') return 'run'
  if (hours >= 0.25 && hasMaterial(row) && ['due', 'retry'].includes(stepOf(row, 'material')?.state ?? '')) return 'material'
  if (hours >= 1.5 && !isDone(row, 'attendance')) return 'attendance'
  if (hours >= 3 && !isDone(row, 'correct') && row.lmsStatus !== 'finished') return 'correct'
  if (hours >= 3 && !isDone(row, 'complete') && row.lmsStatus !== 'finished') return 'complete'
  if (hours >= 2.5 || row.lmsStatus === 'finished') {
    if (isDone(row, 'drive') || row.linkKind === 'other') return null
    if (isDone(row, 'record')) return 'sheet'
    return 'zoomRecording'
  }
  return null
}

/** Every step that makes sense for this class, for its menu. */
export function availableActions(row: Row, now: Date): Action[] {
  const started = startOf(row).getTime() - now.getTime() < 20 * 60_000
  const material: Action[] = hasMaterial(row) ? ['material'] : []
  if (row.material?.assignmentTitle && row.material.deadline && !row.material.noAssignment) material.push('assignment')
  if (!started) return material
  const physical = row.mode === 'Physical'
  const mode: Action[] = [physical ? 'onZoom' : 'inRoom']
  // A physical class has no meeting: only the LMS half of it and its recording.
  if (isPastDay(row, now)) return physical ? ['recording', 'sheet', 'zoomRecording', 'noRecording', 'link', ...mode, ...material]
    : ['report', 'recording', 'sheet', 'zoomRecording', 'link', ...mode, ...material]
  if (physical) return ['run', 'complete', 'recording', 'zoomRecording', 'noRecording', 'sheet', 'link', ...mode, ...material]
  // Opening it by hand, or - when this PC still shows it running - starting it afresh.
  const zoom: Action[] = row.live ? ['zoomAgain'] : ['zoom']
  return [...zoom, 'run', 'attendance', 'correct', 'report', 'complete', 'recording', 'zoomRecording', 'sheet', 'link', ...mode, ...material]
}

/** Before today: the class is over and Completed. */
export const isPastDay = (row: Row, now: Date) => row.date < dayKey(now)

export const workingKey = (row: Row, action: Action) => `${row.key}|${action}`
