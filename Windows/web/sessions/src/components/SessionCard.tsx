import { useEffect, useRef, useState } from 'react'
import type { Action, Row, Step, StepKey } from '../types'
import { ACTIONS, availableActions, dayNumber, groupHue, month, primaryAction, relative, shortGroup, startOf, weekday, workingKey } from '../logic'
import { Icon } from './Icon'

const STEP_ICON: Record<StepKey, string> = { zoom: 'video', run: 'play', attendance: 'people', correct: 'late', complete: 'flag', record: 'film', drive: 'drive', material: 'sheet', assignment: 'calendar' }
const STATE_ICON: Record<Step['state'], string> = { done: 'check', lms: 'check', partial: 'half', due: 'clock', retry: 'retry', failed: 'x', future: 'dot', none: 'dot' }
const STEP_ACTIONS: Partial<Record<StepKey, Action[]>> = {
  run: ['run'], attendance: ['attendance'], correct: ['correct'], complete: ['complete'], record: ['recording', 'zoomRecording', 'link'], drive: ['recording', 'sheet', 'link'],
  material: ['material'], assignment: ['assignment'],
}
const LMS_LABEL: Record<string, string> = { running: 'Running', finished: 'Finished', completed: 'Finished', pending: 'Pending', '': 'Not read' }

interface Props {
  row: Row
  now: Date
  working: Set<string>
  onAction: (row: Row, action: Action) => void
  onOpen: (url: string) => void
  /** Choose (or, with clear, remove) the folder this class's material comes from. */
  onMaterialFolder: (row: Row, clear?: boolean) => void
  /** Open the assignment's title and deadline. */
  onAssignment: (row: Row) => void
}

