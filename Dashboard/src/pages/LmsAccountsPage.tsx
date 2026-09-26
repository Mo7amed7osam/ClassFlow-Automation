import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { useDeleteLmsAccount, useMe, useMyLmsAccounts, useSaveLmsAccount, useUseLmsAccount } from '../api/hooks'
import type { MyLmsAccount, Role } from '../api/types'
import { PageHeader } from '../components/Layout'
import { useToast } from '../components/Toast'
import { button, Card, EmptyState, ErrorBanner, input, LoadingRows, Pill, Spinner, td, th, TimeAgo } from '../components/ui'
import { Field } from './ZoomAccountsPage'

/**
 * Your LMS sign-ins - the app's LMS page, on the web.
 *
 * Every class is written up on the LMS under a name: Run Session, the attendance, the late joiners,
 * the recording. This is that name and its password. One sign-in is the one in use, and that is the
 * one a machine holding your class signs in with; the password is sealed on the server and handed
 * only to the machine running the class, never back to this page.
 */
export function LmsAccountsPage() {
  const { data: me } = useMe()
  const { data, isLoading, error } = useMyLmsAccounts()
  const save = useSaveLmsAccount()
  const use = useUseLmsAccount()
  const remove = useDeleteLmsAccount()
  const toast = useToast()
  const accounts = data?.accounts ?? []

  const [open, setOpen] = useState(false)
  const [email, setEmail] = useState('')
  const [label, setLabel] = useState('')
  const [role, setRole] = useState<Role>('coordinator')
  const [password, setPassword] = useState('')
  const [active, setActive] = useState(true)
  const [submitted, setSubmitted] = useState(false)

  const known = accounts.find((account) => account.email.toLowerCase() === email.trim().toLowerCase()) ?? null
  const problem =
    !email.includes('@') || /\s/.test(email.trim()) ? 'That is not an email address.'
      : !password ? 'The LMS password is required: a machine cannot type one in for you.'
        : null

  function reset() {
    setOpen(false); setSubmitted(false); setEmail(''); setLabel(''); setPassword(''); setRole('coordinator'); setActive(true)
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (problem) return
    save.mutate({ label: label.trim(), email: email.trim(), role, password, active }, {
      onSuccess: (account) => {
        reset()
        toast.success(`${account.email} saved.`, known ? 'The password for it was replaced.' : 'Your classes can be written up under it.')
      },
    })
  }

  function useThis(account: MyLmsAccount) {
    use.mutate(account.id, { onSuccess: () => toast.success(`Your classes go up as ${account.email}.`) })
  }

  function deleteThis(account: MyLmsAccount) {
    if (!window.confirm(`Remove ${account.email}? Classes waiting to be written up under it will have no sign-in.`)) return
    remove.mutate(account.id, { onSuccess: () => toast.success(`${account.email} removed.`) })
  }

  const busy = save.isPending || use.isPending || remove.isPending
  const failed = [save.error, use.error, remove.error].find(Boolean)
  const message = failed instanceof ApiError && typeof failed.details === 'string' ? failed.details : failed?.message

  return (
    <>
      <PageHeader
        title="LMS sign-ins"
        description="The name your classes are written up under on the LMS: Run Session, the attendance, the recording."
        action={<button type="button" className={button.primary} onClick={() => setOpen(true)}>Add a sign-in</button>}
      />

      {failed && <p role="alert" className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{message}</p>}
      {data && !data.canKeepPasswords && (
        <p role="alert" className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
          This server has no encryption key set, so it cannot keep passwords. Ask the admin to set one before adding a sign-in.
        </p>
      )}

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <Card title={`${accounts.length} sign-in${accounts.length === 1 ? '' : 's'}`}>
          {error ? <ErrorBanner error={error} /> : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[34rem]">
                <thead className="border-b border-slate-100 dark:border-slate-800/80 bg-slate-50/60 dark:bg-[#0c111d]">
                  <tr>
                    <th className={th}>Email</th>
                    <th className={th}>Name</th>
                    <th className={th}>Role on the LMS</th>
                    <th className={th}>Saved</th>
                    <th className={th}><span className="sr-only">Actions</span></th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-slate-800/80">
                  {isLoading && <LoadingRows columns={5} />}
                  {!isLoading && accounts.length === 0 && (
                    <tr><td colSpan={5}><EmptyState>No LMS sign-in yet. Your classes cannot be written up until there is one.</EmptyState></td></tr>
                  )}
                  {accounts.map((account) => (
                    <tr key={account.id} className="hover:bg-slate-50/60 dark:hover:bg-[#161d2f]/40 transition-colors">
                      <td className={td}>
                        <p className="font-semibold text-slate-900 dark:text-slate-100">{account.email}</p>
                        {account.active && <Pill tone="green">In use</Pill>}
                      </td>
                      <td className={td}>{account.label || <span className="text-slate-400">—</span>}</td>
                      <td className={td}>{account.role === 'admin' ? 'Admin' : 'Coordinator'}</td>
                      <td className={`${td} text-slate-500 dark:text-slate-400`}><TimeAgo iso={account.updatedAt} /></td>
                      <td className={`${td} text-right`}>
                        <span className="inline-flex gap-1.5">
                          {!account.active && (
                            <button type="button" className={button.small} onClick={() => useThis(account)} disabled={busy} aria-label={`Use ${account.email}`}>Use</button>
                          )}
                          <button type="button" className={button.small} onClick={() => { setOpen(true); setEmail(account.email); setLabel(account.label); setRole(account.role); setActive(account.active); setPassword('') }} aria-label={`Change the password of ${account.email}`}>
                            New password
                          </button>
                          <button type="button" className={button.smallDanger} onClick={() => deleteThis(account)} disabled={busy} aria-label={`Remove ${account.email}`}>Remove</button>
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        {open && (
          <Card title={known ? `New password for ${known.email}` : 'New LMS sign-in'}>
            <form onSubmit={submit} noValidate aria-label="LMS sign-in" className="flex flex-col gap-4 px-5 py-4">
              <Field label="Email" hint="The address you sign in to the LMS with.">
                <input className={`${input} w-full`} type="email" autoComplete="username" value={email} maxLength={320} onChange={(e) => setEmail(e.target.value)} />
              </Field>
              <Field label="A name for it" hint={`Shown in lists. Leave empty for "${role === 'admin' ? 'Admin' : 'Coordinator'}".`}>
                <input className={`${input} w-full`} value={label} maxLength={100} onChange={(e) => setLabel(e.target.value)} />
              </Field>
              <Field label="Role on the LMS" hint="What this sign-in can do there. Most coordinators are Coordinator.">
                <select className={`${input} w-full`} value={role} onChange={(e) => setRole(e.target.value as Role)}>
                  <option value="coordinator">Coordinator</option>
                  <option value="admin">Admin</option>
                </select>
              </Field>
              <Field label="Password" hint="Kept sealed and given only to the machine running your class. It is never shown again.">
                <input className={`${input} w-full`} type="password" autoComplete="new-password" value={password} maxLength={500} onChange={(e) => setPassword(e.target.value)} />
              </Field>
              <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                <input type="checkbox" className="size-4 rounded border-slate-300 accent-indigo-600" checked={active} onChange={(e) => setActive(e.target.checked)} />
                Write my classes up under this one
              </label>
              {submitted && problem && <p className="text-xs text-rose-700">{problem}</p>}
              <div className="flex gap-2">
                <button type="submit" className={button.primary} disabled={save.isPending || data?.canKeepPasswords === false}>
                  {save.isPending && <Spinner label="Saving" />}
                  {save.isPending ? 'Saving…' : 'Save'}
                </button>
                <button type="button" className={button.secondary} onClick={reset}>Cancel</button>
              </div>
            </form>
          </Card>
        )}
      </div>

      {me?.role === 'admin' && (
        <p className="mt-4 text-sm text-slate-500">
          These are your own sign-ins. Whose classes this workspace runs, and which of their sign-ins each goes up
          under, is on <span className="font-medium text-slate-700">Run classes</span>.
        </p>
      )}
    </>
  )
}
