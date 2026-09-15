// What the app sends (SessionsWebView.BuildState). Kept in step with that method by hand.

export type StepKey = 'zoom' | 'run' | 'attendance' | 'correct' | 'complete' | 'record' | 'drive' | 'material' | 'assignment'
export type StepStateName = 'done' | 'lms' | 'partial' | 'due' | 'retry' | 'failed' | 'future' | 'none'

export interface Step {
  key: StepKey
  label: string
  state: StepStateName
  text: string
  detail?: string | null
}

export interface Row {
  key: string
  group: string
  date: string // yyyy-MM-dd
  start: string // HH:mm
  title: string
  tone: 'future' | 'live' | 'bad' | 'warn' | 'done'
  next: string
  zoom: string
  lmsStatus: string
  lmsReadAt?: string | null
  lmsUrl?: string | null
  linkKind: string
  recordLink?: string | null
  steps: Step[]
  details: string
  material?: Material | null
}

/** A class's material: its track (Freelancing, Soft Skills, English, Technical), files and assignment. */
export interface Material {
  track: string
  number?: number | null
  folder?: string | null
  files: string[]
  skipped: string[]
  technical: boolean
  fixed: boolean
  note: string
  assignmentTitle?: string | null
  /** yyyy-MM-ddTHH:mm, local */
  deadline?: string | null
  noAssignment: boolean
  done: boolean
  description?: string | null
  /** The assignment's own file, when one was chosen */
  assignmentFile?: string | null
}

export interface Account {
  id: string
  label: string
  email: string
  role: string
  active: boolean
}

export interface State {
  now: string
  status: string
  busy: boolean
  lastRead: string
  stats: { running: number; today: number; attention: number; drivePending: number; done: number }
  account: string
  accounts: Account[]
  sheet: { url: string; tabs: Record<string, string>; groups: string[] }
  materials?: { tracks: { track: string; folder: string }[] }
  /** The days chosen with "Show these days"; null is the usual two weeks back and one ahead. */
  view?: { from: string; to: string } | null
  working: string[]
  rows: Row[]
}

/** A step the page can ask the app to do now, on the real LMS. */
export type Action = 'run' | 'attendance' | 'correct' | 'complete' | 'zoomRecording' | 'sheet' | 'recording' | 'link' | 'material' | 'assignment'

export interface Result {
  ok: boolean
  message: string
}
