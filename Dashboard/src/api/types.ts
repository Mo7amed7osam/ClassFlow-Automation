// The shapes of the answers of /api/v1/dashboard/*, /api/v1/auth/* and /api/v1/admin/*
// (see Backend/central_backend/dashboard.py, auth.py and admin.py).

export type LinkKind = 'drive' | 'zoom' | 'missing'

export interface LinkStatus {
  link: LinkKind
  lms: string
  label: string
}

export interface Recording {
  id: string
  group: string
  date: string
  startTime: string | null
  fileName: string | null
  type: string | null
  driveLink: string | null
  zoomLink: string | null
  source: 'drive' | 'zoom' | string
  lmsStatus: string
  lmsUpdatedAt: string | null
  createdAt: string
  updatedAt: string
  linkStatus: LinkStatus
  /** The newest job the dashboard created for this recording, if any. */
  lastJob?: JobSummary | null
}

export type RecordingSort = 'updated' | 'created' | 'session'

/** GET /api/v1/dashboard/recordings/{id} */
export interface RecordingDetails {
  recording: Recording
  jobs: JobSummary[]
  audit: AuditEntry[]
}

export interface AuditEntry {
  action: string
  username: string
  details: Record<string, unknown> | null
  createdAt: string
}

/** The fields PATCH /api/v1/dashboard/recordings/{id} accepts; only the ones sent change. */
export interface RecordingChanges {
  group?: string
  date?: string
  startTime?: string
  fileName?: string
  type?: string
  link?: string
}

export type EditResult = Recording & { changed: string[] }

export interface AttachOptions {
  replaceExisting: boolean
  dryRun: boolean
}

/** 202 from POST /api/v1/dashboard/recordings/{id}/attach */
export interface AttachResult {
  recordingId: string
  jobId: string
  status: string
  dryRun: boolean
  replaceExisting: boolean
}

/** 200 from POST /api/v1/dashboard/recordings/{id}/cancel */
export interface CancelResult {
  recordingId: string
  jobId: string
  status: 'cancelled'
}

export interface RecordingPage {
  items: Recording[]
  total: number
  page: number
  pageSize: number
  sort: RecordingSort
}

export interface RecordingQuery {
  group?: string
  date?: string
  status?: string
  link?: LinkKind | ''
  sort?: RecordingSort
  page?: number
  pageSize?: number
}

export interface GroupSummary {
  id: string
  group: string
  displayName: string | null
  archived: boolean
  recordings: number
  /** null for a group with no recording yet */
  lastSessionDate: string | null
  lastUpdatedAt: string | null
  pending: number
  onLms: number
  missingLink: number
}

/** GET /api/v1/admin/groups: a group and the coordinators who see it. */
export interface AdminGroup extends GroupSummary {
  coordinators: { id: string; username: string; displayName: string; status: UserStatus }[]
}

/** A group as it appears on an account: GET /api/v1/auth/me, /api/v1/admin/users. */
export interface GroupRef {
  id: string
  name: string
  displayName: string | null
  archived: boolean
}

export interface JobSummary {
  jobId: string
  type: string
  status: string
  group: string | null
  date: string | null
  attempts: number
  createdAt: string
  assignedAt: string | null
  startedAt: string | null
  finishedAt: string | null
  errorCode: string | null
  alreadyExists: boolean | null
  recordingId?: string | null
  dryRun?: boolean
  replaceExisting?: boolean
}

export interface Agent {
  deviceId: string
  name: string
  status: 'online' | 'offline'
  connected: boolean
  agentState: 'idle' | 'busy' | null
  version: string
  capabilities: string[]
  lastHeartbeat: string | null
  lastAssignedAt: string | null
  revoked: boolean
  createdAt: string
  updatedAt: string
  activeJobs: JobSummary[]
  jobsLast24h: { succeeded: number; failed: number }
}

/** A Zoom meeting currently owned by a cloud worker. */
export interface LiveSession extends JobSummary {
  meetingUrl: string | null
  durationMinutes: number | null
  worker: string | null
  attendanceSessionId: string | null
  attendanceStatus: 'open' | 'closed' | 'finalized' | null
  snapshots: number
  observed: number
}

