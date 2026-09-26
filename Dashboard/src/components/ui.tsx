import { useEffect, useState, type ReactNode } from 'react'
import { hasActiveJob } from '../api/hooks'
import type { LinkStatus, Recording } from '../api/types'
import { formatDateTime, timeAgo } from '../lib/format'

export type Tone = 'green' | 'amber' | 'red' | 'blue' | 'slate'

const TONES: Record<Tone, string> = {
  green:
    'bg-emerald-50 text-emerald-700 ring-emerald-600/20 dark:bg-emerald-500/10 dark:text-emerald-400 dark:ring-emerald-500/20',
  amber:
    'bg-amber-50 text-amber-800 ring-amber-600/25 dark:bg-amber-500/10 dark:text-amber-400 dark:ring-amber-500/20',
  red:
    'bg-rose-50 text-rose-700 ring-rose-600/20 dark:bg-rose-500/10 dark:text-rose-400 dark:ring-rose-500/20',
  blue:
    'bg-sky-50 text-sky-700 ring-sky-600/20 dark:bg-sky-500/10 dark:text-sky-400 dark:ring-sky-500/20',
  slate:
    'bg-slate-100 text-slate-600 ring-slate-500/20 dark:bg-slate-800/80 dark:text-slate-300 dark:ring-slate-700/60',
}

export function Pill({ tone, children, title }: { tone: Tone; children: ReactNode; title?: string }) {
  return (
    <span
      title={title}
      className={`inline-flex items-center gap-1.5 whitespace-nowrap rounded-full px-2.5 py-0.5 text-[11px] font-semibold ring-1 ring-inset ${TONES[tone]}`}
    >
      {children}
    </span>
  )
}

export function Dot({ tone }: { tone: Tone }) {
  const colour = {
    green: 'bg-emerald-500 dark:bg-emerald-400',
    amber: 'bg-amber-500 dark:bg-amber-400',
    red: 'bg-rose-500 dark:bg-rose-400',
    blue: 'bg-sky-500 dark:bg-sky-400',
    slate: 'bg-slate-400 dark:bg-slate-500',
  }[tone]
  return <span aria-hidden className={`inline-block size-1.5 rounded-full ${colour}`} />
}

/** "Drive · pending", "Drive · on LMS", "Zoom only · pending", "Missing · pending". */
export function linkTone(status: LinkStatus): Tone {
  if (status.lms === 'failed' || status.link === 'missing') return 'red'
  if (status.lms === 'attached') return 'green'
  if (status.link === 'zoom') return 'blue'
  return 'amber'
}

export function LinkStatusBadge({ status }: { status: LinkStatus }) {
  const tone = linkTone(status)
  return (
    <Pill tone={tone} title="Which link is stored, and where the recording stands on the LMS">
      <Dot tone={tone} />
      {status.label}
    </Pill>
  )
}

/** What the LMS column shows: the stored status, or "processing" while an attach job is in flight. */
export type LmsDisplay = 'pending' | 'attached' | 'failed' | 'processing' | (string & {})

export function displayLmsStatus(recording: Recording): LmsDisplay {
  return hasActiveJob(recording) ? 'processing' : recording.lmsStatus
}

const LMS_BADGES: Record<string, { tone: Tone; label: string; title: string }> = {
  pending: { tone: 'amber', label: 'Pending', title: 'Not on the LMS yet' },
  attached: { tone: 'green', label: 'Attached', title: 'The link is on the LMS session' },
  failed: { tone: 'red', label: 'Failed', title: 'The last attach job failed' },
  processing: { tone: 'blue', label: 'Processing', title: 'An attach job is queued or running' },
}

export function LmsStatusBadge({ recording }: { recording: Recording }) {
  const status = displayLmsStatus(recording)
  const badge = LMS_BADGES[status] ?? { tone: 'slate' as Tone, label: status, title: status }
  const dry = status === 'processing' && recording.lastJob?.dryRun
  return (
    <Pill tone={badge.tone} title={dry ? 'A dry run is queued or running (it saves nothing)' : badge.title}>
      <Dot tone={badge.tone} />
      {badge.label}
      {dry && <span className="font-normal opacity-75">· dry run</span>}
    </Pill>
  )
}

