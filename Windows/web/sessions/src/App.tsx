import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, inApp, onState } from './bridge'
import type { Action, Row, State } from './types'
import { bucketOf, groupHue, relative, shortGroup, startOf, type Bucket } from './logic'
import { SessionCard } from './components/SessionCard'
import { AssignmentDialog, ConfirmDialog, SettingsPanel, Toasts, type Toast } from './components/Dialogs'
import { Icon } from './components/Icon'
import { DaysPicker, rangeOf, type DayRange } from './components/DaysPicker'

type StatFilter = 'all' | 'running' | 'today' | 'attention' | 'drive' | 'done'
const BUCKETS: { key: Bucket; label: string }[] = [
  { key: 'today', label: 'Today' },
  { key: 'upcoming', label: 'Coming up' },
  { key: 'earlier', label: 'Earlier' },
]

export function App() {
  const [state, setState] = useState<State | null>(null)
  const [now, setNow] = useState(() => new Date())
  const [stat, setStat] = useState<StatFilter>('all')
  const [group, setGroup] = useState<string>('all')
  const [query, setQuery] = useState('')
  const [confirm, setConfirm] = useState<{ row: Row; action: Action } | null>(null)
  const [assignment, setAssignment] = useState<Row | null>(null)
  const [settings, setSettings] = useState(false)
  const [toasts, setToasts] = useState<Toast[]>([])
  const [checking, setChecking] = useState<string | null>(null)
  const [range, setRange] = useState<DayRange & { key: string }>(() => ({ key: '7', ...rangeOf('7') }))

  useEffect(() => {
    const off = onState(setState)
    api.ready().then(() => api.state()).then(setState).catch((e: Error) => toast(false, e.message))
    const tick = setInterval(() => setNow(new Date()), 30_000)
    return () => { off(); clearInterval(tick) }
  }, [])

  const toast = useCallback((ok: boolean, text: string) => {
    const id = Date.now() + Math.random()
    setToasts((all) => [...all.slice(-3), { id, ok, text }])
    setTimeout(() => setToasts((all) => all.filter((t) => t.id !== id)), ok ? 6000 : 12000)
  }, [])

  const run = async (label: string, work: () => Promise<{ ok: boolean; message: string } | boolean | void>) => {
    setChecking(label)
    try {
      const result = await work()
      if (result && typeof result === 'object') toast(result.ok, result.message)
      setState(await api.state())
    } catch (e) {
      toast(false, (e as Error).message)
    } finally {
      setChecking(null)
    }
  }

  const doAction = async (row: Row, action: Action, link?: string) => {
    setConfirm(null)
    try {
      const result = await api.step(row.group, row.date, row.start, action, link)
      toast(result.ok, result.message)
      setState(await api.state())
    } catch (e) {
      toast(false, (e as Error).message)
    }
  }

  const working = useMemo(() => new Set(state?.working ?? []), [state?.working])
  const groups = useMemo(() => [...new Set((state?.rows ?? []).map((r) => r.group))].sort(), [state?.rows])

  const visible = useMemo(() => {
    if (!state) return []
    const q = query.trim().toLowerCase()
    const view = state.view
    return state.rows.filter((r) => {
      if (view && (r.date < view.from || r.date > view.to)) return false
      if (group !== 'all' && r.group !== group) return false
      if (q && !`${r.group} ${r.title} ${r.date} ${r.next}`.toLowerCase().includes(q)) return false
      switch (stat) {
        case 'running': return r.lmsStatus === 'running'
        case 'today': return bucketOf(r, now) === 'today'
        case 'attention': return r.tone === 'bad'
        case 'drive': return r.linkKind === 'zoom'
        case 'done': return r.tone === 'done'
        default: return true
      }
    })
  }, [state, query, group, stat, now])

  const hero = useMemo(() => {
    if (!state) return null
    const rows = [...state.rows].sort((a, b) => startOf(a).getTime() - startOf(b).getTime())
    const live = rows.find((r) => r.lmsStatus === 'running')
    if (live) return { row: live, kind: 'live' as const }
    const next = rows.find((r) => startOf(r) > now)
    return next ? { row: next, kind: 'next' as const } : null
  }, [state, now])

  if (!state) return <div className="loading"><span className="spinner" /> Loading the sessions…</div>

  const stats: { key: StatFilter; label: string; value: number; tone: string; icon: string }[] = [
    { key: 'running', label: 'Running on the LMS', value: state.stats.running, tone: 'blue', icon: 'bolt' },
    { key: 'today', label: 'Classes today', value: state.stats.today, tone: 'violet', icon: 'calendar' },
    { key: 'attention', label: 'Need attention', value: state.stats.attention, tone: 'red', icon: 'alert' },
    { key: 'drive', label: 'Zoom link, Drive pending', value: state.stats.drivePending, tone: 'amber', icon: 'film' },
    { key: 'done', label: 'Fully done', value: state.stats.done, tone: 'green', icon: 'check' },
  ]

  return (
    <div className="page">
      <section className={`hero hero-${hero?.kind ?? 'idle'}`} style={hero ? ({ '--hue': groupHue(hero.row.group) } as React.CSSProperties) : undefined}>
        <div className="hero-copy">
          <span className="eyebrow">{hero?.kind === 'live' ? <><i className="pulse" /> Live on the LMS</> : hero ? 'Next class' : 'No class ahead'}</span>
          {hero ? (
            <>
              <h1><span className="hero-group">{shortGroup(hero.row.group)}</span> {hero.row.title || hero.row.group}</h1>
              <p>{new Date(`${hero.row.date}T${hero.row.start}`).toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' })} · {hero.row.start} · {relative(startOf(hero.row), now)} · {hero.row.next}</p>
            </>
          ) : <h1>Nothing scheduled</h1>}
        </div>
        <div className="hero-tools">
          <div className="account" title={state.account}><Icon name="user" size={14} /> {state.account.split(' — ')[0]}</div>
          <div className="toolbar">
            <button type="button" className="btn glass" disabled={state.busy || checking !== null} onClick={() => run('check', () => api.check(false, range.from, range.to))}>
              {checking === 'check' || state.busy ? <span className="spinner light" /> : <Icon name="refresh" size={15} />} Check LMS
            </button>
            <button type="button" className="btn glass" disabled={state.busy || checking !== null} onClick={() => run('full', () => api.check(true, range.from, range.to))} title="Opens every session of the chosen days: status, record link and attendance. Nothing is changed.">
              {checking === 'full' ? <span className="spinner light" /> : <Icon name="search" size={15} />} Full check · {range.label}
            </button>
            <button type="button" className="btn glass" disabled={checking !== null} onClick={() => run('sheet', () => api.sheetAll())} title="Look every finished class without its Drive link up in the recordings sheet and put the link on the LMS.">
              {checking === 'sheet' ? <span className="spinner light" /> : <Icon name="sheet" size={15} />} Check sheet
            </button>
            <button type="button" className="btn glass icon" aria-label="Settings" onClick={() => setSettings(true)}><Icon name="gear" size={16} /></button>
          </div>
          <p className="read">LMS read {state.lastRead}{!inApp && ' · demo'}</p>
        </div>
      </section>

      <section className="stats" aria-label="Summary; each one filters the list">
        {stats.map((s) => (
          <button key={s.key} type="button" className={`stat t-${s.tone}${stat === s.key ? ' on' : ''}`} aria-pressed={stat === s.key}
            onClick={() => setStat(stat === s.key ? 'all' : s.key)}>
            <span className="stat-icon"><Icon name={s.icon} size={16} /></span>
            <b>{s.value}</b>
            <span>{s.label}</span>
          </button>
        ))}
      </section>

      <div className="filters">
        <div className="chips" role="group" aria-label="Group">
          <button type="button" className={`chip${group === 'all' ? ' on' : ''}`} onClick={() => setGroup('all')}>All groups</button>
          {groups.map((g) => (
            <button key={g} type="button" className={`chip g${group === g ? ' on' : ''}`} style={{ '--hue': groupHue(g) } as React.CSSProperties} onClick={() => setGroup(group === g ? 'all' : g)}>
              <i /> {shortGroup(g)}
            </button>
          ))}
        </div>
        <label className="search"><Icon name="search" size={15} /><input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search classes" aria-label="Search classes" /></label>
        {stat !== 'all' && <button type="button" className="chip clear" onClick={() => setStat('all')}><Icon name="close" size={12} /> {stats.find((s) => s.key === stat)?.label}</button>}
      </div>

      <DaysPicker value={range} onChange={setRange} onShow={(from, to) => run('view', () => api.viewRange(from, to))} />
      {state.view && (
        <div className="view-bar">
          <Icon name="calendar" size={14} /> Showing only {state.view.from} → {state.view.to}
          <button type="button" className="chip small clear" onClick={() => run('view', () => api.viewRange('', '', true))}><Icon name="close" size={12} /> Show the usual weeks</button>
        </div>
      )}
      {state.status && <p className="status">{state.status}</p>}

      {BUCKETS.map(({ key, label }) => {
        const rows = visible.filter((r) => bucketOf(r, now) === key)
          .sort((a, b) => key === 'earlier' ? startOf(b).getTime() - startOf(a).getTime() : startOf(a).getTime() - startOf(b).getTime())
        if (rows.length === 0) return null
        return (
          <section key={key} className="bucket">
            <h2>{label}<span>{rows.length}</span></h2>
            <div className="cards">
              {rows.map((row) => (
                <SessionCard key={row.key} row={row} now={now} working={working}
                  onAction={(r, a) => setConfirm({ row: r, action: a })} onOpen={(url) => api.open(url)}
                  onMaterialFolder={(r, clear) => run('folder', async () => {
                    const result = await api.materialFolder(r.group, r.date, r.start, clear)
                    return result.message ? result : undefined
                  })}
                  onAssignment={(r) => setAssignment(r)} />
              ))}
            </div>
          </section>
        )
      })}
      {visible.length === 0 && (
        <div className="empty">
          <Icon name="calendar" size={28} />
          {state.view ? (
            <>
              <p>Nothing is kept on this PC for {state.view.from} → {state.view.to}{group !== 'all' ? ` (${shortGroup(group)})` : ''}.</p>
              <button type="button" className="btn primary" disabled={state.busy || checking !== null}
                onClick={() => run('check', () => api.check(false, state.view!.from, state.view!.to))}>
                {checking === 'check' || state.busy ? <span className="spinner light" /> : <Icon name="refresh" size={15} />} Read these days from the LMS
              </button>
            </>
          ) : <p>No class matches these filters.</p>}
        </div>
      )}

      {confirm && <ConfirmDialog row={confirm.row} action={confirm.action} onCancel={() => setConfirm(null)} onConfirm={(link) => doAction(confirm.row, confirm.action, link)} />}
      {assignment && (
        <AssignmentDialog row={assignment} onCancel={() => setAssignment(null)}
          pickFile={async () => { const r = await api.assignmentFile(assignment.group, assignment.date); return r.ok ? r : null }}
          onSave={async (draft, none, createNow) => {
            const row = assignment
            setAssignment(null)
            await run('assignment', () => api.setAssignment(row.group, row.date, row.start, draft.title, draft.deadline, none, draft.description, draft.file))
            if (createNow && !none) await doAction(row, 'assignment')
          }} />
      )}
      {settings && (
        <SettingsPanel state={state} onClose={() => setSettings(false)}
          chooseTrack={async (track) => { await run('track', async () => { const r = await api.trackFolder(track); return r.message ? r : undefined }) }}
          saveSheet={async (url, tabs) => { await run('save', () => api.saveSheet(url, tabs)) }}
          useAccount={async (id) => { await run('use', () => api.useAccount(id)) }}
          removeAccount={async (id) => { await run('remove', () => api.removeAccount(id)) }}
          saveAccount={async (l, e, r, p, m) => { await run('account', () => api.saveAccount(l, e, r, p, m)) }} />
      )}
      <Toasts toasts={toasts} dismiss={(id) => setToasts((all) => all.filter((t) => t.id !== id))} />
    </div>
  )
}
