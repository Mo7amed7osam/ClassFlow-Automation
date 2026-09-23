import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { api, query, UnauthorizedError } from './client'
import type {
  ActivityItem,
  AdminGroup,
  AiStatus,
  Agent,
  ClassPlan,
  CloudPolicy,
  Delegation,
  Enrollment,
  MyLmsAccount,
  MyLmsAccounts,
  MyZoomAccount,
  NotifySettings,
  NotifyTest,
  AttachOptions,
  AttachResult,
  CancelResult,
  EditResult,
  GroupSummary,
  JobSummary,
  Me,
  Overview,
  Role,
  Recording,
  RecordingChanges,
  RecordingDetails,
  RecordingPage,
  RecordingQuery,
  RunPlan,
  ScheduleRow,
  SchedulesAnswer,
  SessionRoleProfile,
  SessionsPage,
  Setting,
  StageRun,
  ZoomAccountInput,
  User,
  UserList,
  UserStatus,
} from './types'

// How often each view refreshes itself. Agents change fastest (heartbeats every 30 s); a recording
// with a job in flight is watched closely until the agent reports back.
export const REFRESH = { agents: 10_000, overview: 15_000, recordings: 30_000, groups: 60_000, activeJob: 5_000, sessions: 20_000 }

/** Every class in the window and where each one stands. Refreshed on its own, because a class
 *  card changes while somebody is looking at it: a stage goes from due to running to done. */
export function useSessions(params: { from?: string; to?: string; group?: string }) {
  return useQuery({
    queryKey: ['sessions', params],
    queryFn: () => api<SessionsPage>(`/api/v1/dashboard/sessions${query(params)}`),
    refetchInterval: REFRESH.sessions,
    placeholderData: keepPreviousData,
  })
}

export const ACTIVE_JOB_STATUSES = ['queued', 'assigned', 'running']

export function hasActiveJob(recording: Pick<Recording, 'lastJob'> | undefined): boolean {
  return Boolean(recording?.lastJob && ACTIVE_JOB_STATUSES.includes(recording.lastJob.status))
}

/** The signed-in user (the admin or a coordinator), or null when there is no session. */
export function useMe() {
  return useQuery({
    queryKey: ['me'],
    queryFn: async (): Promise<Me | null> => {
      try {
        return await api<Me>('/api/v1/auth/me')
      } catch (error) {
        if (error instanceof UnauthorizedError) return null
        throw error
      }
    },
    staleTime: 60_000,
  })
}

export function useLogin() {
  const client = useQueryClient()
  return useMutation({
    // The login answer is short; /me adds the role's groups, which every page needs.
    mutationFn: async (credentials: { username: string; password: string }) => {
      await api('/api/v1/auth/login', { method: 'POST', body: JSON.stringify(credentials) })
      return api<Me>('/api/v1/auth/me')
    },
    onSuccess: (me) => {
      client.clear()
      client.setQueryData(['me'], me)
    },
  })
}

export function useLogout() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: () => api<{ status: string }>('/api/v1/auth/logout', { method: 'POST' }),
    onSettled: () => {
      client.clear()
      client.setQueryData(['me'], null)
    },
  })
}

/** A coordinator asks for an account; it waits for the admin's approval. */
export function useRegister() {
  return useMutation({
    mutationFn: (account: { username: string; displayName: string; password: string }) =>
      api<{ username: string; status: string; message: string }>('/api/v1/auth/register', {
        method: 'POST',
        body: JSON.stringify(account),
      }),
  })
}

/** Anyone's own password. Other browsers are signed out; this one stays in. */
export function useChangePassword() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) =>
      api<{ status: string }>('/api/v1/auth/password', { method: 'POST', body: JSON.stringify(body) }),
  })
}

export function useOverview() {
  return useQuery({
    queryKey: ['overview'],
    queryFn: () => api<Overview>('/api/v1/dashboard/overview'),
    refetchInterval: REFRESH.overview,
  })
}

export function useRecordings(params: RecordingQuery) {
  return useQuery({
    queryKey: ['recordings', params],
    queryFn: () => api<RecordingPage>(`/api/v1/dashboard/recordings${query({ ...params })}`),
    refetchInterval: (q) => (q.state.data?.items.some(hasActiveJob) ? REFRESH.activeJob : REFRESH.recordings),
    placeholderData: keepPreviousData,
  })
}

/** One recording with its jobs and audit trail (the details drawer). */
export function useRecordingDetails(id: string | null) {
  return useQuery({
    queryKey: ['recording', id],
    queryFn: () => api<RecordingDetails>(`/api/v1/dashboard/recordings/${id}`),
    enabled: id !== null,
    refetchInterval: (q) => (hasActiveJob(q.state.data?.recording) ? REFRESH.activeJob : REFRESH.recordings),
  })
}

