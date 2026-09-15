import { useEffect, useRef, useState } from 'react'
import { call, inApp, onState } from '../bridge'
import { Icon } from '../components/Icon'
import { groupHue, shortGroup } from '../logic'
import type { Reply, WelcomeState, ZoomAccount } from './types'
import { demoWelcome } from './demo'

// Get started: what a new user types once, in order. Their account first (coordinator or admin),
// then their LMS account, a Zoom account and session link per group, and their own AI key (or Skip).

const rpc = <T,>(method: string, params: Record<string, unknown> = {}) => (inApp ? call<T>(method, params) : demoWelcome.call<T>(method, params))

type Step = 'account' | 'lms' | 'zoom' | 'ai' | 'done'
const STEPS: { key: Step; title: string; hint: string }[] = [
  { key: 'account', title: 'Your account', hint: 'Coordinator or admin' },
  { key: 'lms', title: 'LMS account', hint: 'The email you use on the LMS' },
  { key: 'zoom', title: 'Zoom & session links', hint: 'One Zoom account per group' },
  { key: 'ai', title: 'AI key', hint: 'OpenRouter · optional' },
  { key: 'done', title: 'Done', hint: 'Import your timetable' },
]

function firstOpen(s: WelcomeState): Step {
  if (!s.me) return 'account'
  if (s.lms.accounts.length === 0) return 'lms'
  if (s.zoom.length === 0) return 'zoom'
  if (!s.ai.ready) return 'ai'
  return 'done'
}

export function Welcome() {
  const [state, setState] = useState<WelcomeState | null>(null)
  const [step, setStep] = useState<Step>('account')
  const [aiSkipped, setAiSkipped] = useState(false)
  const started = useRef(false)

  useEffect(() => {
    const stop = inApp ? onState<WelcomeState>(setState) : demoWelcome.subscribe(setState)
    rpc<boolean>('ready').then(() => rpc<WelcomeState>('state')).then(setState).catch(() => { })
    return () => { stop() }
  }, [])

  // The steps open where this PC stands (closed half way last time: carry on from there).
  useEffect(() => {
    if (state && !started.current) { started.current = true; setStep(firstOpen(state)) }
  }, [state])

  if (!state) return <div className="loading"><span className="spinner" /> Opening…</div>

  const done: Record<Step, boolean> = {
    account: Boolean(state.me),
    lms: state.lms.accounts.length > 0,
    zoom: state.zoom.length > 0,
    ai: state.ai.ready || aiSkipped,
    done: false,
  }
  // A step opens once everything before it is done (the AI key may be skipped).
  const reachable = (key: Step) => STEPS.slice(0, STEPS.findIndex((s) => s.key === key)).every((s) => done[s.key])
  const index = STEPS.findIndex((s) => s.key === step)
  const next = () => setStep(STEPS[Math.min(index + 1, STEPS.length - 1)].key)

  return (
    <div className="welcome">
      <aside className="rail">
        <div className="brand">
          <span className="brand-mark"><Icon name="video" size={18} /></span>
          <div><b>Zoom Auto Admit</b><span>Get started</span></div>
        </div>
        <ol>
          {STEPS.map((s, i) => {
            const open = reachable(s.key)
            return (
              <li key={s.key}>
                <button type="button" className={`rail-step${s.key === step ? ' current' : ''}${done[s.key] ? ' done' : ''}`} disabled={!open} onClick={() => setStep(s.key)}>
                  <span className="bullet">{done[s.key] ? <Icon name="check" size={13} /> : i + 1}</span>
                  <span className="rail-text"><b>{s.title}</b><em>{s.hint}</em></span>
                </button>
              </li>
            )
          })}
        </ol>
        <button type="button" className="btn ghost small later" onClick={() => rpc('later')} title="Close; these steps open again the next time the app starts.">Finish later</button>
      </aside>
      <main className="stage">
        {step === 'account' && <AccountStep state={state} onNext={next} />}
        {step === 'lms' && <LmsStep state={state} onNext={next} />}
        {step === 'zoom' && <ZoomStep state={state} onNext={next} />}
        {step === 'ai' && <AiStep state={state} onNext={next} onSkip={() => { setAiSkipped(true); next() }} />}
        {step === 'done' && <DoneStep state={state} aiSkipped={aiSkipped && !state.ai.ready} />}
      </main>
    </div>
  )
}

// ------------------------------------------------------------------ pieces

function useAction() {
  const [working, setWorking] = useState(false)
  const [reply, setReply] = useState<Reply | null>(null)
  const run = async (method: string, params: Record<string, unknown>) => {
    setWorking(true)
    setReply(null)
    try {
      const answer = await rpc<Reply>(method, params)
      setReply(answer)
      return answer
    } catch (e) {
      const failed = { ok: false, message: (e as Error).message }
      setReply(failed)
      return failed
    } finally { setWorking(false) }
  }
  return { working, reply, run, clear: () => setReply(null) }
}

