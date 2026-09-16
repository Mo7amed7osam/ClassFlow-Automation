import { useCallback, useEffect, useState } from 'react'
import { call, inApp, onState } from '../bridge'
import { Icon } from '../components/Icon'
import { Toasts, type Toast } from '../components/Dialogs'
import { DaysPicker, rangeOf, type DayRange } from '../components/DaysPicker'
import { groupHue, shortGroup } from '../logic'
import type { Activity, DashState } from './types'
import { demoDash } from './demo'

type Result = { ok: boolean; message: string }

export function Dashboard() {
  const [state, setState] = useState<DashState | null>(null)
  const [toasts, setToasts] = useState<Toast[]>([])
  const [working, setWorking] = useState<string | null>(null)

  const toast = useCallback((ok: boolean, text: string) => {
    const id = Date.now() + Math.random()
    setToasts((all) => [...all.slice(-3), { id, ok, text }])
    setTimeout(() => setToasts((all) => all.filter((t) => t.id !== id)), ok ? 6000 : 12000)
  }, [])

  useEffect(() => {
    if (!inApp) { setState(demoDash()); return }
    const off = onState<DashState>(setState)
    call('ready').then(() => call<DashState>('state')).then(setState).catch((e: Error) => toast(false, e.message))
    return off
  }, [toast])

  /** Every button: one request at a time, its answer as a toast, then the fresh state. */
  const run = async (label: string, method: string, params: Record<string, unknown> = {}) => {
    setWorking(label)
    try {
      const result = await call<Result | boolean>(method, params)
      if (result && typeof result === 'object') toast(result.ok, result.message)
      if (inApp) setState(await call<DashState>('state'))
      return result
    } catch (e) {
      toast(false, (e as Error).message)
    } finally {
      setWorking(null)
    }
  }

  if (!state) return <div className="loading"><span className="spinner" /> Loading the dashboard…</div>
  const me = state.me
  const admin = me?.role === 'admin'
  // The same page is also the Coordinators & groups page (?view=coordinators).
  const coordinatorsView = new URLSearchParams(location.search).get('view') === 'coordinators'

  return (
    <div className="page dash">
      <section className={`hero dash-hero ${me ? (admin ? 'role-admin' : 'role-coord') : 'idle'}`}>
        <div className="hero-copy">
          <span className="eyebrow">{me ? (admin ? 'Admin' : 'Coordinator') : 'Dashboard'}</span>
          {me ? (
            <>
              <h1>{greeting()}, {me.displayName || me.username}</h1>
              <p>
                Signed in as <b>{me.username}</b> · {admin ? 'you see and manage every group and every coordinator' : `you see ${me.groups.length} group${me.groups.length === 1 ? '' : 's'}`}
              </p>
              <div className="hero-groups">
                {(admin && me.allGroups ? ['All groups'] : me.groups).map((g) => (
                  <span key={g} className="hero-group-chip" style={{ '--hue': groupHue(g) } as React.CSSProperties}>{g === 'All groups' ? g : shortGroup(g)}</span>
                ))}
              </div>
            </>
          ) : (
            <>
              <h1>{coordinatorsView ? 'Coordinators & groups' : 'Sign in to your dashboard'}</h1>
              <p>With the account the admin gave you. The admin sees everything; a coordinator sees their own groups.</p>
            </>
          )}
        </div>
        <div className="hero-tools">
          <span className={`server-pill ${state.server.up ? 'up' : 'down'}`} title={state.server.detail}>
            <i className="pulse" /> {state.server.text}
          </span>
          {me && <SwitchMenu state={state} busy={working !== null} run={run} />}
        </div>
      </section>

      {!state.server.up && (
        <div className="banner bad">
          <Icon name="alert" size={18} />
          <div><b>The central server is not running.</b> <span>{state.server.detail}</span></div>
          {!state.server.clientMode && (
            <button type="button" className="btn primary" disabled={working !== null} onClick={() => run('server', 'startServer')}>
              {working === 'server' ? <span className="spinner light" /> : <Icon name="bolt" size={14} />} Start it
            </button>
          )}
          <button type="button" className="btn" onClick={() => call('open', { page: state.pages.server })}>Server page</button>
        </div>
      )}

      {!me && <SignIn busy={working !== null} known={state.known} onSignIn={(u, p, r, s) => run('in', 'signIn', { username: u, password: p, remember: r, savePassword: s })}
        onContinue={(u) => run('switch', 'switchAccount', { username: u })} onForget={(u) => run('forget', 'forgetAccount', { username: u })} />}

      {!coordinatorsView && (
        <div className="dash-grid">
          <SessionsCard state={state} working={working} run={run} />
          <LmsCard state={state} working={working} run={run} />
          {state.recordings && <RecordingsCard state={state} />}
        </div>
      )}

      {me && !coordinatorsView && <ActivityPanel />}

      {admin && state.users && state.groups && <Coordinators state={state} working={working} run={run} />}
      {coordinatorsView && admin && state.allGroups && <Groups state={state} working={working} run={run} />}
      {coordinatorsView && me && !admin && (
        <section className="panel"><h2><Icon name="alert" size={18} /> Only the admin manages coordinators and groups</h2>
          <p className="muted">Sign in as the admin (the account menu at the top) to add coordinators, approve them and give them groups.</p></section>
      )}
      {me && !admin && (
        <section className="panel">
          <h2><Icon name="people" size={18} /> Your groups</h2>
          <p className="muted">These are the groups the admin gave you. Ask the admin to add one.</p>
          <div className="chips">{me.groups.map((g) => <span key={g} className="chip g on" style={{ '--hue': groupHue(g) } as React.CSSProperties}><i /> {g}</span>)}</div>
        </section>
      )}

      <Toasts toasts={toasts} dismiss={(id) => setToasts((all) => all.filter((t) => t.id !== id))} />
    </div>
  )
}