export interface Overview {
  /** null for a coordinator: agents and jobs span every group */
  agents: { total: number; online: number; busy: number } | null
  recordings: { total: number; pending: number; onLms: number; missingLink: number }
  jobs: { queued: number; assigned: number; running: number; succeededLast24h: number; failedLast24h: number } | null
  /** groups the viewer sees (active ones, for the admin) */
  groups: number
  serverTime: string
}

export type Role = 'admin' | 'coordinator'
export type UserStatus = 'pending' | 'active' | 'rejected' | 'disabled'

/** GET /api/v1/auth/me */
export interface Me {
  id: string
  username: string
  displayName: string
  role: Role
  /** true for the admin, who sees every group */
  allGroups: boolean
  /** the coordinator's groups; null for the admin */
  groups: GroupRef[] | null
  expiresAt: string
}

/** An account, as the admin sees it (GET /api/v1/admin/users). */
export interface User {
  id: string
  username: string
  displayName: string
  role: Role
  status: UserStatus
  createdAt: string
  approvedAt: string | null
  lastLoginAt: string | null
  groups: GroupRef[]
}

export interface UserList {
  users: User[]
  count: number
  counts: Record<UserStatus, number>
}

// --------------------------------------------------------------- other people's classes, run here

/** One of a user's LMS sign-ins, as the admin sees it. Never a password. */
export interface LmsAccountRef {
  id: string
  label: string
  email: string
  role: Role
  active: boolean
}

export type PreferredEngine = 'desktop' | 'web'

/**
 * One Zoom account a user opens classes with, kept against their dashboard account by their own
 * copy of the app. No sign-in is kept: that stays in the Zoom app or a browser profile on their PC.
 */
export interface ZoomAccountRef {
  id: string
  accountId: string
  label: string
  zoomEmail: string | null
  group: string | null
  /** the link this account's classes open, which is where a class's link comes from */
  meetingUrl: string | null
  preferredEngine: PreferredEngine | null
  active: boolean
}

/** A coordinator, and whether the admin's PC opens and finishes their classes for them. */
export interface Delegation {
  coordinatorId: string
  username: string
  displayName: string
  status: UserStatus
  enabled: boolean
  groups: GroupRef[]
  /** the sign-in their classes go up under: the one chosen, or the one they have in use */
  lmsAccount: LmsAccountRef | null
  lmsAccounts: LmsAccountRef[]
  /** which of their own Zoom accounts opens their meetings */
  zoomAccountId: string | null
  zoomAccount: string | null
  zoomAccounts: ZoomAccountRef[]
  /** their groups none of their Zoom accounts hosts, so those cannot open yet */
  groupsWithoutZoom?: string[]
  classes: { planned: number; done: number; skipped: number; needsLink: number }
  updatedAt: string | null
}

export type ClassPlanStatus = 'planned' | 'skipped' | 'opened' | 'done' | 'failed'

/** One class of a delegated coordinator, read from their own LMS session list. */
export interface ClassPlan {
  id: string
  coordinatorId: string
  group: string
  date: string
  startTime: string | null
  title: string | null
  meetingUrl: string | null
  zoomAccount: string | null
  preferredEngine: PreferredEngine | null
  source: 'lms' | 'manual'
  status: ClassPlanStatus
  note: string | null
  /** still waiting for the one thing the LMS cannot give: the Zoom link */
  needsLink: boolean
  importedAt: string | null
  updatedAt: string
}

export interface RunCoordinator {
  coordinatorId: string
  displayName: string
  username: string
  enabled: boolean
  zoomAccount: string | null
  zoomAccounts: ZoomAccountRef[]
  lmsAccount: LmsAccountRef | null
}

export interface RunPlan {
  classes: ClassPlan[]
  coordinators: RunCoordinator[]
}

// ============================================================ the Sessions page

/** What a stage of a class is doing. `missing` means nothing implements it yet, which is not the
 *  same as "not started" and must not look the same on the card. */