/** After a change, everything that shows recordings is fetched again. */
function refreshRecordings(client: QueryClient) {
  for (const key of ['recordings', 'recording', 'groups', 'overview']) client.invalidateQueries({ queryKey: [key] })
}

export function useEditRecording() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, changes }: { id: string; changes: RecordingChanges }) =>
      api<EditResult>(`/api/v1/dashboard/recordings/${id}`, { method: 'PATCH', body: JSON.stringify(changes) }),
    onSuccess: () => refreshRecordings(client),
  })
}

export function useAttachRecording() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, options }: { id: string; options: AttachOptions }) =>
      api<AttachResult>(`/api/v1/dashboard/recordings/${id}/attach`, { method: 'POST', body: JSON.stringify(options) }),
    onSuccess: () => refreshRecordings(client),
  })
}

/** Stop the recording's attach job while it still waits in the queue (not once an agent has it). */
export function useCancelRecordingJob() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api<CancelResult>(`/api/v1/dashboard/recordings/${id}/cancel`, { method: 'POST' }),
    onSuccess: () => refreshRecordings(client),
  })
}

export function useGroups() {
  return useQuery({
    queryKey: ['groups'],
    queryFn: () => api<{ groups: GroupSummary[]; count: number }>('/api/v1/dashboard/groups'),
    refetchInterval: REFRESH.groups,
  })
}

export function useAgents() {
  return useQuery({
    queryKey: ['agents'],
    queryFn: () => api<{ agents: Agent[]; count: number; online: number }>('/api/v1/dashboard/agents'),
    refetchInterval: REFRESH.agents,
  })
}

export function useAgentJobs(deviceId: string | null) {
  return useQuery({
    queryKey: ['agent-jobs', deviceId],
    queryFn: () => api<{ jobs: JobSummary[]; count: number }>(`/api/v1/dashboard/agents/${deviceId}/jobs?limit=20`),
    enabled: deviceId !== null,
    refetchInterval: REFRESH.agents,
  })
}

// ----------------------------------------------------------------------------- the admin's tools

export function useUsers(status?: UserStatus | '', enabled = true) {
  return useQuery({
    queryKey: ['users', status ?? ''],
    queryFn: () => api<UserList>(`/api/v1/admin/users${query({ status })}`),
    refetchInterval: REFRESH.groups,
    enabled,
  })
}

export function useAdminGroups(enabled = true) {
  return useQuery({
    queryKey: ['admin-groups'],
    queryFn: () => api<{ groups: AdminGroup[]; count: number }>('/api/v1/admin/groups'),
    refetchInterval: REFRESH.groups,
    enabled,
  })
}

/** After an account or group change, every list that shows accounts or groups is fetched again. */
function refreshAccounts(client: QueryClient) {
  for (const key of ['users', 'admin-groups', 'groups', 'overview']) client.invalidateQueries({ queryKey: [key] })
}

function useAdminMutation<Vars, Result>(request: (vars: Vars) => Promise<Result>) {
  const client = useQueryClient()
  return useMutation({ mutationFn: request, onSuccess: () => refreshAccounts(client) })
}

const send = (method: string, body?: unknown): RequestInit => ({
  method,
  body: body === undefined ? undefined : JSON.stringify(body),
})

export const useCreateUser = () =>
  useAdminMutation((body: { username: string; displayName: string; password: string; groupIds: string[] }) =>
    api<User>('/api/v1/admin/users', send('POST', body)),
  )

export const useUpdateUser = () =>
  useAdminMutation(({ id, ...body }: { id: string; displayName?: string; status?: 'active' | 'disabled' }) =>
    api<User>(`/api/v1/admin/users/${id}`, send('PATCH', body)),
  )

export const useReviewUser = () =>
  useAdminMutation(({ id, decision }: { id: string; decision: 'approve' | 'reject' }) =>
    api<User>(`/api/v1/admin/users/${id}/${decision}`, send('POST')),
  )

export const useResetPassword = () =>
  useAdminMutation(({ id, password }: { id: string; password: string }) =>
    api<{ status: string }>(`/api/v1/admin/users/${id}/password`, send('POST', { password })),
  )

export const useSetUserGroups = () =>
  useAdminMutation(({ id, groupIds }: { id: string; groupIds: string[] }) =>
    api<User>(`/api/v1/admin/users/${id}/groups`, send('PUT', { groupIds })),
  )

export const useCreateGroup = () =>
  useAdminMutation((body: { name: string; displayName?: string }) => api('/api/v1/admin/groups', send('POST', body)))

