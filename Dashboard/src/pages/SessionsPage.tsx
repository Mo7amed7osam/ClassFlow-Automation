import { useMemo, useState } from 'react'
import { useSessions } from '../api/hooks'
import type { SessionClass, SessionStage, SessionsPage as Page, StageState } from '../api/types'
import { Card, Pill, Spinner, StatCard, button } from '../components/ui'

/**
 * Every class and where it stands: Zoom, the LMS, attendance, Complete and its record link.
 *
 * The card is a row of stages, and what matters is that it tells the truth about each one. A stage
 * nothing implements yet is drawn differently from a stage that has simply not come round - both
 * are "not done", and showing them the same way is how a person comes to believe a class was
 * handled when nothing touched it.
 */

/** How each state is drawn, and what it means. Kept together so no two states can drift apart. */
const LOOK: Record<StageState, { ring: string; fill: string; text: string; title: string }> = {
  done: { ring: 'border-emerald-500', fill: 'bg-emerald-500', text: 'text-white', title: 'Done' },
  running: { ring: 'border-blue-500', fill: 'bg-blue-50', text: 'text-blue-600', title: 'Running now' },
  waiting: { ring: 'border-blue-400', fill: 'bg-white', text: 'text-blue-500', title: 'Queued, waiting for a worker' },
  due: { ring: 'border-blue-400', fill: 'bg-white', text: 'text-blue-500', title: 'Due now' },
  later: { ring: 'border-slate-200', fill: 'bg-white', text: 'text-slate-300', title: 'Not yet' },
  failed: { ring: 'border-red-400', fill: 'bg-red-50', text: 'text-red-600', title: 'Failed' },
  blocked: { ring: 'border-amber-400', fill: 'bg-amber-50', text: 'text-amber-600', title: 'Cannot run yet' },
  // Dashed, and never grey like "later": this stage does not exist in the cloud at all.
  missing: { ring: 'border-slate-200 border-dashed', fill: 'bg-white', text: 'text-slate-300', title: 'Not built for the cloud yet' },
}

const HEADLINE: Record<SessionClass['headline'], { tone: Parameters<typeof Pill>[0]['tone']; label: string }> = {
  running: { tone: 'blue', label: 'Running' },
  needsAttention: { tone: 'red', label: 'Needs attention' },
  blocked: { tone: 'amber', label: 'Waiting on something' },
  done: { tone: 'green', label: 'Fully done' },
  planned: { tone: 'slate', label: 'Planned' },
}

const DAY_RANGES = [
  { label: 'Today', days: 0 },
  { label: '3 days', days: 2 },
  { label: '7 days', days: 6 },
  { label: '14 days', days: 13 },
  { label: '30 days', days: 29 },
]

function isoDay(offsetDays: number): string {
  const now = new Date()
  now.setDate(now.getDate() + offsetDays)
  return now.toISOString().slice(0, 10)
}

function StageMark({ stage }: { stage: SessionStage }) {
  const look = LOOK[stage.state]
  // Done, but not clean. A class that was held and whose meeting could not be closed is drawn as a
  // tick everywhere else, and reads as a class that ran perfectly - so the ring says otherwise.
  const warned = stage.state === 'done' && Boolean(stage.warning)
  const ring = warned ? 'border-amber-400' : look.ring
  const text = warned ? 'text-amber-600' : look.text
  const fill = warned ? 'bg-amber-50' : look.fill
  return (
    <div className="flex min-w-20 flex-col items-center gap-1.5 text-center">
      <span
        className={`flex h-8 w-8 items-center justify-center rounded-full border-2 ${ring} ${fill} ${text}`}
        title={stage.detail ? `${warned ? 'Done, with a problem' : look.title} — ${stage.detail}` : look.title}
        aria-label={`${stage.label}: ${warned ? 'done, with a problem' : look.title}${
          stage.detail ? `, ${stage.detail}` : ''
        }`}
      >
        {stage.state === 'done' ? (
          <svg viewBox="0 0 20 20" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden>
            <path d="M5 10.5l3.5 3.5L15 7" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        ) : stage.state === 'failed' ? (
          <svg viewBox="0 0 20 20" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden>
            <path d="M6 6l8 8M14 6l-8 8" strokeLinecap="round" />
          </svg>
        ) : stage.state === 'missing' ? (
          <span className="text-[11px] leading-none">–</span>
        ) : (
          <svg viewBox="0 0 20 20" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
            <circle cx="10" cy="10" r="6.5" />
            <path d="M10 6.5V10l2.5 1.5" strokeLinecap="round" />
          </svg>
        )}
      </span>
      <span className="text-[11px] font-medium leading-tight text-slate-700">{stage.label}</span>
      <span
        className={`text-[10px] leading-tight ${
          stage.state === 'failed'
            ? 'text-red-600'
            : stage.state === 'blocked' || warned
              ? 'text-amber-700'
              : 'text-slate-400'
        }`}
      >
        {stage.detail ?? stage.caption}
      </span>
    </div>
  )
}

