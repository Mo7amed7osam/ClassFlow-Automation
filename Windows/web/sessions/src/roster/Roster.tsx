import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { call, inApp, onState } from '../bridge'
import { Icon } from '../components/Icon'
import { Toasts, type Toast } from '../components/Dialogs'
import { groupHue } from '../logic'
import type { Group, Reply, RosterState, Student } from './types'
import { demoRoster } from './demo'

// Groups & Students: each group's roster in its order. A group's students come from the LMS in one
// click (read from one of its sessions this month, nothing changed there), from a file, or by hand.

const rpc = <T,>(method: string, params: Record<string, unknown> = {}) => (inApp ? call<T>(method, params) : demoRoster.call<T>(method, params))
const remembered = () => { try { return localStorage.getItem('roster-group') ?? '' } catch { return '' } }
const remember = (id: string) => { try { localStorage.setItem('roster-group', id) } catch { /* private window */ } }

type Editing = { student: Student | null }
type Confirming = { title: string; text: string; action: string; run: () => Promise<void> }

export function Roster() {
  const [state, setState] = useState<RosterState | null>(null)
  const [selected, setSelected] = useState<string>(remembered())
  const [query, setQuery] = useState('')
  const [toasts, setToasts] = useState<Toast[]>([])
  const [editing, setEditing] = useState<Editing | null>(null)
  const [confirming, setConfirming] = useState<Confirming | null>(null)
  const [adding, setAdding] = useState(false)
  const [menu, setMenu] = useState(false)
  const [renaming, setRenaming] = useState(false)
  const [allLms, setAllLms] = useState(false)
  const [showOthers, setShowOthers] = useState(false)
  const nextToast = useRef(1)

  useEffect(() => {
    const stop = inApp ? onState<RosterState>(setState) : demoRoster.subscribe(setState)
    rpc<boolean>('ready').then(() => rpc<RosterState>('state')).then(setState).catch(() => { })
    return () => { stop() }
  }, [])

  const say = useCallback((ok: boolean, text: string) => {
    if (!text) return
    const id = nextToast.current++
    setToasts((all) => [...all.slice(-3), { id, ok, text }])
    setTimeout(() => setToasts((all) => all.filter((t) => t.id !== id)), ok ? 6000 : 12000)
  }, [])

  const run = useCallback(async (method: string, params: Record<string, unknown> = {}) => {
    try {
      const reply = await rpc<Reply | boolean>(method, params)
      if (typeof reply === 'object' && reply) say(reply.ok, reply.message)
      return typeof reply === 'object' ? reply : { ok: Boolean(reply), message: '' }
    } catch (e) {
      say(false, (e as Error).message)
      return { ok: false, message: (e as Error).message }
    }
  }, [say])

  // Only this person's groups, unless they ask for the others kept on this PC (a roster read earlier
  // with another account).
  const allGroups = state?.groups ?? []
  const mine = allGroups.filter((g) => g.mine)
  const others = allGroups.length - mine.length
  const groups = showOthers || mine.length === 0 ? allGroups : mine
  const group = groups.find((g) => g.id === selected) ?? groups[0] ?? null
  useEffect(() => { if (group && group.id !== selected) setSelected(group.id) }, [group, selected])
  const choose = (id: string) => { setSelected(id); remember(id); setQuery(''); setMenu(false) }

  const lmsOnly = useMemo(() => (state?.lmsGroups ?? []).filter((g) => !allGroups.some((x) => x.id.toLowerCase() === g.toLowerCase())), [state, allGroups])
  const students = useMemo(() => {
    const q = query.trim().toLowerCase()
    const all = group?.students ?? []
    return q ? all.filter((s) => [s.name, s.email, ...s.aliases].some((v) => v.toLowerCase().includes(q))) : all
  }, [group, query])

  const fromLms = async (id: string) => {
    const reply = await run('lmsRoster', { group: id })
    if (reply.ok && 'group' in reply && reply.group) choose(reply.group)
  }

  if (!state) return <div className="loading"><span className="spinner" /> Opening the rosters…</div>

  const reading = state.reading
  const total = groups.reduce((n, g) => n + g.students.length, 0)

  return (
    <div className="page roster-page">
      <header className="hero roster-hero">
        <div className="hero-copy">
          <span className="eyebrow">Rosters</span>
          <h1>Groups &amp; Students</h1>
          <p>{groups.length} {groups.length === 1 ? 'group' : 'groups'} · {total} students, each in roster order. Attendance checks the names in a meeting against these.</p>
        </div>
        <div className="hero-tools">
          {state.account
            ? <span className="account" title={state.account.email}><Icon name="user" size={13} /> LMS: {state.account.label || state.account.email}</span>
            : <button type="button" className="btn glass small" onClick={() => rpc('open', { page: state.pages.dashboard })}><Icon name="alert" size={14} /> Choose an LMS account on the Dashboard</button>}
          <div className="toolbar">
            <button type="button" className="btn glass" onClick={() => setAdding(true)}><Icon name="plus" size={15} /> New group</button>
            <button type="button" className="btn glass" onClick={() => rpc('open', { page: state.pages.attendance })}><Icon name="people" size={15} /> Attendance</button>
          </div>
        </div>
      </header>

      <div className="roster-layout">
        <aside className="group-rail" aria-label="Groups">
          {groups.length === 0 && lmsOnly.length === 0 && (
            <div className="rail-empty"><Icon name="people" size={22} /><p>No groups yet.</p>
              <button type="button" className="btn primary small" onClick={() => setAdding(true)}>Add the first group</button></div>
          )}
          {groups.map((g) => (
            <button type="button" key={g.id} className={`rail-item ${group?.id === g.id ? 'on' : ''}`} style={{ '--hue': groupHue(g.id) } as React.CSSProperties}
              onClick={() => choose(g.id)} aria-current={group?.id === g.id}>
              <i className="rail-dot" />
              <span className="rail-names"><b>{g.id}</b>{g.name !== g.id && <span>{g.name}</span>}</span>
              {reading === g.id ? <span className="spinner" /> : <span className="rail-count">{g.students.length}</span>}
            </button>
          ))}
          {mine.length > 0 && others > 0 && (
            <button type="button" className="btn ghost small rail-more" onClick={() => setShowOthers(!showOthers)}
              title="Rosters kept on this PC for groups that are not yours (read earlier with another account)">
              {showOthers ? 'Only my groups' : `Show ${others} other group${others === 1 ? '' : 's'} on this PC`}
            </button>
          )}
          {lmsOnly.length > 0 && (
            <>
              <h3 className="rail-title">On the LMS · no roster yet</h3>
              {(allLms || lmsOnly.length <= 6 ? lmsOnly : [...lmsOnly.slice(0, 5), ...(reading && lmsOnly.indexOf(reading) >= 5 ? [reading] : [])]).map((g) => (
                <div key={g} className="rail-item ghost" style={{ '--hue': groupHue(g) } as React.CSSProperties}>
                  <i className="rail-dot" />
                  <span className="rail-names"><b>{g}</b></span>
                  <button type="button" className="btn small" disabled={Boolean(reading)} onClick={() => fromLms(g)}>
                    {reading === g ? <span className="spinner" /> : <Icon name="download" size={13} />} Get
                  </button>
                </div>
              ))}
              {lmsOnly.length > 6 && (
                <button type="button" className="btn ghost small rail-more" onClick={() => setAllLms(!allLms)}>
                  {allLms ? 'Show fewer' : `Show all ${lmsOnly.length}`}
                </button>
              )}
            </>
          )}
        </aside>

        <main className="panel students-panel">
          {!group ? (
            <div className="empty-big">
              <Icon name="people" size={40} stroke={1.5} />
              <h2>Bring your first group</h2>
              <p className="muted">Choose a group on the left, or add one. Its students can come straight from the LMS.</p>
              <button type="button" className="btn primary" onClick={() => setAdding(true)}><Icon name="plus" size={15} /> New group</button>
            </div>
          ) : (
            <>
              <div className="students-head" style={{ '--hue': groupHue(group.id) } as React.CSSProperties}>
                <div className="students-title">
                  <span className="group-badge">{group.id.split('_').pop()}</span>
                  <div>
                    <h2>{group.name}</h2>
                    <span className="muted">{group.id !== group.name ? `${group.id} · ` : ''}{group.students.length} students · in roster order, never sorted</span>
                  </div>
                </div>
                <div className="students-actions">
                  <button type="button" className="btn primary" disabled={Boolean(reading)} onClick={() => fromLms(group.id)}
                    title="Reads the students from one of this group's sessions this month (nothing is changed on the LMS). New names are added; nobody is removed.">
                    {reading === group.id ? <span className="spinner light" /> : <Icon name="download" size={15} />} {reading === group.id ? 'Reading the LMS…' : 'Get from LMS'}
                  </button>
                  <button type="button" className="btn" onClick={() => run('import', { group: group.id })}><Icon name="sheet" size={15} /> Import file</button>
                  <button type="button" className="btn" onClick={() => setEditing({ student: null })}><Icon name="plus" size={15} /> Add student</button>
                  <div className="more-wrap">
                    <button type="button" className="btn icon" aria-label="More" aria-expanded={menu} onClick={() => setMenu(!menu)}><Icon name="more" size={18} /></button>
                    {menu && (
                      <div className="menu" role="menu" onMouseLeave={() => setMenu(false)}>
                        <button type="button" role="menuitem" onClick={() => { setMenu(false); setRenaming(true) }}>Rename</button>
                        <button type="button" role="menuitem" className="danger" onClick={() => { setMenu(false); setConfirming({
                          title: `Delete ${group.id}?`, text: `Its ${group.students.length} students are removed from this PC's rosters. Attendance already taken is not affected, and the previous file is kept as a backup.`,
                          action: 'Delete group', run: async () => { await run('deleteGroup', { id: group.id }) } }) }}>Delete group</button>
                      </div>
                    )}
                  </div>
                </div>
              </div>

              {reading === group.id && (
                <div className="reading"><span className="spinner" /><div><b>Reading {group.id} from the LMS</b>
                  <span>Opening its sessions this month and the attendance list. About a minute; nothing is changed on the LMS.</span></div></div>
              )}

              {group.students.length > 0 && (
                <label className="search roster-search"><Icon name="search" size={15} />
                  <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder={`Search ${group.students.length} students, other names or email`} aria-label="Search students" />
                  {query && <button type="button" className="clear-x" aria-label="Clear search" onClick={() => setQuery('')}><Icon name="close" size={13} /></button>}
                </label>
              )}

              {group.students.length === 0 ? (
                <div className="empty-big">
                  <Icon name="download" size={40} stroke={1.5} />
                  <h2>No students in {group.id} yet</h2>
                  <p className="muted">The LMS already has them: the app opens one of this group's sessions this month, reads its attendance list, and saves everyone here in the same order.</p>
                  <div className="row-actions center">
                    <button type="button" className="btn primary" disabled={Boolean(reading)} onClick={() => fromLms(group.id)}><Icon name="download" size={15} /> Get the students from the LMS</button>
                    <button type="button" className="btn" onClick={() => run('import', { group: group.id })}>Import a file</button>
                  </div>
                </div>
              ) : students.length === 0 ? (
                <p className="muted none">Nobody matches “{query}”.</p>
              ) : (
                <ol className="student-list">
                  {students.map((s, index) => (
                    <li key={s.id} className="st-row">
                      <span className="st-order">{s.order}</span>
                      <div className="st-main">
                        <b>{s.name}</b>
                        {(s.aliases.length > 0 || s.email) && (
                          <span className="st-extra">
                            {s.aliases.map((a) => <i key={a} className="alias">{a}</i>)}
                            {s.email && <span className="st-email">{s.email}</span>}
                          </span>
                        )}
                      </div>
                      <div className="st-actions">
                        <button type="button" className="btn icon small ghost" aria-label={`Move ${s.name} up`} disabled={Boolean(query) || index === 0} onClick={() => run('move', { group: group.id, id: s.id, dir: -1 })}><Icon name="up" size={15} /></button>
                        <button type="button" className="btn icon small ghost" aria-label={`Move ${s.name} down`} disabled={Boolean(query) || index === students.length - 1} onClick={() => run('move', { group: group.id, id: s.id, dir: 1 })}><Icon name="down" size={15} /></button>
                        <button type="button" className="btn icon small ghost" aria-label={`Edit ${s.name}`} onClick={() => setEditing({ student: s })}><Icon name="edit" size={15} /></button>
                        <button type="button" className="btn icon small ghost danger" aria-label={`Remove ${s.name}`} onClick={() => setConfirming({
                          title: `Remove ${s.name}?`, text: `They are taken off the ${group.id} roster. The other students keep their numbers.`, action: 'Remove',
                          run: async () => { await run('deleteStudent', { group: group.id, id: s.id }) } })}><Icon name="trash" size={15} /></button>
                      </div>
                    </li>
                  ))}
                </ol>
              )}
            </>
          )}
        </main>
      </div>

      {editing && group && (
        <StudentDialog student={editing.student} group={group.id} onClose={() => setEditing(null)}
          onSave={async (name, aliases, email) => {
            const reply = await run('saveStudent', { group: group.id, id: editing.student?.id ?? '', name, aliases, email })
            if (reply.ok) setEditing(null)
          }} />
      )}
      {adding && (
        <NewGroupDialog lmsGroups={lmsOnly} busy={Boolean(reading)} onClose={() => setAdding(false)}
          onCreate={async (id, fetch) => {
            setAdding(false)
            if (fetch) { await fromLms(id); return }
            const reply = await run('createGroup', { id, name: id })
            if (reply.ok) choose(id)
          }} />
      )}
      {renaming && group && (
        <RenameDialog group={group} onClose={() => setRenaming(false)}
          onSave={async (name) => { const reply = await run('renameGroup', { id: group.id, name }); if (reply.ok) setRenaming(false) }} />
      )}
      {confirming && (
        <Confirm {...confirming} onCancel={() => setConfirming(null)} onConfirm={async () => { const c = confirming; setConfirming(null); await c.run() }} />
      )}
      <Toasts toasts={toasts} dismiss={(id) => setToasts((all) => all.filter((t) => t.id !== id))} />
    </div>
  )
}

