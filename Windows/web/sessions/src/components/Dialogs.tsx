import { useEffect, useRef, useState } from 'react'
import type { Account, Action, Row, State } from '../types'
import { ACTIONS, shortGroup } from '../logic'
import { Icon } from './Icon'

/** Every LMS change is confirmed first: what it does, to which class. */
export function ConfirmDialog({ row, action, onConfirm, onCancel }: { row: Row; action: Action; onConfirm: (link?: string) => void; onCancel: () => void }) {
  const info = ACTIONS[action]
  const [link, setLink] = useState('')
  const first = useRef<HTMLButtonElement | HTMLInputElement>(null)
  useEffect(() => {
    first.current?.focus()
    const key = (e: KeyboardEvent) => e.key === 'Escape' && onCancel()
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [onCancel])
  const linkOk = /^https:\/\/(drive\.google\.com\/(file\/d\/|open\?id=)|([a-z0-9-]+\.)?zoom\.us\/rec\/)/i.test(link.trim())
  return (
    <div className="scrim" onMouseDown={(e) => e.target === e.currentTarget && onCancel()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="confirm-title">
        <span className="dialog-badge"><Icon name="bolt" size={14} /> Real change on the LMS</span>
        <h2 id="confirm-title">{info.label}</h2>
        <p className="dialog-class"><b>{shortGroup(row.group)}</b> · {row.title} · {row.date} {row.start}</p>
        <p>{info.explain}</p>
        {action === 'link' && (
          <label className="field">
            <span>Recording link</span>
            <input ref={first as React.RefObject<HTMLInputElement>} value={link} onChange={(e) => setLink(e.target.value)} placeholder="https://drive.google.com/file/d/… or https://zoom.us/rec/share/…" />
          </label>
        )}
        <div className="dialog-actions">
          <button type="button" className="btn ghost" onClick={onCancel}>Cancel</button>
          <button type="button" className="btn primary" ref={action === 'link' ? undefined : (first as React.RefObject<HTMLButtonElement>)}
            disabled={action === 'link' && !linkOk} onClick={() => onConfirm(action === 'link' ? link.trim() : undefined)}>
            {info.label}
          </button>
        </div>
      </div>
    </div>
  )
}

interface SettingsProps {
  state: State
  onClose: () => void
  chooseTrack: (track: string) => Promise<void>
  saveSheet: (url: string, tabs: Record<string, string>) => Promise<void>
  useAccount: (id: string) => Promise<void>
  removeAccount: (id: string) => Promise<void>
  saveAccount: (label: string, email: string, role: string, password: string, makeActive: boolean) => Promise<void>
}

/** The recordings sheet, and the LMS accounts the app signs in with. */
export function SettingsPanel({ state, onClose, chooseTrack, saveSheet, useAccount, removeAccount, saveAccount }: SettingsProps) {
  const [url, setUrl] = useState(state.sheet.url)
  const [tabs, setTabs] = useState<Record<string, string>>(state.sheet.tabs)
  const [label, setLabel] = useState('')
  const [email, setEmail] = useState('')
  const [role, setRole] = useState('coordinator')
  const [password, setPassword] = useState('')
  useEffect(() => {
    const key = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [onClose])
  const add = async (makeActive: boolean) => {
    await saveAccount(label, email, role, password, makeActive)
    setPassword('')
    setLabel('')
    setEmail('')
  }
  return (
    <div className="scrim drawer-scrim" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <aside className="drawer" role="dialog" aria-modal="true" aria-label="Sessions settings">
        <header className="drawer-head">
          <h2>Settings</h2>
          <button type="button" className="btn icon" aria-label="Close" onClick={onClose}><Icon name="close" size={18} /></button>
        </header>

        <section>
          <h3><Icon name="sheet" size={16} /> Recordings sheet</h3>
          <p className="muted">The Google Sheet n8n reads (shared "anyone with the link"). The app looks each finished class up in it and puts its Drive link on the LMS — by the button, and by itself every 30 minutes.</p>
          <label className="field"><span>Sheet link</span>
            <input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://docs.google.com/spreadsheets/d/…/edit" /></label>
          <details>
            <summary>A group's tab is not named after the group?</summary>
            <p className="muted">Paste that tab's own link (it ends in <code>#gid=…</code>).</p>
            {state.sheet.groups.map((g) => (
              <label className="field" key={g}><span>{g}</span>
                <input value={tabs[g] ?? ''} onChange={(e) => setTabs({ ...tabs, [g]: e.target.value })} placeholder="Tab named after the group" /></label>
            ))}
          </details>
          <button type="button" className="btn primary" onClick={() => saveSheet(url, tabs)}>Save sheet</button>
        </section>

        <section>
          <h3><Icon name="sheet" size={16} /> Material folders</h3>
          <p className="muted">Freelancing, Soft Skills and English go up by themselves at class time: a class's number in its track (by the timetable) picks its "Session N" folder or file. A technical class gets the folder you choose on its card.</p>
          <ul className="accounts">
            {(state.materials?.tracks ?? []).map((t) => (
              <li key={t.track}>
                <div><b>{t.track}</b><span title={t.folder}>{t.folder || 'Not set'}</span></div>
                <button type="button" className="btn small" onClick={() => chooseTrack(t.track)}>{t.folder ? 'Change…' : 'Choose…'}</button>
              </li>
            ))}
          </ul>
        </section>

        <section>
          <h3><Icon name="user" size={16} /> LMS accounts</h3>
          <p className="muted">The account the app signs in to the LMS with. Passwords go to Windows Credential Manager.</p>
          <ul className="accounts">
            {state.accounts.map((a: Account) => (
              <li key={a.id} className={a.active ? 'active' : ''}>
                <div><b>{a.label}</b><span>{a.email} · {a.role}</span></div>
                {a.active ? <span className="tag">In use</span> : <button type="button" className="btn small" onClick={() => useAccount(a.id)}>Use</button>}
                {!a.active && <button type="button" className="btn small ghost danger" onClick={() => removeAccount(a.id)}>Remove</button>}
              </li>
            ))}
          </ul>
          <div className="form-grid">
            <label className="field"><span>Label</span><input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="Admin" /></label>
            <label className="field"><span>Role</span>
              <select value={role} onChange={(e) => setRole(e.target.value)}><option value="coordinator">coordinator</option><option value="admin">admin</option></select></label>
            <label className="field wide"><span>Email</span><input type="email" autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
            <label className="field wide"><span>Password</span><input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          </div>
          <div className="row-actions">
            <button type="button" className="btn" disabled={!email || !password} onClick={() => add(false)}>Save</button>
            <button type="button" className="btn primary" disabled={!email || !password} onClick={() => add(true)}>Save and use</button>
          </div>
        </section>
      </aside>
    </div>
  )
}

/**
 * A class's assignment: its title and its deadline, picked on a calendar and a clock. It is
 * created on the LMS with the class's material; "Save and create now" does it at once.
 */
export interface AssignmentDraft { title: string; deadline: string; description: string; file: string }

export function AssignmentDialog({ row, onSave, onCancel, pickFile }: {
  row: Row
  onSave: (draft: AssignmentDraft, none: boolean, createNow: boolean) => void
  onCancel: () => void
  /** Choose the assignment's own file on this PC; null when nothing was chosen. */
  pickFile: () => Promise<{ path: string; name: string } | null>
}) {
  const material = row.material
  // A week after the class unless one was chosen (technical classes have no default).
  const fallback = (() => {
    const d = new Date(`${row.date}T${row.start}:00`)
    d.setDate(d.getDate() + 7)
    const pad = (n: number) => String(n).padStart(2, '0')
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
  })()
  const initial = material?.deadline ?? (material?.technical ? '' : fallback)
  const [title, setTitle] = useState(material?.assignmentTitle ?? '')
  const [day, setDay] = useState(initial.slice(0, 10))
  const [time, setTime] = useState(initial.slice(11, 16) || row.start)
  const [description, setDescription] = useState(material?.description ?? '')
  const [file, setFile] = useState(material?.assignmentFile ?? '')
  const fileName = file ? file.split(/[\\/]/).pop() : ''
  useEffect(() => {
    const key = (e: KeyboardEvent) => e.key === 'Escape' && onCancel()
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [onCancel])
  const ready = title.trim().length > 0 && /^\d{4}-\d{2}-\d{2}$/.test(day) && /^\d{2}:\d{2}$/.test(time)
  const deadline = `${day}T${time}`
  const past = ready && new Date(deadline) < new Date(`${row.date}T${row.start}:00`)
  const draft = (): AssignmentDraft => ({ title: title.trim(), deadline, description: description.trim(), file })
  return (
    <div className="scrim" onMouseDown={(e) => e.target === e.currentTarget && onCancel()}>
      <form className="dialog" role="dialog" aria-modal="true" aria-labelledby="assignment-title"
        onSubmit={(e) => { e.preventDefault(); if (ready) onSave(draft(), false, false) }}>
        <span className="dialog-badge"><Icon name="calendar" size={14} /> Assignment</span>
        <h2 id="assignment-title">{shortGroup(row.group)} · {row.title}</h2>
        <p className="dialog-class">{row.date} {row.start}{material?.track ? ` · ${material.track}${material.number ? ` ${material.number}` : ''}` : ''}</p>
        <label className="field"><span>Title</span>
          <input autoFocus value={title} onChange={(e) => setTitle(e.target.value)} placeholder="The assignment's title on the LMS" /></label>
        <label className="field"><span>Description</span>
          <textarea rows={2} value={description} onChange={(e) => setDescription(e.target.value)}
            placeholder="The LMS needs one; left empty, it says the assignment is in the session's attached file." /></label>
        <div className="field">
          <span>Its file (optional) — goes up as an attachment with the assignment</span>
          <div className="file-pick">
            <button type="button" className="btn small" onClick={async () => {
              const picked = await pickFile()
              if (picked) { setFile(picked.path); if (!title.trim()) setTitle(picked.name) }
            }}><Icon name="sheet" size={13} /> {file ? 'Change file…' : 'Choose file…'}</button>
            {file && <span className="file-name" title={file}>{fileName}</span>}
            {file && <button type="button" className="btn small ghost" onClick={() => setFile('')}>Remove</button>}
          </div>
        </div>
        <div className="form-grid">
          <label className="field"><span>Deadline day</span><input type="date" value={day} onChange={(e) => setDay(e.target.value)} /></label>
          <label className="field"><span>Time</span><input type="time" value={time} onChange={(e) => setTime(e.target.value)} /></label>
        </div>
        {!material?.technical && <p className="muted">A week after the class unless you change it.</p>}
        {past && <p className="warn-line"><Icon name="alert" size={13} /> That deadline is before the class.</p>}
        <div className="dialog-actions spread">
          <button type="button" className="btn ghost danger" onClick={() => onSave({ title: '', deadline: '', description: '', file: '' }, true, false)}>No assignment</button>
          <span className="grow" />
          <button type="button" className="btn ghost" onClick={onCancel}>Cancel</button>
          <button type="submit" className="btn" disabled={!ready}>Save</button>
          <button type="button" className="btn primary" disabled={!ready} onClick={() => onSave(draft(), false, true)}>Save and create now</button>
        </div>
      </form>
    </div>
  )
}

export interface Toast { id: number; ok: boolean; text: string }

export function Toasts({ toasts, dismiss }: { toasts: Toast[]; dismiss: (id: number) => void }) {
  return (
    <div className="toasts" role="status" aria-live="polite">
      {toasts.map((t) => (
        <div key={t.id} className={`toast ${t.ok ? 'ok' : 'bad'}`}>
          <Icon name={t.ok ? 'check' : 'alert'} size={16} />
          <span>{t.text}</span>
          <button type="button" aria-label="Dismiss" onClick={() => dismiss(t.id)}><Icon name="close" size={14} /></button>
        </div>
      ))}
    </div>
  )
}
