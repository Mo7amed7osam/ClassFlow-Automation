import { useState } from 'react'
import { useSearchParams } from 'react-router'
import { useReviewUser, useUpdateUser, useUsers } from '../api/hooks'
import type { User, UserStatus } from '../api/types'
import { PageHeader } from '../components/Layout'
import { useToast } from '../components/Toast'
import { AssignGroupsModal, ConfirmModal, CreateUserModal, reason, ResetPasswordModal } from '../components/UserModals'
import { button, Card, EmptyState, ErrorBanner, LoadingRows, Pill, TimeAgo, td, th, type Tone } from '../components/ui'

const STATUS: Record<UserStatus, { tone: Tone; label: string }> = {
  pending: { tone: 'amber', label: 'Waiting for approval' },
  active: { tone: 'green', label: 'Active' },
  disabled: { tone: 'slate', label: 'Disabled' },
  rejected: { tone: 'red', label: 'Rejected' },
}

const FILTERS: { value: UserStatus | ''; label: string }[] = [
  { value: '', label: 'All' },
  { value: 'pending', label: 'Pending' },
  { value: 'active', label: 'Active' },
  { value: 'disabled', label: 'Disabled' },
  { value: 'rejected', label: 'Rejected' },
]

const COLUMNS = ['Coordinator', 'Status', 'Groups', 'Last sign-in', 'Actions']

type Dialog =
  | { kind: 'create' }
  | { kind: 'groups'; user: User }
  | { kind: 'password'; user: User }
  | { kind: 'disable'; user: User }
  | { kind: 'reject'; user: User }