export type StageState =
  | 'done' | 'running' | 'waiting' | 'due' | 'later' | 'failed' | 'blocked' | 'missing'

export interface SessionStage {
  key: string
  label: string
  /** What the Windows card writes under a stage: "Opens", "At start", "After class". */
  caption: string
  state: StageState
  /** The time it is due, or when it finished, or why it cannot run - whichever applies. */
  detail?: string | null
  dueAt?: string | null
  jobId?: string | null
  retryable?: boolean
  /** Set when the stage succeeded and something is still wrong - a meeting that could not be
   *  closed, most often. Drawn as a tick with an amber ring rather than a clean one. */
  warning?: string | null
}

/** A class is `needsAttention` when any stage failed, `blocked` when one cannot run at all. */
export type SessionHeadline = 'running' | 'needsAttention' | 'blocked' | 'done' | 'planned'

export interface SessionClass {
  classPlanId: string
  group: string
  title: string | null
  date: string
  startTime: string | null
  startsAt: string | null
  coordinator: { id: string; name: string | null }
  meetingUrl: string | null
  planStatus: string
  stages: SessionStage[]
  headline: SessionHeadline
}

export interface SessionsPage {
  from: string
  to: string
  classes: SessionClass[]
  counters: {
    runningOnTheLms: number
    classesToday: number
    needAttention: number
    blocked: number
    fullyDone: number
  }
  groups: string[]
}

// ============================================ your own accounts, your timetable, and the switches
// (see Backend/central_backend/user_data.py, activity.py and worker_support.py)

/** One of your own LMS sign-ins. The password is never sent back - only whether one is kept. */
export interface MyLmsAccount {
  id: string
  label: string
  email: string
  role: Role
  /** the one your classes go up under */
  active: boolean
  updatedAt?: string | null
}

export interface MyLmsAccounts {
  accounts: MyLmsAccount[]
  /** false when the server has no encryption key: passwords cannot be kept at all then. */
  canKeepPasswords: boolean
}

/** One of your own Zoom accounts, as you keep it. `hasPassword` is how a server signs in as you. */
export interface MyZoomAccount extends ZoomAccountRef {
  hasPassword: boolean
  updatedAt?: string | null
}

/** What PUT /api/v1/me/zoom-accounts takes: the whole set, since what you keep is what is kept. */
export interface ZoomAccountInput {
  accountId: string
  label: string
  zoomEmail: string | null
  group: string | null
  meetingUrl: string | null
  preferredEngine: PreferredEngine | null
  active: boolean
  /** left out to keep the saved password, "" to remove it, anything else to replace it */
  password?: string
}

/**
 * One class that opens by itself. The server keeps these as the Windows app writes them and never
 * reads inside one, so this mirrors the app's own shape (MeetingSchedule) rather than inventing a
 * second one: a class edited here is the same class on a PC that syncs.
 */
/** What a class opens with, in the app's own spelling (the dashboard's own is lower case). */
export type ScheduleEngine = 'Web' | 'Desktop'

export interface ScheduleRow {
  id: string
  name: string
  meetingUrl: string
  accountId: string
  /** "19:00:00" as the app writes it */
  time: string
  /** the days as the app's flags name them: "Monday, Wednesday", or "None" for a one-off */
  days: string
  enabled: boolean
  /** a single class on one date, instead of a weekly one */
  occurrenceDate?: string | null
  groupName?: string | null
  /** The app's own enum names, as it writes them: "Web" or "Desktop". */
  preferredEngine?: ScheduleEngine | null
  /** whose class it is when one machine runs several people's; null for your own */
  coordinator?: string | null
  coordinatorId?: string | null
  lastTriggeredDate?: string | null
}

export interface SchedulesAnswer {
  schedules: ScheduleRow[]
  count: number
  /** the PC that last sent them */
  deviceName: string | null
  updatedAt: string | null
}

