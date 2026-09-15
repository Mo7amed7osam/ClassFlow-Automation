import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { api, query, UnauthorizedError } from './client'
import type {
  AdminGroup,
  Agent,
  AttachOptions,
  AttachResult,
  CancelResult,
  EditResult,
  GroupSummary,
  JobSummary,
  Me,
  Overview,
  Recording,
  RecordingChanges,
  RecordingDetails,
  RecordingPage,
  RecordingQuery,
  User,
  UserList,
  UserStatus,
} from './types'

// How often each view refreshes itself. Agents change fastest (heartbeats every 30 s); a recording
// with a job in flight is watched closely until the agent reports back.
export const REFRESH = { agents: 10_000, overview: 15_000, recordings: 30_000, groups: 60_000, activeJob: 5_000 }

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