/** The admin's list of accounts: approve registrations, create coordinators, give out groups. */
export function UsersPage() {
  const [params, setParams] = useSearchParams()
  const status = (FILTERS.find((f) => f.value === params.get('status'))?.value ?? '') as UserStatus | ''
  const users = useUsers(status)
  const review = useReviewUser()
  const update = useUpdateUser()
  const toast = useToast()
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const counts = users.data?.counts
  const close = () => {
    setDialog(null)
    review.reset()
    update.reset()
  }

  function approve(user: User) {
    review.mutate({ id: user.id, decision: 'approve' }, {
      onSuccess: (approved) => {
        toast.success(`${approved.displayName} can sign in now`, 'Choose the groups they will see.')
        setDialog({ kind: 'groups', user: approved })               // the next thing an approval needs
      },
      onError: (error) => toast.error('Could not approve', reason(error)),
    })
  }

  function enable(user: User) {
    update.mutate({ id: user.id, status: 'active' }, {
      onSuccess: () => toast.success(`${user.displayName} is active again`),
      onError: (error) => toast.error('Could not enable', reason(error)),
    })
  }

  const items = users.data?.users
  const pending = items?.filter((u) => u.status === 'pending') ?? []

  return (
    <>
      <PageHeader
        title="Users"
        description="Coordinators see only the groups you give them. Nobody can sign in until you approve them."
        action={<button type="button" className={button.primary} onClick={() => setDialog({ kind: 'create' })}>New coordinator</button>}
      />

      {status === '' && pending.length > 0 && (
        <Card title={`Waiting for approval (${pending.length})`} className="mb-5 border-amber-300">
          <ul className="divide-y divide-slate-100">
            {pending.map((user) => (
              <li key={user.id} className="flex flex-wrap items-center justify-between gap-3 px-5 py-3">
                <div>
                  <p className="text-sm font-medium text-slate-900">{user.displayName}</p>
                  <p className="text-xs text-slate-500">{user.username} · asked <TimeAgo iso={user.createdAt} /></p>
                </div>
                <div className="flex gap-2">
                  <button type="button" className={button.smallPrimary} disabled={review.isPending} onClick={() => approve(user)} aria-label={`Approve ${user.username}`}>
                    Approve
                  </button>
                  <button type="button" className={button.small} onClick={() => setDialog({ kind: 'reject', user })} aria-label={`Reject ${user.username}`}>
                    Reject
                  </button>
                </div>
              </li>
            ))}
          </ul>
        </Card>
      )}

      <Card>
        <div className="flex flex-wrap gap-1.5 border-b border-slate-100 px-4 py-3" role="tablist" aria-label="Filter by status">
          {FILTERS.map((f) => {
            const selected = f.value === status
            const n = f.value && counts ? counts[f.value] : undefined
            return (
              <button
                key={f.value || 'all'}
                type="button"
                role="tab"
                aria-selected={selected}
                onClick={() => setParams(f.value ? { status: f.value } : {}, { replace: true })}
                className={`rounded-full px-3 py-1 text-sm ${selected ? 'bg-slate-900 text-white' : 'text-slate-600 hover:bg-slate-100'}`}
              >
                {f.label}
                {n ? <span className="ml-1 tabular-nums opacity-70">{n}</span> : null}
              </button>
            )
          })}
        </div>
        {users.error ? (
          <ErrorBanner error={users.error} />
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-100">
              <thead className="bg-slate-50/70">
                <tr>{COLUMNS.map((c) => <th key={c} scope="col" className={th}>{c}</th>)}</tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {users.isLoading ? (
                  <LoadingRows columns={COLUMNS.length} />
                ) : (
                  items?.map((user) => (
                    <tr key={user.id} className="hover:bg-slate-50/60">
                      <td className={td}>
                        <p className="font-medium text-slate-900">{user.displayName}</p>
                        <p className="text-xs text-slate-500">{user.username}</p>
                      </td>
                      <td className={td}>
                        {user.role === 'admin' ? <Pill tone="blue">Admin</Pill> : <Pill tone={STATUS[user.status].tone}>{STATUS[user.status].label}</Pill>}
                      </td>
                      <td className={`${td} max-w-sm`}>
                        {user.role === 'admin' ? (
                          <span className="text-slate-500">All groups</span>
                        ) : user.groups.length ? (
                          <div className="flex flex-wrap gap-1">
                            {user.groups.map((g) => <Pill key={g.id} tone={g.archived ? 'slate' : 'green'} title={g.archived ? 'Archived: not visible to them' : undefined}>{g.name}</Pill>)}
                          </div>
                        ) : (
                          <span className="text-slate-400">None</span>
                        )}
                      </td>
                      <td className={`${td} text-slate-600`}><TimeAgo iso={user.lastLoginAt} /></td>
                      <td className={td}>
                        {user.role === 'admin' ? (
                          <span className="text-xs text-slate-400">You</span>
                        ) : (
                          <div className="flex flex-wrap gap-1.5">
                            {user.status === 'pending' && (
                              <button type="button" className={button.smallPrimary} disabled={review.isPending} onClick={() => approve(user)}>Approve</button>
                            )}
                            {user.status === 'rejected' && (
                              <button type="button" className={button.small} disabled={review.isPending} onClick={() => approve(user)}>Approve after all</button>
                            )}
                            {(user.status === 'active' || user.status === 'disabled') && (
                              <button type="button" className={button.small} onClick={() => setDialog({ kind: 'groups', user })} aria-label={`Groups of ${user.username}`}>Groups</button>
                            )}
                            {user.status === 'active' && (
                              <>
                                <button type="button" className={button.small} onClick={() => setDialog({ kind: 'password', user })} aria-label={`New password for ${user.username}`}>Password</button>
                                <button type="button" className={button.small} onClick={() => setDialog({ kind: 'disable', user })} aria-label={`Disable ${user.username}`}>Disable</button>
                              </>
                            )}
                            {user.status === 'disabled' && (
                              <button type="button" className={button.small} disabled={update.isPending} onClick={() => enable(user)} aria-label={`Enable ${user.username}`}>Enable</button>
                            )}
                          </div>
                        )}
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
            {!users.isLoading && items?.length === 0 && <EmptyState>{status ? 'No accounts with this status.' : 'No accounts yet.'}</EmptyState>}
          </div>
        )}
      </Card>

      {dialog?.kind === 'create' && <CreateUserModal onClose={close} />}
      {dialog?.kind === 'groups' && <AssignGroupsModal user={dialog.user} onClose={close} />}
      {dialog?.kind === 'password' && <ResetPasswordModal user={dialog.user} onClose={close} />}
      {dialog?.kind === 'disable' && (
        <ConfirmModal
          title={`Disable ${dialog.user.displayName}?`}
          body={<p>They are signed out at once and cannot sign in until you enable the account again. Their groups are kept.</p>}
          confirm="Disable"
          danger
          busy={update.isPending}
          error={update.error}
          onClose={close}
          onConfirm={() => update.mutate({ id: dialog.user.id, status: 'disabled' }, {
            onSuccess: () => { toast.success(`${dialog.user.displayName} is disabled`); close() },
          })}
        />
      )}
      {dialog?.kind === 'reject' && (
        <ConfirmModal
          title={`Reject ${dialog.user.displayName}?`}
          body={<p>The request for <span className="font-medium">{dialog.user.username}</span> is refused; they cannot sign in. You can still approve it later.</p>}
          confirm="Reject"
          danger
          busy={review.isPending}
          error={review.error}
          onClose={close}
          onConfirm={() => review.mutate({ id: dialog.user.id, decision: 'reject' }, {
            onSuccess: () => { toast.success(`Request from ${dialog.user.username} rejected`); close() },
          })}
        />
      )}
    </>
  )
}