function Note({ reply }: { reply: Reply | null }) {
  if (!reply?.message) return null
  return <p className={`note ${reply.ok ? 'ok' : 'bad'}`} role="status"><Icon name={reply.ok ? 'check' : 'alert'} size={14} /> {reply.message}</p>
}

function Head({ n, title, text }: { n: number; title: string; text: string }) {
  return (
    <header className="stage-head">
      <span className="step-no">Step {n} of 4</span>
      <h1>{title}</h1>
      <p className="muted">{text}</p>
    </header>
  )
}

function Footer({ canNext, onNext, children }: { canNext: boolean; onNext: () => void; children?: React.ReactNode }) {
  return (
    <footer className="stage-foot">
      {children}
      <button type="button" className="btn primary" disabled={!canNext} onClick={onNext}>Next <span aria-hidden="true">→</span></button>
    </footer>
  )
}

// ------------------------------------------------------------------ 1. account

function AccountStep({ state, onNext }: { state: WelcomeState; onNext: () => void }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [savePassword, setSavePassword] = useState(true)
  const [server, setServer] = useState(state.server)
  const [editServer, setEditServer] = useState(!state.server)
  const { working, reply, run } = useAction()
  const me = state.me

  return (
    <section className="step">
      <Head n={1} title="Your account" text="Sign in with your username and password: the coordinator account the admin made for you, or the admin account. The app then knows your groups and keeps your LMS account for you." />
      {me ? (
        <div className="card-box signed">
          <span className="avatar">{me.displayName.slice(0, 1).toUpperCase()}</span>
          <div className="grow">
            <b>{me.displayName}</b>
            <span className="muted">{me.username} · {me.role}</span>
            <div className="chips">
              {me.allGroups ? <span className="chip">All groups</span> : me.groups.length === 0
                ? <span className="muted">The admin has not given you a group yet — you can still go on.</span>
                : me.groups.map((g) => <span key={g} className="chip" style={{ '--hue': groupHue(g) } as React.CSSProperties} title={g}>{g}</span>)}
            </div>
          </div>
          <button type="button" className="btn small ghost" disabled={working} onClick={() => run('signOut', {})}>Use another account</button>
        </div>
      ) : (
        <form className="card-box form" onSubmit={async (e) => { e.preventDefault(); await run('signIn', { server, username, password, savePassword }); setPassword('') }}>
          <label className="field"><span>Username</span><input autoFocus autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} /></label>
          <label className="field"><span>Password</span><input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          <label className="check"><input type="checkbox" checked={savePassword} onChange={(e) => setSavePassword(e.target.checked)} /> Remember the password on this PC (Windows Credential Manager)</label>
          {editServer
            ? <label className="field"><span>Server (from the admin)</span><input value={server} onChange={(e) => setServer(e.target.value)} placeholder="https://…" /></label>
            : <p className="muted server-line"><Icon name="link" size={13} /> Server: {hostOf(server)} <button type="button" className="linkish" onClick={() => setEditServer(true)}>Change</button></p>}
          <div className="row-actions start">
            <button type="submit" className="btn primary" disabled={working || !username || !password || !server}>{working ? <span className="spinner light" /> : <Icon name="user" size={14} />} Sign in</button>
          </div>
        </form>
      )}
      <Note reply={reply} />
      <Footer canNext={Boolean(me)} onNext={onNext} />
    </section>
  )
}

function hostOf(url: string) {
  try { return new URL(url).host } catch { return url || '(not set)' }
}

// ------------------------------------------------------------------ 2. LMS

