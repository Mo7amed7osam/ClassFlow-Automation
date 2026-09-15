// Rosters and attendance: the shapes of /api/v1/dashboard/students* and /attendance/* answers
// (Backend/central_backend/attendance.py), and the hooks that call them.
import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { api, query } from './client'

export interface Student {
  id: string
  group: string
  fullName: string
  email: string | null
  externalId: string | null
  order: number | null
  aliases: string[]
  active: boolean
  createdAt: string
  updatedAt: string
}

export interface StudentAlias {
  id: string
  alias: string
  status: 'accepted' | 'rejected'
  source: string
  createdAt: string
}

export interface ImportResult {
  group: string
  dryRun: boolean
  created: number
  updated: number
  unchanged: number
  invalidRows?: string[]
  duplicateRows?: string[]
  skipped: string[]
  createdNames: string[]
  updatedNames: string[]
}

export type AttendanceStatus = 'present' | 'needs_review' | 'absent'
export type SessionStatus = 'open' | 'closed' | 'finalized'

export interface AttendanceSession {
  id: string
  group: string
  date: string
  startTime: string | null
  title: string | null
  source: string
  status: SessionStatus
  recordingId: string | null
  startedAt: string | null
  endedAt: string | null
  matchedAt: string | null
  finalizedAt: string | null
  createdAt: string
  updatedAt: string
}

export interface SessionListItem extends AttendanceSession {
  present: number
  needsReview: number
  absent: number
  lastCapturedAt: string | null
}

export interface SessionPage {
  items: SessionListItem[]
  total: number
  page: number
  pageSize: number
}

export interface AttendanceRecordView {
  studentId: string
  fullName: string
  email: string | null
  order: number | null
  active: boolean
  status: AttendanceStatus
  confidence: number
  source: string
  reason: string | null
  manual: boolean
  participant: { id: string; name: string } | null
  extraNames: string[]
  joinTime: string | null
  leaveTime: string | null
  durationSeconds: number | null
}

export interface ParticipantView {
  id: string
  name: string
  firstSeenAt: string
  lastSeenAt: string
  sightings: number
  presentSeconds: number
  ignored: boolean
  assignedTo: string | null
  candidates: { studentId: string; score: number }[]
}

export interface AiSuggestion {
  studentId: string
  participantId: string
  name: string
  confidence: number
  applied: boolean
}

export interface SessionDetails {
  session: AttendanceSession
  records: AttendanceRecordView[]
  participants: ParticipantView[]
  snapshots: { count: number; lastCapturedAt: string | null }
  summary: { present: number; needsReview: number; absent: number; students: number; unmatched: number }
  aiAvailable: boolean
  aiSuggestions?: AiSuggestion[]
}

export interface SessionQuery {
  group?: string
  date?: string
  status?: SessionStatus | ''
  page?: number
  pageSize?: number
}

export const SESSION_REFRESH_MS = 15_000

const post = (body?: unknown): RequestInit => ({ method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })

// ----------------------------------------------------------------------------- students

export function useStudents(params: { group?: string; q?: string; includeInactive?: boolean }) {
  return useQuery({
    queryKey: ['students', params],
    queryFn: () => api<{ students: Student[]; count: number }>(`/api/v1/dashboard/students${query({
      group: params.group, q: params.q, includeInactive: params.includeInactive ? 'true' : undefined })}`),
    placeholderData: keepPreviousData,
  })
}

function refreshStudents(client: QueryClient) {
  for (const key of ['students', 'attendance-session', 'attendance-sessions', 'groups']) client.invalidateQueries({ queryKey: [key] })
}

export function useCreateStudent() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (body: { group: string; fullName: string; email?: string; externalId?: string; order?: number | null; aliases?: string[] }) =>
      api<Student>('/api/v1/dashboard/students', post(body)),
    onSuccess: () => refreshStudents(client),
  })
}