const SOURCE_BADGES: Record<string, { tone: Tone; label: string; title: string }> = {
  drive: { tone: 'green', label: 'Drive', title: 'A Google Drive link is stored' },
  zoom: { tone: 'blue', label: 'Zoom', title: 'Only a Zoom link is stored' },
  missing: { tone: 'red', label: 'Missing', title: 'No link yet' },
}

export function SourceBadge({ recording }: { recording: Recording }) {
  const badge = SOURCE_BADGES[recording.linkStatus.link]
  return <Pill tone={badge.tone} title={badge.title}>{badge.label}</Pill>
}

/** A job's status colour, the same on every page. */
export function jobTone(status: string): Tone {
  return ({ running: 'blue', assigned: 'amber', queued: 'slate', succeeded: 'green', failed: 'red', cancelled: 'slate' } as Record<string, Tone>)[status] ?? 'slate'
}

/** Its attach job still waits in the queue, so it can be cancelled (an agent has not taken it). */
export function canCancelJob(recording: Recording): boolean {
  return recording.lastJob?.status === 'queued'
}

/** Why a recording cannot be attached right now, or null if it can. */
export function attachBlocker(recording: Recording): string | null {
  if (recording.linkStatus.link === 'missing') return 'This recording has no link yet. Add its Google Drive link first.'
  if (recording.linkStatus.link === 'zoom') return 'Only Google Drive links can be attached. Add the Drive link first.'
  if (hasActiveJob(recording)) return 'An attach job for this recording is already queued or running.'
  return null
}

export const button = {
  primary:
    'inline-flex h-9 items-center justify-center gap-2 rounded-xl bg-indigo-600 px-4 text-xs font-semibold text-white shadow-xs hover:bg-indigo-500 active:bg-indigo-700 disabled:cursor-not-allowed disabled:opacity-50 transition-colors',
  danger:
    'inline-flex h-9 items-center justify-center gap-2 rounded-xl bg-rose-600 px-4 text-xs font-semibold text-white shadow-xs hover:bg-rose-500 active:bg-rose-700 disabled:cursor-not-allowed disabled:opacity-50 transition-colors',
  secondary:
    'inline-flex h-9 items-center justify-center gap-2 rounded-xl border border-slate-200 bg-white px-4 text-xs font-medium text-slate-700 shadow-xs hover:bg-slate-50 dark:border-slate-800 dark:bg-[#161d2f] dark:text-slate-200 dark:hover:bg-[#1e273e] disabled:cursor-not-allowed disabled:opacity-50 transition-colors',
  small:
    'inline-flex h-7 shrink-0 items-center justify-center whitespace-nowrap rounded-lg border border-slate-200 bg-white px-2.5 text-xs font-medium text-slate-700 hover:bg-slate-50 dark:border-slate-800 dark:bg-[#161d2f] dark:text-slate-200 dark:hover:bg-[#1e273e] disabled:cursor-not-allowed disabled:opacity-40 transition-colors',
  smallDanger:
    'inline-flex h-7 shrink-0 items-center justify-center whitespace-nowrap rounded-lg border border-rose-200 bg-white px-2.5 text-xs font-medium text-rose-700 hover:bg-rose-50 dark:border-rose-900/60 dark:bg-rose-950/40 dark:text-rose-400 dark:hover:bg-rose-900/60 disabled:cursor-not-allowed disabled:opacity-40 transition-colors',
  smallPrimary:
    'inline-flex h-7 shrink-0 items-center justify-center whitespace-nowrap rounded-lg bg-indigo-600 px-2.5 text-xs font-semibold text-white hover:bg-indigo-500 dark:bg-indigo-600 dark:hover:bg-indigo-500 disabled:cursor-not-allowed disabled:bg-slate-300 dark:disabled:bg-slate-800 transition-colors',
}