/** One thing a machine did, from GET /api/v1/dashboard/activity. */
export interface ActivityItem {
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

export type SessionRoleName = 'Instructor' | 'CoHost'

/** One configured person for a session type: who teaches it, or who may be made co-host in it. */
export interface RolePerson {
  name: string
  role: SessionRoleName
  /** Zoom display names already seen for them */
  aliases?: string[]
}

/** Who is made co-host when a session of one kind starts (the Windows app's session roles). */
export interface SessionRoleProfile {
  sessionType: string
  /** words matched against the class's name to recognise this kind of session */
  keywords: string[]
  /** groups or accounts that always mean this kind, whatever the name says */
  accounts: string[]
  people: RolePerson[]
}

/** The switches every worker reads: GET/PUT /api/v1/settings/cloudPolicy. */
export interface CloudPolicy {
  autoCoHost: boolean
  autoEnd: boolean
}

/** Where the recordings sheet is read from, for the machine that syncs it. */
export interface RecordingsSheet {
  spreadsheetId?: string
  sheetName?: string
  [key: string]: unknown
}

/** The server-owned, read-only Google Sheet connection used for recording links. */
export interface GoogleSheetsStatus {
  configured: boolean
  spreadsheetId: string | null
  googleEmail: string | null
  connectedAt: string | null
  states: Record<string, number>
}

export interface GoogleAuthorization {
  authorizationUrl: string
}

export interface GoogleSheetSyncResult {
  seen: number
  pending: number
  conflict: number
  skipped: number
}

/** GET /api/v1/settings/{key}: the value, or null when nothing has been saved yet. */
export interface Setting<T> {
  key: string
  value: T | null
  updatedAt: string | null
}

/**
 * GET /api/v1/dashboard/notifications: whether a class that needs somebody is said out loud, and
 * whether there is a webhook to say it to. Never the address itself - that is kept like a password.
 */
export interface NotifySettings {
  enabled: boolean
  hasUrl: boolean
  /** just the host of the webhook, to recognise it by */
  urlHost: string | null
  label: string
  updatedAt: string | null
  lastSentAt: string | null
  lastError: string | null
}

/** 200 from POST /api/v1/dashboard/notifications/test. */
export interface NotifyTest {
  sent: boolean
  detail: string | null
  enabled: boolean
}

/** GET /api/v1/dashboard/ai: whether attendance matching can ask an AI, and which model. */
export interface AiStatus {
  available: boolean
  model: string | null
}

/** 202 from POST /api/v1/admin/run-plan/{id}/run. */
export type StageRun = JobSummary & { created: boolean }

/** 200 from POST /api/v1/me/devices/enroll: the token a machine spends at once to join. */
export interface Enrollment {
  enrollmentToken: string
  expiresInSeconds: number
}

export interface ClassOccurrenceView {
  id: string
  state: string
  actualStart: string | null
  actualEnd: string | null
  lastError: string | null
  zoomMeetingUrl: string | null
  zoomRecordingUrl: string | null
  driveRecordingUrl: string | null
  recordingFoundAt: string | null
  retryState: Record<string, unknown>
  nextRetryAt: string | null
  attendanceSessionId: string | null
  lmsSessionUrl: string | null
  lmsSessionId: string | null
}

export interface ClassDetails {
  class: SessionClass
  occurrence: ClassOccurrenceView | null
  attendance: {
    id: string
    status: string
    students: number
    present: number
    needsReview: number
    absent: number
  } | null
  recording: {
    id: string
    zoomLink: string | null
    driveLink: string | null
    lmsStatus: string
    lmsUpdatedAt: string | null
  } | null
  jobs: JobSummary[]
  activity: {
    id: number
    kind: string
    outcome: 'done' | 'failed' | 'skipped'
    summary: string
    at: string
    detail: Record<string, unknown> | null
  }[]
}

export interface HealthCheckItem {
  name: string
  status: 'healthy' | 'warning' | 'failed'
  summary: string
  detail: string
  fix: string | null
  action: string | null
}

export interface SystemHealth {
  overall: 'healthy' | 'warning' | 'failed'
  checks: HealthCheckItem[]
  serverTime: string
}

export interface NotificationItem {
  id: string
  timestamp: string
  severity: 'error' | 'warning' | 'info' | 'success'
  title: string
  description: string
  classPlanId?: string | null
  group?: string | null
  read?: boolean
}