function ClassCard({ row }: { row: SessionClass }) {
  const headline = HEADLINE[row.headline]
  const when = row.startTime ?? '—'
  const day = new Date(`${row.date}T00:00:00`)

  return (
    <article className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div className="flex flex-wrap items-start gap-3">
        <div className="flex w-14 shrink-0 flex-col items-center rounded-lg bg-slate-50 px-2 py-1.5 text-center">
          <span className="text-[10px] font-semibold uppercase tracking-wide text-slate-500">
            {day.toLocaleDateString(undefined, { weekday: 'short' })}
          </span>
          <span className="text-lg font-semibold leading-none text-slate-900">{day.getDate()}</span>
          <span className="text-[10px] text-slate-500">
            {day.toLocaleDateString(undefined, { month: 'short' })}
          </span>
        </div>

        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <Pill tone="blue">{row.group}</Pill>
            <span className="truncate font-medium text-slate-900">{row.title ?? 'Session'}</span>
            <span className="text-sm text-slate-500">{when}</span>
            <Pill tone={headline.tone}>{headline.label}</Pill>
          </div>
          <p className="mt-0.5 text-xs text-slate-500">
            {row.coordinator.name ?? 'Unassigned'}
            {row.meetingUrl ? '' : ' · no Zoom link yet'}
          </p>
        </div>
      </div>

      {/* The stages, in the order the class runs. Scrolls sideways on a narrow screen rather
          than wrapping, so the order stays readable as a line. */}
      <div className="mt-4 flex items-start gap-1 overflow-x-auto pb-1">
        {row.stages.map((stage) => (
          <StageMark key={stage.key} stage={stage} />
        ))}
      </div>
    </article>
  )
}

export function SessionsPage() {
  const [days, setDays] = useState(6)
  const [group, setGroup] = useState<string>('')
  const [search, setSearch] = useState('')

  const params = useMemo(
    () => ({ from: isoDay(0), to: isoDay(days), ...(group ? { group } : {}) }),
    [days, group],
  )
  const sessions = useSessions(params)
  const page: Page | undefined = sessions.data

  const shown = useMemo(() => {
    const rows = page?.classes ?? []
    const needle = search.trim().toLowerCase()
    if (!needle) return rows
    return rows.filter(
      (r) =>
        r.group.toLowerCase().includes(needle) ||
        (r.title ?? '').toLowerCase().includes(needle) ||
        (r.coordinator.name ?? '').toLowerCase().includes(needle),
    )
  }, [page, search])

  const today = isoDay(0)
  const todays = shown.filter((r) => r.date === today)
  const coming = shown.filter((r) => r.date !== today)

  return (
    <div className="space-y-4">
      <header>
        <h1 className="text-xl font-semibold text-slate-900">Sessions</h1>
        <p className="text-sm text-slate-500">
          Every class and where it stands: Zoom, the LMS, attendance, Complete and its record link.
        </p>
      </header>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
        <StatCard label="Running on the LMS" value={page?.counters.runningOnTheLms ?? '—'} tone="blue" />
        <StatCard label="Classes today" value={page?.counters.classesToday ?? "—"} tone="blue" />
        <StatCard label="Need attention" value={page?.counters.needAttention ?? '—'} tone="red" />
        <StatCard label="Waiting on something" value={page?.counters.blocked ?? '—'} tone="amber" hint="Zoom link, account" />
        <StatCard label="Fully done" value={page?.counters.fullyDone ?? '—'} tone="green" />
      </div>

      <Card>
        <div className="flex flex-wrap items-center gap-2 border-b border-slate-100 px-4 py-3">
          <button
            type="button"
            className={group === '' ? button.smallPrimary : button.small}
            onClick={() => setGroup('')}
          >
            All groups
          </button>
          {(page?.groups ?? []).map((name) => (
            <button
              key={name}
              type="button"
              className={group === name ? button.smallPrimary : button.small}
              onClick={() => setGroup(name)}
            >
              {name}
            </button>
          ))}
          <input
            className="ml-auto w-56 rounded-lg border border-slate-200 px-3 py-1.5 text-sm"
            placeholder="Search classes"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            aria-label="Search classes"
          />
        </div>

        <div className="flex flex-wrap items-center gap-2 border-b border-slate-100 px-4 py-2">
          <span className="text-xs font-semibold uppercase tracking-wide text-slate-400">Days</span>
          {DAY_RANGES.map((range) => (
            <button
              key={range.label}
              type="button"
              className={days === range.days ? button.smallPrimary : button.small}
              onClick={() => setDays(range.days)}
            >
              {range.label}
            </button>
          ))}
        </div>

        <div className="space-y-4 px-4 py-4">
          {sessions.isLoading ? (
            <Spinner label="Reading the classes" />
          ) : sessions.isError ? (
            <p className="text-sm text-red-600">The classes could not be read.</p>
          ) : shown.length === 0 ? (
            <p className="text-sm text-slate-500">
              No classes in this window. A class appears here once a coordinator's timetable has been read.
            </p>
          ) : (
            <>
              {todays.length > 0 && (
                <section className="space-y-3">
                  <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-400">
                    Today <span className="ml-1 text-slate-300">{todays.length}</span>
                  </h2>
                  {todays.map((row) => (
                    <ClassCard key={row.classPlanId} row={row} />
                  ))}
                </section>
              )}
              {coming.length > 0 && (
                <section className="space-y-3">
                  <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-400">
                    Coming up <span className="ml-1 text-slate-300">{coming.length}</span>
                  </h2>
                  {coming.map((row) => (
                    <ClassCard key={row.classPlanId} row={row} />
                  ))}
                </section>
              )}
            </>
          )}
        </div>
      </Card>
    </div>
  )
}