export const useUpdateGroup = () =>
  useAdminMutation(({ id, ...body }: { id: string; displayName?: string | null; archived?: boolean }) =>
    api(`/api/v1/admin/groups/${id}`, send('PATCH', body)),
  )

// ------------------------------------------------- the coordinators whose classes this PC runs

/** Every coordinator, with their groups, their LMS sign-in, and whether we run their classes. */
export function useDelegations(enabled = true) {
  return useQuery({
    queryKey: ['delegations'],
    queryFn: () => api<{ delegations: Delegation[] }>('/api/v1/admin/delegations'),
    refetchInterval: REFRESH.groups,
    enabled,
  })
}

/**
 * The classes to run between two days. `coordinators` narrows it to some of the people turned on;
 * empty means all of them. The ids go in as repeated parameters, which is what the backend reads.
 */
export function useRunPlan(from: string, to: string, coordinators: string[] = [], enabled = true) {
  const who = coordinators.map((id) => `&coordinator=${encodeURIComponent(id)}`).join('')
  return useQuery({
    queryKey: ['run-plan', from, to, [...coordinators].sort().join(',')],
    queryFn: () => api<RunPlan>(`/api/v1/admin/run-plan?from=${from}&to=${to}${who}`),
    refetchInterval: REFRESH.groups,
    placeholderData: keepPreviousData,
    enabled,
  })
}

/** After turning somebody on or off, or changing a class, both lists are read again. */
function refreshRuns(client: QueryClient) {
  for (const key of ['delegations', 'run-plan']) client.invalidateQueries({ queryKey: [key] })
}

function useRunMutation<Vars, Result>(request: (vars: Vars) => Promise<Result>) {
  const client = useQueryClient()
  return useMutation({ mutationFn: request, onSuccess: () => refreshRuns(client) })
}

export const useSetDelegation = () =>
  useRunMutation(({ coordinatorId, ...body }: { coordinatorId: string; enabled: boolean; lmsAccountId?: string | null; zoomAccountId?: string | null }) =>
    api<{ coordinatorId: string; enabled: boolean }>(`/api/v1/admin/delegations/${coordinatorId}`, send('PUT', body)),
  )

export const useUpdateClassPlan = () =>
  useRunMutation(({ id, ...body }: {
    id: string
    meetingUrl?: string | null
    zoomAccount?: string | null
    preferredEngine?: 'desktop' | 'web' | 'auto' | null
    status?: ClassPlan['status']
    note?: string | null
    applyToGroup?: boolean
  }) => api<ClassPlan & { alsoInGroup: number }>(`/api/v1/admin/run-plan/${id}`, send('PATCH', body)))

// ==================================================== your own accounts, timetable, and the switches

/** Your LMS sign-ins. Nothing here is a password: the server only says whether it kept one. */
export function useMyLmsAccounts() {
  return useQuery({
    queryKey: ['my-lms-accounts'],
    queryFn: () => api<MyLmsAccounts>('/api/v1/me/lms-accounts'),
    refetchInterval: REFRESH.groups,
  })
}

function useMyMutation<Vars, Result>(keys: string[], request: (vars: Vars) => Promise<Result>) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: request,
    onSuccess: () => { for (const key of keys) client.invalidateQueries({ queryKey: [key] }) },
  })
}

/** Saves one LMS sign-in. The same email saves over the one that is there rather than adding a second. */
export const useSaveLmsAccount = () =>
  useMyMutation(['my-lms-accounts', 'delegations'], (body: { label: string; email: string; role: Role; password: string; active: boolean }) =>
    api<MyLmsAccount>('/api/v1/me/lms-accounts', send('POST', body)),
  )

/** Which sign-in your classes go up under. */
export const useUseLmsAccount = () =>
  useMyMutation(['my-lms-accounts', 'delegations'], (id: string) =>
    api<MyLmsAccount>(`/api/v1/me/lms-accounts/${id}/use`, send('POST')),
  )

export const useDeleteLmsAccount = () =>
  useMyMutation(['my-lms-accounts', 'delegations'], (id: string) =>
    api<{ status: string }>(`/api/v1/me/lms-accounts/${id}`, send('DELETE')),
  )

/** The Zoom accounts you host with, and whether a password is kept for each. */
export function useMyZoomAccounts() {
  return useQuery({
    queryKey: ['my-zoom-accounts'],
    queryFn: () => api<{ accounts: MyZoomAccount[] }>('/api/v1/me/zoom-accounts'),
    refetchInterval: REFRESH.groups,
  })
}

/**
 * Saves the whole set of Zoom accounts, which is what the endpoint takes: an account left out of
 * the list is removed. Every page that sends this therefore sends the list it is showing, not one
 * account on its own.
 */