export function useUpdateStudent() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, ...body }: { id: string; group?: string; fullName?: string; email?: string | null; externalId?: string | null;
      order?: number | null; aliases?: string[]; active?: boolean }) =>
      api<Student & { changed: string[] }>(`/api/v1/dashboard/students/${id}`, { method: 'PATCH', body: JSON.stringify(body) }),
    onSuccess: () => refreshStudents(client),
  })
}

export function useImportStudents() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (body: { group: string; text: string; dryRun: boolean }) => api<ImportResult>('/api/v1/dashboard/students/import', post(body)),
    onSuccess: (result) => { if (!result.dryRun) refreshStudents(client) },
  })
}

export function useStudentAliases(id: string | null) {
  return useQuery({
    queryKey: ['student-aliases', id],
    queryFn: () => api<{ aliases: StudentAlias[] }>(`/api/v1/dashboard/students/${id}/aliases`),
    enabled: id !== null,
  })
}

export function useForgetAlias() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api<{ status: string }>(`/api/v1/dashboard/aliases/${id}`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['student-aliases'] }),
  })
}

// ----------------------------------------------------------------------------- sessions

export function useAttendanceSessions(params: SessionQuery) {
  return useQuery({
    queryKey: ['attendance-sessions', params],
    queryFn: () => api<SessionPage>(`/api/v1/dashboard/attendance/sessions${query({ ...params })}`),
    refetchInterval: SESSION_REFRESH_MS * 2,
    placeholderData: keepPreviousData,
  })
}

export function useCreateSession() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (body: { group: string; date: string; startTime?: string; title?: string }) =>
      api<AttendanceSession>('/api/v1/dashboard/attendance/sessions', post(body)),
    onSuccess: () => client.invalidateQueries({ queryKey: ['attendance-sessions'] }),
  })
}

/** One session; refreshes while it is still open (snapshots keep arriving from the agent). */
export function useSessionDetails(id: string | null) {
  return useQuery({
    queryKey: ['attendance-session', id],
    queryFn: () => api<SessionDetails>(`/api/v1/dashboard/attendance/sessions/${id}`),
    enabled: id !== null,
    refetchInterval: (q) => (q.state.data?.session.status === 'finalized' ? false : SESSION_REFRESH_MS),
  })
}

/** Every session change answers with the whole session: it replaces the cached copy at once. */
function useSessionChange<Vars>(request: (vars: Vars) => Promise<SessionDetails>) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: request,
    onSuccess: (details) => {
      client.setQueryData(['attendance-session', details.session.id], details)
      client.invalidateQueries({ queryKey: ['attendance-sessions'] })
    },
  })
}

const sessionUrl = (id: string) => `/api/v1/dashboard/attendance/sessions/${id}`

export const useAddParticipants = () =>
  useSessionChange(({ id, names }: { id: string; names: string[] }) => api<SessionDetails>(`${sessionUrl(id)}/participants`, post({ names })))

export const useRematch = () =>
  useSessionChange(({ id, useAi }: { id: string; useAi: boolean }) => api<SessionDetails>(`${sessionUrl(id)}/match`, post({ useAi })))

export const useCorrectRecord = () =>
  useSessionChange(({ id, studentId, ...body }: { id: string; studentId: string; participantId?: string | null;
    status?: AttendanceStatus; reset?: boolean; remember?: boolean }) =>
    api<SessionDetails>(`${sessionUrl(id)}/records/${studentId}`, { method: 'PUT', body: JSON.stringify(body) }))

export const useIgnoreParticipant = () =>
  useSessionChange(({ id, participantId, ignored }: { id: string; participantId: string; ignored: boolean }) =>
    api<SessionDetails>(`${sessionUrl(id)}/participants/${participantId}/ignore`, post({ ignored })))

export const useFinalizeSession = () =>
  useSessionChange(({ id, reopen }: { id: string; reopen: boolean }) => api<SessionDetails>(`${sessionUrl(id)}/${reopen ? 'reopen' : 'finalize'}`, post()))

export const exportUrl = (id: string) => `${sessionUrl(id)}/export.csv`