function greeting() {
  const h = new Date().getHours()
  return h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening'
}

type Run = (label: string, method: string, params?: Record<string, unknown>) => Promise<unknown>

type Known = DashState['known'][number]

/** Accounts that signed in on this PC: one click continues (their session, or their saved password). */
function KnownAccounts({ known, busy, onContinue, onPick, onForget }: { known: Known[]; busy: boolean; onContinue: (u: string) => void; onPick: (u: string) => void; onForget: (u: string) => void }) {
  if (known.length === 0) return null
  return (
    <div className="known">
      {known.map((k) => {
        const ready = k.hasSession || k.hasPassword
        return (
        <div key={k.username} className="known-card" style={{ '--hue': groupHue(k.username) } as React.CSSProperties}>
          <button type="button" className="known-main" disabled={busy} onClick={() => (ready ? onContinue(k.username) : onPick(k.username))}
            title={k.hasSession ? 'Its 120-day session is still open.' : k.hasPassword ? 'Its password is saved on this PC (Windows Credential Manager).' : 'Type its password once.'}>
            <span className="avatar">{(k.displayName || k.username).slice(0, 1).toUpperCase()}</span>
            <span className="known-who"><b>{k.displayName}</b><span>{k.username} · <span className={`role-tag ${k.role}`}>{k.role}</span></span></span>
            <span className={`known-go${ready ? ' live' : ''}`}>{ready ? 'Continue' : 'Password'}</span>
          </button>
          <button type="button" className="known-x" aria-label={`Remove ${k.username} from this PC`} title="Remove from this PC (and its saved password)" onClick={() => onForget(k.username)}><Icon name="close" size={13} /></button>
        </div>
        )
      })}
    </div>
  )
}