export function SessionCard({ row, now, working, onAction, onOpen, onMaterialFolder, onAssignment }: Props) {
  const [openStep, setOpenStep] = useState<StepKey | null>(null)
  const [menu, setMenu] = useState(false)
  const menuRef = useRef<HTMLDivElement>(null)
  const primary = primaryAction(row, now)
  const actions = availableActions(row, now)
  const busy = actions.some((a) => working.has(workingKey(row, a)))
  const hue = groupHue(row.group)
  const start = startOf(row)
  const live = row.lmsStatus === 'running'

  useEffect(() => {
    if (!menu && !openStep) return
    const close = (e: MouseEvent) => {
      if (!(e.target as HTMLElement).closest(`[data-card="${CSS.escape(row.key)}"] .popover, [data-card="${CSS.escape(row.key)}"] .menu-wrap`)) {
        setMenu(false)
        setOpenStep(null)
      }
    }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [menu, openStep, row.key])

  return (
    <article className={`card tone-${row.tone}${busy ? ' is-busy' : ''}`} data-card={row.key} style={{ '--hue': hue } as React.CSSProperties}>
      <div className="date-tile">
        <span className="dow">{weekday(row)}</span>
        <span className="dnum">{dayNumber(row)}</span>
        <span className="mon">{month(row)}</span>
      </div>

      <div className="card-main">
        <header className="card-head">
          <span className="group-chip" title={row.group}>{shortGroup(row.group)}</span>
          <h3>{row.title || row.group}</h3>
          <span className="time"><Icon name="clock" size={13} /> {row.start}<em>{relative(start, now)}</em></span>
          <span className={`lms-pill lms-${row.lmsStatus || 'unknown'}`}>
            {live && <i className="pulse" />}LMS · {LMS_LABEL[row.lmsStatus] ?? row.lmsStatus}
          </span>
        </header>

        <ol className="track" aria-label="Where this class stands" style={{ '--steps': row.steps.length } as React.CSSProperties}>
          {row.steps.map((step) => {
            const inFlight = (STEP_ACTIONS[step.key] ?? []).some((a) => working.has(workingKey(row, a)))
            return (
              <li key={step.key} className={`node s-${inFlight ? 'working' : step.state}`}>
                <button type="button" className="node-btn" onClick={() => setOpenStep(openStep === step.key ? null : step.key)}
                  aria-expanded={openStep === step.key} title={step.detail ?? step.text}>
                  <span className="orb">
                    {inFlight ? <span className="spinner" /> : <Icon name={step.state === 'future' || step.state === 'none' ? STEP_ICON[step.key] : STATE_ICON[step.state]} size={14} stroke={2.4} />}
                  </span>
                  <span className="node-label">{step.label}</span>
                  <span className="node-text">{inFlight ? 'Working…' : step.text}</span>
                </button>
                {openStep === step.key && (
                  <div className="popover" role="dialog" aria-label={step.label}>
                    <strong><Icon name={STEP_ICON[step.key]} size={14} /> {step.label}</strong>
                    <p>{step.detail || step.text}</p>
                    {step.key === 'material' && row.material && row.material.skipped.length > 0 && (
                      <p className="skipped">Not accepted by the LMS (PDF, ZIP, PowerPoint only): {row.material.skipped.join(', ')}</p>
                    )}
                    <div className="popover-actions">
                      {(STEP_ACTIONS[step.key] ?? []).filter((a) => actions.includes(a)).map((a) => (
                        <button key={a} type="button" className="btn small" disabled={working.has(workingKey(row, a))}
                          onClick={() => { setOpenStep(null); onAction(row, a) }}>{ACTIONS[a].label}</button>
                      ))}
                      {step.key === 'material' && (
                        <button type="button" className="btn small" onClick={() => { setOpenStep(null); onMaterialFolder(row) }}>
                          {row.material?.technical ? 'Choose folder…' : 'Other folder…'}
                        </button>
                      )}
                      {step.key === 'material' && row.material?.folder && !row.material.fixed && (
                        <button type="button" className="btn small ghost" onClick={() => { setOpenStep(null); onMaterialFolder(row, true) }}>Remove folder</button>
                      )}
                      {step.key === 'assignment' && step.state !== 'done' && (
                        <button type="button" className="btn small" onClick={() => { setOpenStep(null); onAssignment(row) }}>
                          <Icon name="calendar" size={13} /> {row.material?.assignmentTitle ? 'Deadline…' : 'Add assignment…'}
                        </button>
                      )}
                    </div>
                  </div>
                )}
              </li>
            )
          })}
        </ol>
      </div>

      <div className="card-side">
        <span className={`next next-${row.tone}`}>{row.next}</span>
        <div className="side-actions">
          {primary && (
            <button type="button" className="btn primary" disabled={working.has(workingKey(row, primary))} onClick={() => onAction(row, primary)}>
              {working.has(workingKey(row, primary)) ? <span className="spinner light" /> : <Icon name="bolt" size={14} />}
              {ACTIONS[primary].label}
            </button>
          )}
          {(
            <div className="menu-wrap" ref={menuRef}>
              <button type="button" className="btn icon" aria-label="More steps for this class" aria-expanded={menu} onClick={() => setMenu(!menu)}>
                <Icon name="more" size={18} stroke={3} />
              </button>
              {menu && (
                <div className="menu" role="menu">
                  {actions.map((a) => (
                    <button key={a} type="button" role="menuitem" disabled={working.has(workingKey(row, a))} onClick={() => { setMenu(false); onAction(row, a) }}>
                      <Icon name={a === 'run' ? 'play' : a === 'attendance' ? 'people' : a === 'correct' ? 'late' : a === 'complete' ? 'flag' : a === 'zoomRecording' || a === 'recording' ? 'film' : a === 'sheet' || a === 'material' ? 'sheet' : a === 'assignment' ? 'calendar' : 'link'} size={15} />
                      {ACTIONS[a].label}
                    </button>
                  ))}
                  {!row.material?.done && (
                    <button type="button" role="menuitem" onClick={() => { setMenu(false); onMaterialFolder(row) }}>
                      <Icon name="sheet" size={15} /> {row.material?.technical ? 'Choose material folder…' : 'Material from another folder…'}
                    </button>
                  )}
                  <button type="button" role="menuitem" onClick={() => { setMenu(false); onAssignment(row) }}>
                    <Icon name="calendar" size={15} /> Assignment…
                  </button>
                  {row.lmsUrl && (
                    <button type="button" role="menuitem" onClick={() => { setMenu(false); onOpen(row.lmsUrl!) }}>
                      <Icon name="open" size={15} /> Open on the LMS
                    </button>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </article>
  )
}
