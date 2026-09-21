// What DashboardWebView.BuildState sends. Kept in step with that method by hand.
/** An account on the Coordinators page: who they are, whether they still work here, their groups. */
export interface DashUser {
  id: string
  username: string
  displayName: string
  role: string
  status: string
  lastLogin?: string | null
  groups: { id: string; name: string }[]
}

export interface DashState {
  server: { up: boolean; text: string; detail: string; clientMode: boolean }
  status: string
  busy: boolean
  savedLogin: boolean
  known: { username: string; displayName: string; role: string; hasSession: boolean; hasPassword?: boolean; lastUsed: string }[]
  me: null | { username: string; displayName: string; role: string; allGroups: boolean; groups: string[] }
  lms: { active?: string | null; onServer: boolean; canKeepPasswords: boolean; accounts: { id: string; label: string; email: string; role: string }[] }
  sessions: {
    running: number; today: number; attention: number; drivePending: number; done: number; total: number; lastRead: string; status: string
    next: null | { group: string; title: string; date: string; start: string }
    attentionRows: { group: string; title: string; date: string; start: string; next: string }[]
  }
  recordings: null | { total: number; onLms: number; pending: number; drive: number; zoomOnly: number; missing: number }
  users: null | DashUser[]
  groups: null | { id: string; name: string }[]
  /** Whose classes this PC runs besides its own (absent on a server that predates it). */
  runs?: null | {
    id: string
    enabled: boolean
    /** Both of their accounts are there, so their classes can actually run. */
    ready: boolean
    lms?: string | null
    zoomAccountId?: string | null
    zoomAccount?: string | null
    zoomAccounts: { id: string; name: string; group?: string | null; link: boolean }[]
    planned: number
    needsLink: number
    /** Their groups none of their Zoom accounts hosts (absent on an older server). */
    groupsWithoutZoom?: string[]
  }[]
  allGroups: null | { id: string; name: string; displayName?: string | null; archived: boolean; recordings: number; lastSession?: string | null; pending: number; onLms: number; missing: number; coordinators: string[] }[]
  /** The app's own update from the central server (absent in older builds). */
  update?: { current: string; version: string | null; sizeMb: number; status: string; progress: number | null; working: boolean }
  pages: { sessions: number; recordings: number; coordinators: number; server: number }
}

/** One thing a PC did and reported (GET api/v1/dashboard/activity). */
export interface Activity {
  id: number
  device: string | null
  deviceId: string
  at: string
  kind: string
  outcome: 'done' | 'failed' | 'skipped'
  group: string | null
  date: string | null
  summary: string
  detail: Record<string, unknown> | null
}