function SignIn({ busy, known, onSignIn, onContinue, onForget }: { busy: boolean; known: Known[]; onSignIn: (u: string, p: string, r: boolean, s: boolean) => void; onContinue: (u: string) => void; onForget: (u: string) => void }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [remember, setRemember] = useState(true)
  const [savePassword, setSavePassword] = useState(true)
  const [other, setOther] = useState(known.length === 0)
  return (
    <form className="panel signin" onSubmit={(e) => { e.preventDefault(); onSignIn(username, password, remember, savePassword); setPassword('') }}>
      <div className="signin-copy">
        <h2><Icon name="user" size={18} /> {known.length ? 'Choose an account' : 'Sign in'}</h2>
        <p className="muted">{known.length ? 'Accounts that signed in on this PC. Continue needs no password: their session, or their password saved on this PC, signs them in.' : 'Admin or coordinator. New coordinators get their sign-in from the admin.'}</p>
        <KnownAccounts known={known} busy={busy} onContinue={onContinue} onForget={onForget}
          onPick={(u) => { setUsername(u); setOther(true); setTimeout(() => document.getElementById('dash-password')?.focus(), 0) }} />
        {known.length > 0 && !other && <button type="button" className="btn ghost small" onClick={() => setOther(true)}>+ Use another account</button>}
      </div>
      {other && <>
      <label className="field"><span>Username</span><input autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} /></label>
      <label className="field"><span>Password</span><input id="dash-password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} /></label>
      <label className="check"><input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} /> Keep me signed in for 120 days</label>
      <label className="check"><input type="checkbox" checked={savePassword} onChange={(e) => setSavePassword(e.target.checked)} /> Remember the password on this PC (Windows Credential Manager), so Continue works after the 120 days too</label>
      <button type="submit" className="btn primary" disabled={busy || !username || !password}>{busy ? <span className="spinner light" /> : <Icon name="bolt" size={14} />} Sign in</button>
      </>}
    </form>
  )
}

/** The signed-in account, and the others kept on this PC to switch to. */
function SwitchMenu({ state, busy, run }: { state: DashState; busy: boolean; run: Run }) {
  const [open, setOpen] = useState(false)
  const others = state.known.filter((k) => k.username !== state.me?.username)
  return (
    <div className="switch">
      <button type="button" className="btn glass" disabled={busy} onClick={() => setOpen(!open)}><Icon name="user" size={15} /> {state.me?.username} ▾</button>
      {open && (
        <div className="menu" role="menu">
          {others.map((k) => (
            <button key={k.username} type="button" role="menuitem" onClick={() => { setOpen(false); run('switch', 'switchAccount', { username: k.username }) }}>
              <Icon name="user" size={15} /> Switch to {k.displayName} ({k.role}){k.hasSession || k.hasPassword ? '' : ' — needs password'}
            </button>
          ))}
          <button type="button" role="menuitem" onClick={() => { setOpen(false); run('out', 'signOut') }}><Icon name="close" size={15} /> Sign out</button>
        </div>
      )}
    </div>
  )
}

function SessionsCard({ state, working, run }: { state: DashState; working: string | null; run: Run }) {
  const s = state.sessions
  const [range, setRange] = useState<DayRange & { key: string }>(() => ({ key: '7', ...rangeOf('7') }))
  const tiles = [
    { label: 'Running now', value: s.running, tone: 'blue', icon: 'bolt' },
    { label: 'Today', value: s.today, tone: 'violet', icon: 'calendar' },
    { label: 'Need attention', value: s.attention, tone: 'red', icon: 'alert' },
    { label: 'Drive pending', value: s.drivePending, tone: 'amber', icon: 'film' },
    { label: 'Fully done', value: s.done, tone: 'green', icon: 'check' },
  ]
  return (
    <section className="panel span-2">
      <header className="panel-head">
        <h2><Icon name="calendar" size={18} /> All sessions</h2>
        <span className="muted">LMS read {s.lastRead}</span>
      </header>
      <div className="mini-stats">
        {tiles.map((t) => (
          <button key={t.label} type="button" className={`mini t-${t.tone}`} onClick={() => call('open', { page: state.pages.sessions })}>
            <span className="stat-icon"><Icon name={t.icon} size={15} /></span><b>{t.value}</b><span>{t.label}</span>
          </button>
        ))}
      </div>
      {s.next && <p className="next-line"><Icon name="clock" size={14} /> Next: <b>{shortGroup(s.next.group)}</b> {s.next.title} · {s.next.date} {s.next.start}</p>}
      {s.attentionRows.length > 0 && (
        <ul className="attention">
          {s.attentionRows.map((r) => (
            <li key={`${r.group}${r.date}${r.start}`}><span className="group-chip" style={{ '--hue': groupHue(r.group) } as React.CSSProperties}>{shortGroup(r.group)}</span>{r.title || r.group} · {r.date}<em>{r.next}</em></li>
          ))}
        </ul>
      )}
      <DaysPicker value={range} onChange={setRange} />
      <div className="row-actions start">
        <button type="button" className="btn primary" disabled={working !== null} onClick={() => run('full', 'check', { full: true, from: range.from, to: range.to })}
          title="Opens every session of these days on the LMS: status, record link and attendance. Nothing is changed.">
          {working === 'full' ? <span className="spinner light" /> : <Icon name="search" size={14} />} Check sessions · {range.label}
        </button>
        <button type="button" className="btn" disabled={working !== null} onClick={() => run('quick', 'check', { full: false, from: range.from, to: range.to })}>
          {working === 'quick' ? <span className="spinner" /> : <Icon name="refresh" size={14} />} Quick check
        </button>
        <button type="button" className="btn" disabled={working !== null} onClick={() => run('drive', 'sheetAll')}>
          {working === 'drive' ? <span className="spinner" /> : <Icon name="drive" size={14} />} Put Drive links on the LMS
        </button>
        <button type="button" className="btn ghost" onClick={() => call('open', { page: state.pages.sessions })}>Open Sessions <Icon name="open" size={14} /></button>
      </div>
      {s.status && <p className="muted small">{s.status}</p>}
    </section>
  )
}

