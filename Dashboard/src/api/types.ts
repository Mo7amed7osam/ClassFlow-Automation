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