export const useSaveZoomAccounts = () =>
  useMyMutation(['my-zoom-accounts', 'delegations', 'run-plan'], (accounts: ZoomAccountInput[]) =>
    api<{ accounts: MyZoomAccount[] }>('/api/v1/me/zoom-accounts', send('PUT', { accounts })),
  )

/** The classes that open by themselves, as your machines keep them. */
export function useSchedules() {
  return useQuery({
    queryKey: ['my-schedules'],
    queryFn: () => api<SchedulesAnswer>('/api/v1/me/schedules'),
    refetchInterval: REFRESH.groups,
  })
}

/** Saves the whole timetable, like the Zoom accounts: what is sent is what is kept. */
export const useSaveSchedules = () =>
  useMyMutation(['my-schedules'], ({ schedules, deviceName }: { schedules: ScheduleRow[]; deviceName?: string | null }) =>
    api<{ count: number; updatedAt: string }>('/api/v1/me/schedules', send('PUT', { schedules, deviceName: deviceName ?? null })),
  )

/** What the machines have done, newest first. A coordinator sees their own groups only. */
export function useActivity(params: { group?: string; limit?: number } = {}) {
  return useQuery({
    queryKey: ['activity', params.group ?? '', params.limit ?? 100],
    queryFn: () => api<{ items: ActivityItem[] }>(`/api/v1/dashboard/activity${query({ group: params.group, limit: params.limit })}`),
    refetchInterval: REFRESH.overview,
    placeholderData: keepPreviousData,
  })
}

/** One shared setting. Any signed-in person may read one; only the admin may save one. */
export function useSetting<T>(key: string, enabled = true) {
  return useQuery({
    queryKey: ['setting', key],
    queryFn: () => api<Setting<T>>(`/api/v1/settings/${key}`),
    refetchInterval: REFRESH.groups,
    enabled,
  })
}

export function useSaveSetting<T>(key: string) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (value: T) => api<Setting<T>>(`/api/v1/settings/${key}`, send('PUT', { value })),
    onSuccess: () => client.invalidateQueries({ queryKey: ['setting', key] }),
  })
}

/** The co-host profiles, which live in the same place for every machine. */
export const useSessionRoles = () => useSetting<{ profiles: SessionRoleProfile[] }>('sessionRoles')
export const useSaveSessionRoles = () => useSaveSetting<{ profiles: SessionRoleProfile[] }>('sessionRoles')

/** The switches the workers read before holding a class. */
export const useCloudPolicy = () => useSetting<CloudPolicy>('cloudPolicy')
export const useSaveCloudPolicy = () => useSaveSetting<CloudPolicy>('cloudPolicy')

/** Runs one stage of one class now instead of at its time. */
export const useRunStage = () => {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ planId, stage }: { planId: string; stage: string }) =>
      api<StageRun>(`/api/v1/admin/run-plan/${planId}/run`, send('POST', { stage })),
    onSuccess: () => { for (const key of ['run-plan', 'sessions', 'overview', 'activity']) client.invalidateQueries({ queryKey: [key] }) },
  })
}

/** Whether a class that needs somebody is said out loud, and where. Never the webhook itself. */
export function useNotifySettings(enabled = true) {
  return useQuery({
    queryKey: ['notifications'],
    queryFn: () => api<NotifySettings>('/api/v1/dashboard/notifications'),
    refetchInterval: REFRESH.groups,
    enabled,
  })
}

export function useSaveNotifySettings() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (body: { enabled?: boolean; url?: string; label?: string }) =>
      api<NotifySettings>('/api/v1/dashboard/notifications', send('PUT', body)),
    onSuccess: () => client.invalidateQueries({ queryKey: ['notifications'] }),
  })
}

/** Sends one now and waits for the answer, so the page can say whether it arrived. */
export function useTestNotification() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: () => api<NotifyTest>('/api/v1/dashboard/notifications/test', send('POST')),
    onSuccess: () => client.invalidateQueries({ queryKey: ['notifications'] }),
  })
}

/** Whether attendance matching can ask an AI, and which model answers. Never the key. */
export function useAiStatus(enabled = true) {
  return useQuery({
    queryKey: ['ai'],
    queryFn: () => api<AiStatus>('/api/v1/dashboard/ai'),
    refetchInterval: REFRESH.groups,
    enabled,
  })
}

/** A token for a machine of yours to join with. It is single-use and short-lived. */
export const useEnrollDevice = () =>
  useMutation({
    mutationFn: (name: string) => api<Enrollment>('/api/v1/me/devices/enroll', send('POST', name ? { name } : undefined)),
  })