function LmsCard({ state, working, run }: { state: DashState; working: string | null; run: Run }) {
  const [adding, setAdding] = useState(false)
  const [label, setLabel] = useState('')
  const [email, setEmail] = useState('')
  // The account's role is the signed-in person's: nothing to choose.
  const role = state.me?.role === 'admin' ? 'admin' : 'coordinator'
  const [password, setPassword] = useState('')
  const who = state.me?.username
  return (
    <section className="panel">
      <header className="panel-head">
        <h2><Icon name="user" size={18} /> Your LMS account</h2>
        <button type="button" className="btn small" onClick={() => setAdding(!adding)}>{adding ? 'Close' : '+ Add'}</button>
      </header>
      <p className="muted">{who ? <>{state.lms.onServer ? 'Kept in the database for ' : 'The account the app uses on the LMS for '}<b>{who}</b>{state.lms.onServer ? ' (the password encrypted) - any PC you sign in on uses it.' : '.'}</> : "Sign in to keep your LMS accounts in the database. Until then this PC's account is used."}</p>
      <ul className="lms-list">
        {state.lms.accounts.map((a) => {
          const active = a.id === state.lms.active
          return (
            <li key={a.id} className={active ? 'active' : ''}>
              <span className="radio">{active && <i />}</span>
              <div><b>{a.label}</b><span>{a.email}</span></div>
              {active ? <span className="tag">In use</span> : (
                <>
                  <button type="button" className="btn small" disabled={working !== null} onClick={() => run('use', 'useLms', { id: a.id })}>Use</button>
                  <button type="button" className="btn small ghost danger" disabled={working !== null} onClick={() => run('rm', 'removeLms', { id: a.id })}>Remove</button>
                </>
              )}
            </li>
          )
        })}
        {state.lms.accounts.length === 0 && <li className="empty-li">No LMS account yet — add one.</li>}
      </ul>
      {adding && (
        <form className="form-grid" onSubmit={async (e) => { e.preventDefault(); await run('lms', 'saveLms', { label, email, role, password, makeActive: true }); setPassword(''); setAdding(false) }}>
          <label className="field wide"><span>Label</span><input value={label} onChange={(e) => setLabel(e.target.value)} placeholder={role === 'admin' ? 'Admin' : 'Coordinator'} /></label>
          <label className="field wide"><span>LMS email</span><input type="email" autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
          <label className="field wide"><span>LMS password {state.lms.onServer ? '(kept encrypted in the database)' : '(kept on this PC until you sign in)'}</span><input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          <div className="row-actions wide"><button type="submit" className="btn primary" disabled={!email || !password || working !== null}>Save and use</button></div>
        </form>
      )}
    </section>
  )
}

