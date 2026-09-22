import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { useDeleteLmsAccount, useLmsAccounts, useSaveLmsAccount, useSaveZoomAccounts, useUseLmsAccount, useZoomAccounts } from '../api/hooks'
import type { LmsAccountRef, ZoomAccountRef } from '../api/types'
import { PageHeader } from '../components/Layout'
import { useToast } from '../components/Toast'
import { button, Card, input, Pill, Spinner } from '../components/ui'

const errorText = (error: unknown): string => error instanceof ApiError
  ? (typeof error.details === 'string' ? error.details : error.message)
  : error instanceof Error ? error.message : 'Could not save the account.'

/** The signed-in coordinator's own LMS and Zoom accounts. Admins can also use it for their account. */
export function AccountsPage() {
  const lms = useLmsAccounts()
  const zoom = useZoomAccounts()
  const saveLms = useSaveLmsAccount()
  const useLms = useUseLmsAccount()
  const deleteLms = useDeleteLmsAccount()
  const saveZoom = useSaveZoomAccounts()
  const toast = useToast()
  const [lmsForm, setLmsForm] = useState({ label: '', email: '', password: '', role: 'coordinator' as 'admin' | 'coordinator' })
  const [zoomForm, setZoomForm] = useState({ accountId: '', label: '', zoomEmail: '', group: '', meetingUrl: '', password: '', preferredEngine: 'web' as 'desktop' | 'web' })
  const [editingLms, setEditingLms] = useState<string | null>(null)
  const [editingZoom, setEditingZoom] = useState<string | null>(null)

  function submitLms(event: FormEvent) {
    event.preventDefault()
    saveLms.mutate({ ...lmsForm, active: true }, {
      onSuccess: () => {
        setLmsForm({ label: '', email: '', password: '', role: 'coordinator' })
        setEditingLms(null)
        toast.success('LMS account saved', 'It is now the active LMS sign-in for this coordinator.')
      },
    })
  }

  function submitZoom(event: FormEvent) {
    event.preventDefault()
    const current = zoom.data?.accounts ?? []
    const next = [...current.filter((account) => account.id !== editingZoom).map(toZoomSave), {
      ...zoomForm,
      zoomEmail: zoomForm.zoomEmail || null,
      group: zoomForm.group || null,
      meetingUrl: zoomForm.meetingUrl || null,
      password: zoomForm.password || null,
      active: true,
    }]
    saveZoom.mutate(next, {
      onSuccess: () => {
        setZoomForm({ accountId: '', label: '', zoomEmail: '', group: '', meetingUrl: '', password: '', preferredEngine: 'web' })
        setEditingZoom(null)
        toast.success('Zoom account saved', 'It can now be selected for that group in Run classes.')
      },
    })
  }

  function editLms(account: LmsAccountRef) {
    setEditingLms(account.id)
    setLmsForm({ label: account.label, email: account.email, password: '', role: account.role })
  }

  function editZoom(account: ZoomAccountRef) {
    setEditingZoom(account.id)
    setZoomForm({ accountId: account.accountId, label: account.label, zoomEmail: account.zoomEmail ?? '', group: account.group ?? '', meetingUrl: account.meetingUrl ?? '', password: '', preferredEngine: account.preferredEngine ?? 'web' })
  }

  function removeZoom(account: ZoomAccountRef) {
    if (!window.confirm(`Remove Zoom account “${account.label}”?`)) return
    saveZoom.mutate((zoom.data?.accounts ?? []).filter((item) => item.id !== account.id).map(toZoomSave), {
      onSuccess: () => toast.success('Zoom account removed'),
    })
  }

  return (
    <>
      <PageHeader title="Accounts" description="Add the LMS and Zoom accounts used when this coordinator’s classes run." />
      <div className="grid gap-5 xl:grid-cols-2">
        <Card title="LMS accounts">
          <form onSubmit={submitLms} className="grid gap-3 px-5 py-4" aria-label="Add LMS account">
            <p className="text-sm text-slate-500">The password is encrypted before it is stored and is never shown in this dashboard.</p>
            <label className="grid gap-1 text-sm font-medium text-slate-700">Label
              <input className={input} value={lmsForm.label} onChange={(e) => setLmsForm({ ...lmsForm, label: e.target.value })} placeholder="Coordinator" maxLength={100} />
            </label>
            <label className="grid gap-1 text-sm font-medium text-slate-700">LMS email
              <input required type="email" className={input} value={lmsForm.email} onChange={(e) => setLmsForm({ ...lmsForm, email: e.target.value })} maxLength={320} />
            </label>
            <label className="grid gap-1 text-sm font-medium text-slate-700">LMS password
              <input required type="password" className={input} value={lmsForm.password} onChange={(e) => setLmsForm({ ...lmsForm, password: e.target.value })} maxLength={500} />
            </label>
            <button className={button.primary} disabled={saveLms.isPending}>{saveLms.isPending && <Spinner label="Saving" />}{saveLms.isPending ? 'Saving…' : editingLms ? 'Update LMS account' : 'Save LMS account'}</button>
            {editingLms && <button type="button" className={button.secondary} onClick={() => { setEditingLms(null); setLmsForm({ label: '', email: '', password: '', role: 'coordinator' }) }}>Cancel edit</button>}
            {saveLms.isError && <p role="alert" className="text-sm text-rose-700">{errorText(saveLms.error)}</p>}
          </form>
          <div className="border-t border-slate-100 px-5 py-4">
            {lms.isLoading ? <Spinner /> : lms.data?.accounts.length ? <ul className="space-y-2">{lms.data.accounts.map((account) => <li key={account.id} className="flex items-center justify-between gap-3 text-sm"><span><span className="font-medium text-slate-800">{account.label}</span><span className="block text-slate-500">{account.email}</span></span><span className="flex shrink-0 items-center gap-2">{account.active ? <Pill tone="green">Active</Pill> : <button className={button.small} onClick={() => useLms.mutate(account.id)}>Use</button>}<button className={button.small} onClick={() => editLms(account)}>Edit</button><button className={button.smallDanger} onClick={() => { if (window.confirm(`Remove LMS account “${account.label}”?`)) deleteLms.mutate(account.id, { onSuccess: () => toast.success('LMS account removed') }) }}>Remove</button></span></li>)}</ul> : <p className="text-sm text-slate-500">No LMS accounts yet.</p>}
          </div>
        </Card>

        <Card title="Zoom accounts">
          <form onSubmit={submitZoom} className="grid gap-3 px-5 py-4" aria-label="Add Zoom account">
            <p className="text-sm text-slate-500">Add one account per group when different Zoom hosts run the classes.</p>
            <label className="grid gap-1 text-sm font-medium text-slate-700">Account ID
              <input required className={input} value={zoomForm.accountId} onChange={(e) => setZoomForm({ ...zoomForm, accountId: e.target.value })} placeholder="CAI5_AIS4_S7" maxLength={100} />
            </label>
            <label className="grid gap-1 text-sm font-medium text-slate-700">Label
              <input className={input} value={zoomForm.label} onChange={(e) => setZoomForm({ ...zoomForm, label: e.target.value })} placeholder="Grade 7 host" maxLength={100} />
            </label>
            <label className="grid gap-1 text-sm font-medium text-slate-700">Zoom email <span className="font-normal text-slate-400">(optional)</span>
              <input type="email" className={input} value={zoomForm.zoomEmail} onChange={(e) => setZoomForm({ ...zoomForm, zoomEmail: e.target.value })} maxLength={320} />
            </label>
            <label className="grid gap-1 text-sm font-medium text-slate-700">Group
              <input className={input} value={zoomForm.group} onChange={(e) => setZoomForm({ ...zoomForm, group: e.target.value })} placeholder="CAI5_AIS4_S7" maxLength={100} />
            </label>
            <label className="grid gap-1 text-sm font-medium text-slate-700">Meeting link <span className="font-normal text-slate-400">(optional)</span>
              <input type="url" className={input} value={zoomForm.meetingUrl} onChange={(e) => setZoomForm({ ...zoomForm, meetingUrl: e.target.value })} placeholder="https://…" />
            </label>
            <label className="grid gap-1 text-sm font-medium text-slate-700">Zoom password <span className="font-normal text-slate-400">(optional)</span>
              <input type="password" className={input} value={zoomForm.password} onChange={(e) => setZoomForm({ ...zoomForm, password: e.target.value })} maxLength={500} />
            </label>
            <button className={button.primary} disabled={saveZoom.isPending}>{saveZoom.isPending && <Spinner label="Saving" />}{saveZoom.isPending ? 'Saving…' : editingZoom ? 'Update Zoom account' : 'Save Zoom account'}</button>
            {editingZoom && <button type="button" className={button.secondary} onClick={() => { setEditingZoom(null); setZoomForm({ accountId: '', label: '', zoomEmail: '', group: '', meetingUrl: '', password: '', preferredEngine: 'web' }) }}>Cancel edit</button>}
            {saveZoom.isError && <p role="alert" className="text-sm text-rose-700">{errorText(saveZoom.error)}</p>}
          </form>
          <div className="border-t border-slate-100 px-5 py-4">
            {zoom.isLoading ? <Spinner /> : zoom.data?.accounts.length ? <ul className="space-y-2">{zoom.data.accounts.map((account) => <li key={account.id} className="flex items-center justify-between gap-3 text-sm"><span><span className="font-medium text-slate-800">{account.label}</span><span className="block text-slate-500">{account.accountId}{account.group ? ` · ${account.group}` : ''}</span></span><span className="flex shrink-0 gap-2"><button className={button.small} onClick={() => editZoom(account)}>Edit</button><button className={button.smallDanger} onClick={() => removeZoom(account)}>Remove</button></span></li>)}</ul> : <p className="text-sm text-slate-500">No Zoom accounts yet.</p>}
          </div>
        </Card>
      </div>
    </>
  )
}

function toZoomSave({ accountId, label, zoomEmail, group, meetingUrl, preferredEngine, active }: ZoomAccountRef) {
  return { accountId, label, zoomEmail, group, meetingUrl, preferredEngine, active, password: null }
}
