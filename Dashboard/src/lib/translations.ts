import { CAIRO } from './format'

/** Human-readable translations for internal technical job names and stages */
export const JOB_TRANSLATIONS: Record<string, { title: string; action: string; description: string }> = {
  'class.run': {
    title: 'Zoom Live Session',
    action: 'Hosting Zoom meeting & admitting students',
    description: 'Cloud worker opens Zoom, admits waiting students, and tracks attendance.',
  },
  'lms.run_session': {
    title: 'Start LMS Session',
    action: 'Starting session on LMS',
    description: 'Marks the class session started on the LMS portal.',
  },
  'lms.attendance': {
    title: 'Submit Attendance',
    action: 'Submitting attendance to LMS',
    description: 'Uploads 90-minute student presence data directly to LMS roster.',
  },
  'lms.late_joiners': {
    title: 'Late Correction',
    action: 'Correcting late joiners attendance',
    description: 'Updates students who arrived after the initial 90-minute mark to Joined.',
  },
  'lms.complete': {
    title: 'Complete Session',
    action: 'Completing LMS session',
    description: 'Marks the session as completed on LMS after final attendance review.',
  },
  'zoom.report': {
    title: 'Zoom Report',
    action: 'Fetching Zoom participant report',
    description: 'Reads participant timestamps published by Zoom cloud.',
  },
  'zoom.recording': {
    title: 'Zoom Recording',
    action: 'Retrieving Zoom cloud recording',
    description: 'Locates the processed Zoom cloud recording URL and metadata.',
  },
  'recording.process': {
    title: 'Attach Recording',
    action: 'Updating recording link on LMS',
    description: 'Attaches the permanent Google Drive recording link to the LMS session.',
  },
}

export const STAGE_TRANSLATIONS: Record<string, { label: string; caption: string }> = {
  zoom: { label: 'Zoom', caption: '15m before class' },
  run: { label: 'Run Session', caption: 'At start' },
  attendance: { label: 'Attendance', caption: '90m into class' },
  lateJoiners: { label: 'Late Joiners', caption: 'End of class' },
  complete: { label: 'Complete', caption: 'After class' },
  ended: { label: 'Meeting Ended', caption: 'After class' },
  zoomReport: { label: 'Zoom Report', caption: 'When ready' },
  zoomRecording: { label: 'Zoom Recording', caption: 'When ready' },
  drive: { label: 'Drive Recording', caption: 'After class' },
  material: { label: 'Material', caption: 'Optional' },
  assignment: { label: 'Assignment', caption: 'Optional' },
}

export const OCCURRENCE_STATE_TRANSLATIONS: Record<string, { label: string; tone: 'green' | 'amber' | 'red' | 'blue' | 'slate'; description: string }> = {
  scheduled: { label: 'Scheduled', tone: 'slate', description: 'Class is scheduled to run at its appointed time.' },
  live: { label: 'Live Now', tone: 'green', description: 'Zoom meeting is currently active and admitting students.' },
  attendancePending: { label: 'Attendance Due', tone: 'amber', description: 'Waiting for attendance submission time.' },
  attendanceSubmitted: { label: 'Attendance Submitted', tone: 'green', description: 'Initial attendance has been uploaded to LMS.' },
  correctionPending: { label: 'Correction Due', tone: 'amber', description: 'Waiting for late joiners correction time.' },
  attendanceFinalized: { label: 'Attendance Finalized', tone: 'green', description: 'Late joiners processed and attendance closed.' },
  lmsSessionCompleted: { label: 'LMS Completed', tone: 'green', description: 'Class has been marked complete on LMS.' },
  recordingPending: { label: 'Recording Pending', tone: 'amber', description: 'Waiting for class recording to process.' },
  zoomLinkFound: { label: 'Zoom Recording Ready', tone: 'blue', description: 'Zoom cloud link found; queueing LMS attachment.' },
  zoomLinkAttached: { label: 'Zoom Link on LMS', tone: 'blue', description: 'Zoom recording attached temporarily while waiting for Drive.' },
  waitingForDrive: { label: 'Waiting for Drive', tone: 'amber', description: 'Waiting for Google Drive recording link.' },
  driveLinkFound: { label: 'Drive Link Ready', tone: 'blue', description: 'Google Drive recording link found in sync.' },
  driveLinkAttached: { label: 'Completed (Drive on LMS)', tone: 'green', description: 'Final Google Drive link successfully attached to LMS.' },
  conflict: { label: 'Recording Conflict', tone: 'red', description: 'A different link was already on LMS. Review needed.' },
  failed: { label: 'Needs Attention', tone: 'red', description: 'An automated step encountered an error.' },
  skipped: { label: 'Skipped', tone: 'slate', description: 'This class occurrence was manually skipped.' },
}

export function translateJobName(type: string): string {
  return JOB_TRANSLATIONS[type]?.title ?? type
}

export function translateJobAction(type: string): string {
  return JOB_TRANSLATIONS[type]?.action ?? type
}

export function translateOccurrenceState(state: string) {
  return OCCURRENCE_STATE_TRANSLATIONS[state] ?? {
    label: state,
    tone: 'slate' as const,
    description: '',
  }
}

/** Format Cairo time HH:mm */
export function formatCairoClock(date: Date | string | null): string {
  if (!date) return '—'
  const d = typeof date === 'string' ? new Date(date) : date
  return new Intl.DateTimeFormat('en-GB', {
    timeZone: CAIRO,
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(d)
}

/** Format Cairo time with seconds HH:mm:ss */
export function formatCairoClockWithSeconds(date: Date | string | null): string {
  if (!date) return '—'
  const d = typeof date === 'string' ? new Date(date) : date
  return new Intl.DateTimeFormat('en-GB', {
    timeZone: CAIRO,
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  }).format(d)
}

/** Human-readable event description for the operational activity feed */
export function formatHumanActivity(kind: string, summary: string, detail?: Record<string, unknown> | null): string {
  if (kind === 'lms.run_session' || summary.includes('run_session')) {
    return 'LMS session started'
  }
  if (kind === 'lms.attendance' || summary.includes('attendance')) {
    return 'Attendance submitted to LMS'
  }
  if (kind === 'lms.late_joiners' || summary.includes('late_joiners')) {
    return 'Late attendance correction completed'
  }
  if (kind === 'lms.complete' || summary.includes('complete')) {
    return 'LMS session marked complete'
  }
  if (kind === 'zoom.recording' || summary.includes('zoom.recording')) {
    return 'Zoom cloud recording link found'
  }
  if (kind === 'recording.process' || summary.includes('recording.process')) {
    if (detail && typeof detail.link === 'string' && detail.link.includes('drive.google.com')) {
      return 'Google Drive recording link attached to LMS'
    }
    return 'Recording link attached to LMS'
  }
  if (kind === 'class.run' || summary.includes('class.run')) {
    return 'Zoom meeting admitted students & held session'
  }
  if (kind === 'google_sheets.sync') {
    return 'Google Sheets recording sync executed'
  }
  return summary || kind
}