function LmsStep({ state, onNext }: { state: WelcomeState; onNext: () => void }) {
  const accounts = state.lms.accounts
  const [adding, setAdding] = useState(accounts.length === 0)
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [label, setLabel] = useState('')
  const { working, reply, run } = useAction()
  useEffect(() => { if (accounts.length === 0) setAdding(true) }, [accounts.length])

  return (
    <section className="step">
      <Head n={2} title="Your LMS account" text={state.lms.onServer
        ? 'The email and password you sign in to the LMS with. The password is kept encrypted with your account on the server, so any PC you sign in on can use it.'
        : 'The email and password you sign in to the LMS with. The password is kept in Windows Credential Manager on this PC.'} />
      {accounts.length > 0 && (
        <ul className="card-box list">
          {accounts.map((a) => (
            <li key={a.id}><Icon name="user" size={15} /><div className="grow"><b>{a.label}</b><span className="muted">{a.email}</span></div>{a.active && <span className="tag">In use</span>}</li>
          ))}
        </ul>
      )}
      {adding ? (
        <form className="card-box form" onSubmit={async (e) => {
          e.preventDefault()
          const answer = await run('saveLms', { email, password, label })
          setPassword('')
          if (answer.ok) { setAdding(false); setEmail(''); setLabel('') }
        }}>
          <label className="field"><span>LMS email</span><input type="email" autoFocus autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
          <label className="field"><span>LMS password</span><input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          <label className="field"><span>Name for it (optional)</span><input value={label} onChange={(e) => setLabel(e.target.value)} placeholder={state.me?.displayName ?? 'My LMS account'} /></label>
          <div className="row-actions start">
            <button type="submit" className="btn primary" disabled={working || !email || !password}>{working ? <span className="spinner light" /> : <Icon name="check" size={14} />} Save</button>
            {accounts.length > 0 && <button type="button" className="btn ghost" onClick={() => setAdding(false)}>Cancel</button>}
          </div>
        </form>
      ) : <button type="button" className="btn small" onClick={() => setAdding(true)}><Icon name="plus" size={13} /> Another LMS account</button>}
      <Note reply={reply} />
      <Footer canNext={accounts.length > 0} onNext={onNext} />
    </section>
  )
}

// ------------------------------------------------------------------ 3. Zoom