function useEscape(close: () => void) {
  useEffect(() => {
    const key = (e: KeyboardEvent) => e.key === 'Escape' && close()
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [close])
}

function StudentDialog({ student, group, onSave, onClose }: { student: Student | null; group: string; onSave: (name: string, aliases: string[], email: string) => void; onClose: () => void }) {
  const [name, setName] = useState(student?.name ?? '')
  const [aliases, setAliases] = useState((student?.aliases ?? []).join('\n'))
  const [email, setEmail] = useState(student?.email ?? '')
  useEscape(onClose)
  const submit = (e: React.FormEvent) => { e.preventDefault(); if (name.trim()) onSave(name.trim(), aliases.split(/[\n,]/).map((a) => a.trim()).filter(Boolean), email.trim()) }
  return (
    <div className="scrim" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <form className="dialog" role="dialog" aria-modal="true" aria-labelledby="student-title" onSubmit={submit}>
        <h2 id="student-title">{student ? 'Edit student' : `Add a student to ${group}`}</h2>
        {!student && <p>They go to the end of the roster; move them up afterwards if needed.</p>}
        <label className="field"><span>Full name</span><input autoFocus value={name} onChange={(e) => setName(e.target.value)} /></label>
        <label className="field"><span>Other names they use in Zoom</span>
          <textarea rows={3} value={aliases} onChange={(e) => setAliases(e.target.value)} placeholder="One per line, e.g. the Arabic spelling or a nickname" /></label>
        <label className="field"><span>Email (optional)</span><input type="email" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
        <div className="dialog-actions">
          <button type="button" className="btn ghost" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn primary" disabled={!name.trim()}>{student ? 'Save' : 'Add student'}</button>
        </div>
      </form>
    </div>
  )
}

function NewGroupDialog({ lmsGroups, busy, onCreate, onClose }: { lmsGroups: string[]; busy: boolean; onCreate: (id: string, fetch: boolean) => void; onClose: () => void }) {
  const [id, setId] = useState(lmsGroups[0] ?? '')
  const [fetch, setFetch] = useState(true)
  useEscape(onClose)
  const clean = id.trim()
  return (
    <div className="scrim" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <form className="dialog" role="dialog" aria-modal="true" aria-labelledby="group-title" onSubmit={(e) => { e.preventDefault(); if (clean) onCreate(clean, fetch) }}>
        <h2 id="group-title">New group</h2>
        <p>Write it exactly as the LMS does (e.g. <code>CAI5_AIS4_S9</code>): the meeting, the LMS session and the roster are matched by that name.</p>
        {lmsGroups.length > 0 && (
          <div className="pick-groups">{lmsGroups.map((g) => (
            <button type="button" key={g} className={`chip ${g === clean ? 'on' : ''}`} onClick={() => setId(g)}>{g}</button>
          ))}</div>
        )}
        <label className="field"><span>Group name</span><input autoFocus value={id} onChange={(e) => setId(e.target.value)} placeholder="CAI5_AIS4_S9" /></label>
        <label className="check small"><input type="checkbox" checked={fetch} disabled={busy} onChange={(e) => setFetch(e.target.checked)} /> Bring its students from the LMS now</label>
        <div className="dialog-actions">
          <button type="button" className="btn ghost" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn primary" disabled={!clean}>{fetch && !busy ? 'Add and get students' : 'Add group'}</button>
        </div>
      </form>
    </div>
  )
}

function RenameDialog({ group, onSave, onClose }: { group: Group; onSave: (name: string) => void; onClose: () => void }) {
  const [name, setName] = useState(group.name)
  useEscape(onClose)
  return (
    <div className="scrim" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <form className="dialog" role="dialog" aria-modal="true" aria-labelledby="rename-title" onSubmit={(e) => { e.preventDefault(); if (name.trim()) onSave(name.trim()) }}>
        <h2 id="rename-title">Rename {group.id}</h2>
        <p>Only the name shown here changes; <code>{group.id}</code> stays, because meetings and the LMS are matched by it.</p>
        <label className="field"><span>Display name</span><input autoFocus value={name} onChange={(e) => setName(e.target.value)} /></label>
        <div className="dialog-actions">
          <button type="button" className="btn ghost" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn primary" disabled={!name.trim()}>Save</button>
        </div>
      </form>
    </div>
  )
}

function Confirm({ title, text, action, onConfirm, onCancel }: Confirming & { onConfirm: () => void; onCancel: () => void }) {
  useEscape(onCancel)
  return (
    <div className="scrim" onMouseDown={(e) => e.target === e.currentTarget && onCancel()}>
      <div className="dialog" role="alertdialog" aria-modal="true" aria-labelledby="confirm-title">
        <h2 id="confirm-title">{title}</h2>
        <p>{text}</p>
        <div className="dialog-actions">
          <button type="button" className="btn ghost" autoFocus onClick={onCancel}>Cancel</button>
          <button type="button" className="btn primary danger-fill" onClick={onConfirm}>{action}</button>
        </div>
      </div>
    </div>
  )
}