export function Spinner({ label = 'Loading' }: { label?: string }) {
  return <span role="status" aria-label={label} className="inline-block size-4 animate-spin rounded-full border-2 border-current border-r-transparent" />
}

/** Re-renders every 30 s so "3 min ago" stays true while the page is open. */
export function useNow(intervalMs = 30_000): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(timer)
  }, [intervalMs])
  return now
}

export function TimeAgo({ iso }: { iso: string | null | undefined }) {
  const now = useNow()
  if (!iso) return <span className="text-slate-400">never</span>
  return (
    <time dateTime={iso} title={`${formatDateTime(iso)} (Cairo)`} className="whitespace-nowrap">
      {timeAgo(iso, now)}
    </time>
  )
}

export function Card({ title, action, children, className = '' }: { title?: ReactNode; action?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`rounded-2xl border border-slate-200/80 bg-white shadow-xs dark:border-slate-800/80 dark:bg-[#111726] dark:shadow-none ring-1 ring-slate-900/5 dark:ring-white/[0.03] transition-colors ${className}`}>
      {(title || action) && (
        <header className="flex items-center justify-between gap-4 border-b border-slate-100 dark:border-slate-800/80 px-5 py-3.5">
          <h2 className="text-sm font-bold text-slate-900 dark:text-slate-100 tracking-tight">{title}</h2>
          {action}
        </header>
      )}
      {children}
    </section>
  )
}

export function StatCard({ label, value, hint, tone = 'slate' }: { label: string; value: ReactNode; hint?: ReactNode; tone?: Tone }) {
  const accent = {
    green: 'text-emerald-600 dark:text-emerald-400',
    amber: 'text-amber-600 dark:text-amber-400',
    red: 'text-rose-600 dark:text-rose-400',
    blue: 'text-sky-600 dark:text-sky-400',
    slate: 'text-slate-900 dark:text-slate-100',
  }[tone]
  return (
    <div className="rounded-2xl border border-slate-200/80 bg-white p-4 shadow-xs dark:border-slate-800/80 dark:bg-[#111726] dark:shadow-none ring-1 ring-slate-900/5 dark:ring-white/[0.03] transition-colors">
      <p className="text-[11px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-400">{label}</p>
      <p className={`mt-1.5 text-2xl font-bold tracking-tight tabular-nums ${accent}`}>{value}</p>
      {hint && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{hint}</p>}
    </div>
  )
}

export function ErrorBanner({ error }: { error: unknown }) {
  const message = error instanceof Error ? error.message : 'Something went wrong.'
  return (
    <div role="alert" className="mx-5 my-4 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-xs font-medium text-rose-700 dark:border-rose-900/60 dark:bg-rose-950/40 dark:text-rose-400">
      {message}
    </div>
  )
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <div className="px-5 py-10 text-center text-xs text-slate-500 dark:text-slate-400">{children}</div>
}

export function LoadingRows({ columns, rows = 5 }: { columns: number; rows?: number }) {
  return (
    <>
      {Array.from({ length: rows }, (_, row) => (
        <tr key={row} className="animate-pulse">
          {Array.from({ length: columns }, (_, column) => (
            <td key={column} className="px-4 py-3">
              <div className="h-3.5 rounded bg-slate-100 dark:bg-slate-800" />
            </td>
          ))}
        </tr>
      ))}
    </>
  )
}

export const th =
  'px-4 py-3 text-left text-[11px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-400 bg-slate-50/80 dark:bg-[#0c111d] border-b border-slate-100 dark:border-slate-800/80 whitespace-nowrap'
export const td =
  'px-4 py-3.5 text-xs align-middle text-slate-700 dark:text-slate-200 border-b border-slate-100/70 dark:border-slate-800/50'
export const input =
  'h-9 rounded-xl border border-slate-200/80 bg-white px-3 text-xs text-slate-900 shadow-xs dark:border-slate-800 dark:bg-[#0c111d] dark:text-slate-100 focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 transition-colors'