/**
 * What every PC did on its own and told the server afterwards: the classes it opened, the LMS steps
 * it finished, the classes it ended. Each PC works without the server; this is how the server knows.
 * Asked for only when it is opened, so the Dashboard's own refresh stays light.
 */
function ActivityPanel() {
  const [items, setItems] = useState<Activity[] | null>(null)
  const [problem, setProblem] = useState('')
  const [open, setOpen] = useState(false)
  const [loading, setLoading] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const answer = await call<{ ok: boolean; items?: Activity[]; message?: string }>('activity')
      if (answer.ok) { setItems(answer.items ?? []); setProblem('') }
      else { setItems([]); setProblem(answer.message ?? 'The server did not answer.') }
    } catch (e) {
      setItems([]); setProblem((e as Error).message)
    } finally { setLoading(false) }
  }, [])

  useEffect(() => { if (open && items === null) void load() }, [open, items, load])

  const when = (at: string) => {
    const date = new Date(at)
    return Number.isNaN(date.getTime()) ? at : date.toLocaleString([], { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
  }

  return (
    <section className="panel">
      <header className="panel-head">
        <h2><Icon name="bolt" size={18} /> What the PCs did</h2>
        <div className="row-actions">
          {open && <button type="button" className="btn small ghost" disabled={loading} onClick={() => void load()}>
            {loading ? <span className="spinner" /> : <Icon name="refresh" size={13} />} Refresh
          </button>}
          <button type="button" className="btn small ghost" onClick={() => setOpen((o) => !o)}>{open ? 'Hide' : 'Show'}</button>
        </div>
      </header>
      {!open && <p className="muted">Every PC runs its classes on its own and reports what it did. Open to see it.</p>}
      {open && loading && items === null && <p className="muted">Asking the server…</p>}
      {open && problem && <p className="muted">{problem}</p>}
      {open && items !== null && items.length === 0 && !problem && (
        <p className="muted">Nothing reported yet. A PC reports once it has joined the server and done something.</p>
      )}
      {open && items !== null && items.length > 0 && (
        <ul className="activity">
          {items.map((item) => (
            <li key={item.id} className={item.outcome === 'failed' ? 'bad' : undefined}>
              <time>{when(item.at)}</time>
              {item.group
                ? <span className="chip g" style={{ '--hue': groupHue(item.group) } as React.CSSProperties}>{shortGroup(item.group)}</span>
                : <span className="chip g">—</span>}
              <span className="what">{item.summary || item.kind}</span>
              <em>{item.device ?? 'a PC'}</em>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function RecordingsCard({ state }: { state: DashState }) {
  const r = state.recordings!
  const parts = [
    { label: 'On the LMS', value: r.onLms, tone: 'green' },
    { label: 'Drive', value: r.drive, tone: 'blue' },
    { label: 'Zoom only', value: r.zoomOnly, tone: 'amber' },
    { label: 'No link', value: r.missing, tone: 'red' },
  ]
  const total = Math.max(1, r.total)
  return (
    <section className="panel">
      <header className="panel-head">
        <h2><Icon name="film" size={18} /> Recordings</h2>
        <button type="button" className="btn small ghost" onClick={() => call('open', { page: state.pages.recordings })}>Open <Icon name="open" size={13} /></button>
      </header>
      <p className="big-number">{r.total}<span> recordings n8n sent from the sheet</span></p>
      <div className="bar">{parts.filter((p) => p.label !== 'On the LMS').map((p) => <i key={p.label} className={`t-${p.tone}`} style={{ width: `${(p.value / total) * 100}%` }} />)}</div>
      <ul className="legend">{parts.map((p) => <li key={p.label} className={`t-${p.tone}`}><i />{p.label}<b>{p.value}</b></li>)}</ul>
    </section>
  )
}

function Coordinators({ state, working, run }: { state: DashState; working: string | null; run: Run }) {
  const users = state.users!
  const groups = state.groups!
  const pending = users.filter((u) => u.status === 'pending')
  const others = users.filter((u) => u.status !== 'pending')
  const [adding, setAdding] = useState(false)
  const [username, setUsername] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const [editing, setEditing] = useState<string | null>(null)
  const [editGroups, setEditGroups] = useState<string[]>([])
  // A password the admin just typed (new coordinator, or a reset) is shown to copy once, then dropped.
  const [share, setShare] = useState<{ title: string; text: string } | null>(null)
  const copy = (text: string) => run('copy', 'copyText', { text })
  const namesOf = (ids: string[]) => groups.filter((g) => ids.includes(g.id)).map((g) => g.name)

  return (
    <section className="panel coordinators">
      <header className="panel-head">
        <h2><Icon name="people" size={18} /> Coordinators</h2>
        <button type="button" className="btn primary small" onClick={() => setAdding(!adding)}>{adding ? 'Close' : '+ Add coordinator'}</button>
      </header>
      <p className="muted">A coordinator signs in to this Dashboard (in their copy of the app) and sees only the groups ticked here — in the app and on the server. <b>Copy sign-in</b> puts everything they need in one message.</p>

      {share && (
        <div className="share-box" role="status">
          <Icon name="check" size={16} />
          <div className="grow"><b>{share.title}</b><span>Copy it and send it to them (WhatsApp, email…). The password is not kept anywhere after you close this.</span></div>
          <button type="button" className="btn small primary" onClick={() => copy(share.text)}><Icon name="sheet" size={13} /> Copy sign-in</button>
          <button type="button" className="btn small ghost" onClick={() => setShare(null)}>Done</button>
        </div>
      )}

      {adding && (
        <form className="add-coord" onSubmit={async (e) => {
          e.preventDefault()
          const result = await run('create', 'createUser', { username, displayName, password, groupIds: picked }) as Result | undefined
          if (result?.ok) {
            const text = signInText({ username: username.trim(), displayName: displayName.trim() }, namesOf(picked), password)
            setShare({ title: `${username.trim()} was created.`, text })
            copy(text)
            setUsername(''); setDisplayName(''); setPassword(''); setPicked([]); setAdding(false)
          }
        }}>
          <div className="form-grid">
            <label className="field"><span>Username</span><input autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} placeholder="e.g. sara.coordinator" /></label>
            <label className="field"><span>Display name</span><input value={displayName} onChange={(e) => setDisplayName(e.target.value)} placeholder="Sara" /></label>
            <label className="field wide"><span>Password (12 characters or more — you give it to them)</span><input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          </div>
          <span className="field-title">Groups they see</span>
          <GroupPicker groups={groups} value={picked} onChange={setPicked} />
          <div className="row-actions"><button type="submit" className="btn primary" disabled={!username || password.length < 12 || working !== null}>Create coordinator</button></div>
        </form>
      )}

      {pending.length > 0 && (
        <div className="pending">
          <h3>Waiting for your approval <span className="count">{pending.length}</span></h3>
          {pending.map((u) => (
            <div key={u.id} className="user-row">
              <div className="avatar">{(u.displayName || u.username).slice(0, 1).toUpperCase()}</div>
              <div className="who"><b>{u.displayName}</b><span>{u.username} asked to join</span></div>
              <button type="button" className="btn small primary" disabled={working !== null} onClick={() => run('ap', 'approve', { id: u.id, approve: true })}>Approve</button>
              <button type="button" className="btn small ghost danger" disabled={working !== null} onClick={() => run('rj', 'approve', { id: u.id, approve: false })}>Reject</button>
            </div>
          ))}
        </div>
      )}

      <div className="users">
        {others.map((u) => (
          <div key={u.id} className={`user-row${u.status === 'disabled' ? ' off' : ''}`}>
            <div className="avatar" style={{ '--hue': groupHue(u.username) } as React.CSSProperties}>{(u.displayName || u.username).slice(0, 1).toUpperCase()}</div>
            <div className="who">
              <b>{u.displayName} <span className={`role-tag ${u.role}`}>{u.role}</span>{u.status === 'disabled' && <span className="role-tag off">disabled</span>}</b>
              <span>{u.username}{u.lastLogin ? ` · last in ${u.lastLogin}` : ' · never signed in'}</span>
            </div>
            <div className="user-groups">
              {editing === u.id ? <span className="muted">{editGroups.length} selected — choose below</span>
                : u.role === 'admin' ? <span className="muted">every group</span> : u.groups.length ? u.groups.map((g) => (
                <span key={g.id} className="group-chip" title={g.name} style={{ '--hue': groupHue(g.name) } as React.CSSProperties}>{shortGroup(g.name)}</span>
              )) : <span className="muted">no groups yet</span>}
            </div>
            {u.role !== 'admin' && (
              <div className="user-actions">
                {editing === u.id ? (
                  <>
                    <button type="button" className="btn small primary" disabled={working !== null} onClick={async () => { await run('grp', 'setGroups', { id: u.id, groupIds: editGroups }); setEditing(null) }}>Save groups</button>
                    <button type="button" className="btn small ghost" onClick={() => setEditing(null)}>Cancel</button>
                  </>
                ) : (
                  <>
                    <button type="button" className="btn small" onClick={() => { setEditing(u.id); setEditGroups(u.groups.map((g) => g.id)) }}>Groups</button>
                    <button type="button" className="btn small" title="Copies their username, password and groups, ready to send them" onClick={() => copy(signInText(u, u.groups.map((g) => g.name)))}><Icon name="sheet" size={13} /> Copy sign-in</button>
                    <button type="button" className="btn small ghost" disabled={working !== null} onClick={() => run('st', 'setStatus', { id: u.id, status: u.status === 'disabled' ? 'active' : 'disabled' })}>{u.status === 'disabled' ? 'Enable' : 'Disable'}</button>
                    <ResetPassword id={u.id} run={run} busy={working !== null} onSet={(password) => {
                      const text = signInText(u, u.groups.map((g) => g.name), password)
                      setShare({ title: `${u.username}'s new password was set.`, text })
                      copy(text)
                    }} />
                  </>
                )}
              </div>
            )}
            {editing === u.id && <GroupPicker groups={groups} value={editGroups} onChange={setEditGroups} />}
          </div>
        ))}
      </div>
    </section>
  )
}

/**
 * Choosing groups by their full names (CAI5_AIS4_S1 and CAI5_AIS2_S1 are different groups), laid
 * out by programme with a search and "all of this programme".
 */
function GroupPicker({ groups, value, onChange }: { groups: { id: string; name: string }[]; value: string[]; onChange: (ids: string[]) => void }) {
  const [query, setQuery] = useState('')
  const q = query.trim().toLowerCase()
  const programmes = new Map<string, { id: string; name: string }[]>()
  for (const g of [...groups].sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }))) {
    if (q && !g.name.toLowerCase().includes(q)) continue
    const key = g.name.includes('_') ? g.name.slice(0, g.name.lastIndexOf('_')) : 'Other'
    programmes.set(key, [...(programmes.get(key) ?? []), g])
  }
  const set = (ids: string[], on: boolean) => onChange(on ? [...new Set([...value, ...ids])] : value.filter((id) => !ids.includes(id)))
  return (
    <div className="group-editor">
      <div className="group-editor-bar">
        <label className="search"><Icon name="search" size={14} /><input autoFocus value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search groups, e.g. AIS4 or S7" /></label>
        <span className="muted">{value.length} selected</span>
        {value.length > 0 && <button type="button" className="btn small ghost" onClick={() => onChange([])}>Clear</button>}
      </div>
      {programmes.size === 0 && <p className="muted">No group matches “{query}”.</p>}
      {[...programmes].map(([programme, list]) => {
        const chosen = list.filter((g) => value.includes(g.id)).length
        return (
          <div key={programme} className="programme">
            <div className="programme-head">
              <b>{programme}</b><span className="muted">{chosen}/{list.length}</span>
              <button type="button" className="btn small ghost" onClick={() => set(list.map((g) => g.id), chosen < list.length)}>{chosen < list.length ? 'Select all' : 'Clear all'}</button>
            </div>
            <div className="chips">{list.map((g) => (
              <button type="button" key={g.id} className={`chip g small${value.includes(g.id) ? ' on' : ''}`} style={{ '--hue': groupHue(g.name) } as React.CSSProperties}
                aria-pressed={value.includes(g.id)} onClick={() => onChange(value.includes(g.id) ? value.filter((x) => x !== g.id) : [...value, g.id])}>
                <i /> {g.name}
              </button>
            ))}</div>
          </div>
        )
      })}
    </div>
  )
}

/**
 * What the admin sends a coordinator: their username, password and groups. The password is in it
 * only right after the admin typed it (a new coordinator, or a reset); the server keeps no copy.
 */
function signInText(user: { username: string; displayName: string }, groups: string[], password?: string) {
  return [
    `Username: ${user.username}`,
    `Password: ${password ?? '(the one I gave you)'}`,
    `Groups: ${groups.length ? groups.join(', ') : '(none yet)'}`,
  ].join('\n')
}

function ResetPassword({ id, run, busy, onSet }: { id: string; run: Run; busy: boolean; onSet: (password: string) => void }) {
  const [open, setOpen] = useState(false)
  const [password, setPassword] = useState('')
  if (!open) return <button type="button" className="btn small ghost" onClick={() => setOpen(true)}>Password</button>
  return (
    <form className="inline-form" onSubmit={async (e) => {
      e.preventDefault()
      const result = await run('pw', 'resetPassword', { id, password }) as Result | undefined
      if (result?.ok) onSet(password)
      setPassword(''); setOpen(false)
    }}>
      <input type="password" autoComplete="new-password" placeholder="New password (12+)" value={password} onChange={(e) => setPassword(e.target.value)} />
      <button type="submit" className="btn small primary" disabled={busy || password.length < 12}>Set</button>
      <button type="button" className="btn small ghost" onClick={() => setOpen(false)}>×</button>
    </form>
  )
}

function Groups({ state, working, run }: { state: DashState; working: string | null; run: Run }) {
  const [name, setName] = useState('')
  const [label, setLabel] = useState('')
  const [showArchived, setShowArchived] = useState(false)
  const all = state.allGroups!
  const shown = all.filter((g) => showArchived || !g.archived)
  return (
    <section className="panel">
      <header className="panel-head">
        <h2><Icon name="calendar" size={18} /> Groups</h2>
        <label className="check small"><input type="checkbox" checked={showArchived} onChange={(e) => setShowArchived(e.target.checked)} /> Show archived ({all.filter((g) => g.archived).length})</label>
      </header>
      <p className="muted">Each group is named exactly as the LMS and the recordings sheet name it. Archiving hides a finished group; nothing is deleted.</p>
      <form className="add-group" onSubmit={async (e) => { e.preventDefault(); const r = await run('grp-new', 'createGroup', { name, displayName: label }) as Result | undefined; if (r?.ok) { setName(''); setLabel('') } }}>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="CAI5_AIS4_S9" aria-label="Group name" />
        <input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="Friendly name (optional)" aria-label="Friendly name" />
        <button type="submit" className="btn primary" disabled={!name.trim() || working !== null}>+ Add group</button>
      </form>
      <div className="group-grid">
        {shown.map((g) => (
          <div key={g.id} className={`group-card${g.archived ? ' off' : ''}`} style={{ '--hue': groupHue(g.name) } as React.CSSProperties}>
            <div className="group-top">
              <span className="group-chip">{shortGroup(g.name)}</span>
              <div className="group-names"><b>{g.displayName || g.name}</b>{g.displayName && <span>{g.name}</span>}</div>
              <button type="button" className="btn small ghost" disabled={working !== null} onClick={() => run('grp-arch', 'archiveGroup', { id: g.id, archived: !g.archived })}>{g.archived ? 'Restore' : 'Archive'}</button>
            </div>
            <div className="group-stats">
              <span><b>{g.recordings}</b> recordings</span>
              <span className="t-green"><b>{g.onLms}</b> on the LMS</span>
              <span className="t-amber"><b>{g.pending}</b> pending</span>
              <span className="t-red"><b>{g.missing}</b> no link</span>
            </div>
            <p className="group-foot">{g.coordinators.length ? <>Coordinators: <b>{g.coordinators.join(', ')}</b></> : 'No coordinator yet'}{g.lastSession ? ` · last class ${g.lastSession}` : ''}</p>
          </div>
        ))}
      </div>
    </section>
  )
}