function ZoomStep({ state, onNext }: { state: WelcomeState; onNext: () => void }) {
  const [extra, setExtra] = useState<string[]>([])
  const [newGroup, setNewGroup] = useState('')
  const saved = state.zoom
  const groups = [...new Set([...(state.me?.groups ?? []), ...saved.map((z) => z.group), ...extra])]
  const [open, setOpen] = useState<string | null>(() => groups.find((g) => !saved.some((z) => same(z.group, g))) ?? null)

  return (
    <section className="step">
      <Head n={3} title="Zoom accounts and session links" text="For each of your groups: the Zoom account that hosts its sessions, and the session link it usually uses. The app opens that meeting by itself at class time." />
      <div className="groups">
        {groups.map((g) => {
          const account = saved.find((z) => same(z.group, g))
          return <ZoomGroup key={g} group={g} account={account} open={open === g} onToggle={() => setOpen(open === g ? null : g)}
            onSaved={() => setOpen(groups.find((x) => x !== g && !saved.some((z) => same(z.group, x))) ?? null)} />
        })}
        {groups.length === 0 && <p className="muted">{state.me?.allGroups ? 'You see every group.' : 'The admin has not given you a group yet.'} Type a group's name as the LMS writes it:</p>}
      </div>
      <form className="add-group" onSubmit={(e) => {
        e.preventDefault()
        const g = newGroup.trim()
        if (!g) return
        if (!groups.some((x) => same(x, g))) setExtra([...extra, g])
        setOpen(g)
        setNewGroup('')
      }}>
        <input value={newGroup} onChange={(e) => setNewGroup(e.target.value)} placeholder="Another group, e.g. CAI5_AIS4_S9" aria-label="Another group" />
        <button type="submit" className="btn small" disabled={!newGroup.trim()}><Icon name="plus" size={13} /> Add group</button>
      </form>
      <Footer canNext={saved.length > 0} onNext={onNext}>
        <span className="muted">{saved.length} of {Math.max(groups.length, saved.length)} saved</span>
      </Footer>
    </section>
  )
}

const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()

function ZoomGroup({ group, account, open, onToggle, onSaved }: { group: string; account?: ZoomAccount; open: boolean; onToggle: () => void; onSaved: () => void }) {
  const [email, setEmail] = useState(account?.email ?? '')
  const [link, setLink] = useState(account?.link ?? '')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState(account?.displayName && account.displayName !== group ? account.displayName : '')
  const { working, reply, run } = useAction()
  useEffect(() => { if (account) { setEmail(account.email); setLink(account.link) } }, [account])

  return (
    <div className={`zoom-group${open ? ' open' : ''}${account ? ' saved' : ''}`}>
      <button type="button" className="zoom-head" onClick={onToggle} aria-expanded={open}>
        <span className="group-chip" style={{ '--hue': groupHue(group) } as React.CSSProperties}>{shortGroup(group)}</span>
        <span className="grow zoom-title"><b>{group}</b><span className="muted">{account ? `${account.email}${account.link ? ' · link saved' : ' · no link yet'}` : 'Not set up yet'}</span></span>
        {account ? <span className="tag ok"><Icon name="check" size={12} /> Saved</span> : <span className="tag">To do</span>}
      </button>
      {open && (
        <form className="form-grid zoom-form" onSubmit={async (e) => {
          e.preventDefault()
          const answer = await run('saveZoom', { group, email, link, password, displayName })
          setPassword('')
          if (answer.ok) onSaved()
        }}>
          <label className="field"><span>Zoom email</span><input type="email" autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
          <label className="field"><span>Zoom password {account?.hasPassword ? '(saved — leave empty to keep it)' : '(optional)'}</span><input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="Only if Zoom asks for it" /></label>
          <label className="field wide"><span>Session link</span><input value={link} onChange={(e) => setLink(e.target.value)} placeholder="https://zoom.us/j/…" /></label>
          <label className="field wide"><span>Name shown in the app (optional)</span><input value={displayName} onChange={(e) => setDisplayName(e.target.value)} placeholder={group} /></label>
          <div className="row-actions start wide">
            <button type="submit" className="btn primary" disabled={working || !email}>{working ? <span className="spinner light" /> : <Icon name="check" size={14} />} Save {shortGroup(group)}</button>
          </div>
          <div className="wide"><Note reply={reply} /></div>
        </form>
      )}
    </div>
  )
}

// ------------------------------------------------------------------ 4. AI

function AiStep({ state, onNext, onSkip }: { state: WelcomeState; onNext: () => void; onSkip: () => void }) {
  const [model, setModel] = useState(state.ai.model)
  const [key, setKey] = useState('')
  const { working, reply, run } = useAction()
  const ready = state.ai.ready

  return (
    <section className="step">
      <Head n={4} title="Your OpenRouter key (optional)" text="The AI matches the names people use in Zoom to your students, and picks who becomes co-host. Paste your own OpenRouter key: it is tested once and kept in Windows Credential Manager on this PC. You can skip this and add it later from AI Matching." />
      {ready && <p className="note ok"><Icon name="check" size={14} /> Connected — OpenRouter / {state.ai.model}</p>}
      <form className="card-box form" onSubmit={async (e) => { e.preventDefault(); await run('testAi', { model, key }); setKey('') }}>
        <label className="field"><span>Model</span>
          <input list="models" value={model} onChange={(e) => setModel(e.target.value)} />
          <datalist id="models">{state.ai.models.map((m) => <option key={m} value={m} />)}</datalist>
        </label>
        <label className="field"><span>OpenRouter API key</span><input type="password" autoComplete="off" value={key} onChange={(e) => setKey(e.target.value)} placeholder="sk-or-…" /></label>
        <p className="muted">Get one at openrouter.ai → Keys. API charges may apply.</p>
        <div className="row-actions start">
          <button type="submit" className="btn primary" disabled={working || !key || !model}>{working ? <span className="spinner light" /> : <Icon name="bolt" size={14} />} Test and save</button>
        </div>
      </form>
      <Note reply={reply} />
      <Footer canNext={ready} onNext={onNext}>
        {!ready && <button type="button" className="btn ghost" onClick={onSkip}>Skip for now</button>}
      </Footer>
    </section>
  )
}

// ------------------------------------------------------------------ done

function DoneStep({ state, aiSkipped }: { state: WelcomeState; aiSkipped: boolean }) {
  const { working, run } = useAction()
  const rows = [
    { ok: Boolean(state.me), text: state.me ? `Signed in as ${state.me.displayName}` : 'Not signed in' },
    { ok: state.lms.accounts.length > 0, text: `${state.lms.accounts.length} LMS account${state.lms.accounts.length === 1 ? '' : 's'}` },
    { ok: state.zoom.length > 0, text: `${state.zoom.length} Zoom account${state.zoom.length === 1 ? '' : 's'} with ${state.zoom.filter((z) => z.link).length} session link${state.zoom.filter((z) => z.link).length === 1 ? '' : 's'}` },
    { ok: state.ai.ready, text: state.ai.ready ? `AI: OpenRouter / ${state.ai.model}` : aiSkipped ? 'AI key skipped — add it later from AI Matching' : 'No AI key' },
  ]
  return (
    <section className="step">
      <header className="stage-head">
        <span className="step-no">All set</span>
        <h1>You're ready</h1>
        <p className="muted">One thing left: import your timetable on the Schedules page, so each class opens by itself at its time.</p>
      </header>
      <ul className="card-box summary">
        {rows.map((r) => <li key={r.text} className={r.ok ? 'ok' : 'skip'}><Icon name={r.ok ? 'check' : 'dot'} size={14} /> {r.text}</li>)}
      </ul>
      <footer className="stage-foot">
        <button type="button" className="btn" disabled={working} onClick={() => run('finish', { page: state.pages.dashboard })}>Open the Dashboard</button>
        <button type="button" className="btn primary" disabled={working} onClick={() => run('finish', { page: state.pages.schedules })}><Icon name="calendar" size={14} /> Import the timetable</button>
      </footer>
    </section>
  )
}
