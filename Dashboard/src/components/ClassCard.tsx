import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useRunStage } from '../api/hooks'
import type { SessionClass, SessionStage } from '../api/types'
import { useToast } from './Toast'
import { IconAlertTriangle, IconCalendar, IconClock, IconExternalLink, IconPlay, IconVideo } from './Icons'

interface ClassCardProps {
  item: SessionClass
  onHealthCheck?: () => void
  onSelect?: (planId: string) => void
}

export function ClassCard({ item, onHealthCheck, onSelect }: ClassCardProps) {
  const navigate = useNavigate()
  const toast = useToast()
  const runStage = useRunStage()
  const [retryingKey, setRetryingKey] = useState<string | null>(null)

  // Map 4 pillars: Zoom, Attendance, LMS, Recording
  const zoomStage = item.stages.find((s) => s.key === 'zoom')
  const attStage = item.stages.find((s) => s.key === 'attendance')
  const runStageItem = item.stages.find((s) => s.key === 'run')
  const lateJoinersStage = item.stages.find((s) => s.key === 'lateJoiners')
  const completeStage = item.stages.find((s) => s.key === 'complete')
  const recStage = item.stages.find((s) => s.key === 'drive') || item.stages.find((s) => s.key === 'zoomRecording')

  // Derive Pillar status labels & tones
  const getPillarStatus = (
    stage: SessionStage | undefined,
    fallbackDone: string,
    fallbackRunning: string,
    fallbackWaiting: string
  ) => {
    if (!stage) return { label: 'Not scheduled', tone: 'slate' as const }
    if (stage.state === 'done') return { label: stage.detail || fallbackDone, tone: 'green' as const }
    if (stage.state === 'running') return { label: fallbackRunning, tone: 'blue' as const }
    if (stage.state === 'failed') return { label: stage.detail || 'Failed', tone: 'red' as const }
    if (stage.state === 'blocked') return { label: stage.detail || 'Blocked', tone: 'amber' as const }
    if (stage.state === 'due') return { label: 'Due now', tone: 'amber' as const }
    return { label: stage.detail || fallbackWaiting, tone: 'slate' as const }
  }

  const zoomPillar = getPillarStatus(zoomStage, 'Completed', 'Admitting & Running', 'Opens -15m')
  const attendancePillar = getPillarStatus(attStage, 'Submitted', 'Recording reads', 'Due +90m')
  
  // LMS Pillar (checks run, attendance, late joiners, complete)
  const lmsPillar = (() => {
    if (completeStage?.state === 'done') return { label: 'Completed', tone: 'green' as const }
    if (lateJoinersStage?.state === 'done') return { label: 'Late Correction Done', tone: 'green' as const }
    if (attStage?.state === 'done') return { label: 'Attendance Submitted', tone: 'green' as const }
    if (runStageItem?.state === 'done') return { label: 'Session Started', tone: 'green' as const }
    if (item.stages.some((s) => s.key.startsWith('lms.') && s.state === 'running')) {
      return { label: 'Running task', tone: 'blue' as const }
    }
    const failedLms = item.stages.find((s) => (s.key.startsWith('lms.') || ['run', 'attendance', 'lateJoiners', 'complete'].includes(s.key)) && s.state === 'failed')
    if (failedLms) return { label: `${failedLms.label} Failed`, tone: 'red' as const }
    return { label: 'Scheduled', tone: 'slate' as const }
  })()

  // Recording Pillar
  const recPillar = (() => {
    if (recStage?.state === 'done') return { label: 'Google Drive Attached', tone: 'green' as const }
    if (recStage?.state === 'waiting' || recStage?.state === 'running') return { label: 'Waiting for Drive', tone: 'amber' as const }
    const zoomRec = item.stages.find((s) => s.key === 'zoomRecording')
    if (zoomRec?.state === 'done') return { label: 'Zoom Link Attached', tone: 'blue' as const }
    if (recStage?.state === 'failed') return { label: 'Recording Attachment Failed', tone: 'red' as const }
    return { label: 'Pending class end', tone: 'slate' as const }
  })()

  // Identify next upcoming stage
  const nextStage = item.stages.find((s) => s.state === 'due' || s.state === 'later')
  // Identify last success stage
  const lastSuccessStage = [...item.stages].reverse().find((s) => s.state === 'done')

  // Find any failed or blocked stage
  const failedStage = item.stages.find((s) => s.state === 'failed')
  const blockedStage = item.stages.find((s) => s.state === 'blocked')
  const hasProblem = Boolean(failedStage || blockedStage || item.headline === 'needsAttention' || item.headline === 'blocked')

  // Next actionable stage to run
  const stageToRun = failedStage || item.stages.find((s) => s.state === 'due') || item.stages.find((s) => s.state === 'later')

  const handleRetryFailed = async (stageKey: string) => {
    const stageJobMap: Record<string, string> = {
      zoom: 'class.run',
      run: 'lms.run_session',
      attendance: 'lms.attendance',
      lateJoiners: 'lms.late_joiners',
      complete: 'lms.complete',
      zoomReport: 'zoom.report',
      zoomRecording: 'zoom.recording',
      drive: 'recording.process',
    }
    const jobType = stageJobMap[stageKey]
    if (!jobType) {
      toast.error('Cannot retry', `Unknown job type for stage ${stageKey}`)
      return
    }
    try {
      setRetryingKey(stageKey)
      await runStage.mutateAsync({ planId: item.classPlanId, stage: jobType })
      toast.success('Stage enqueued', `Triggered ${stageKey} for ${item.group}`)
    } catch (e: any) {
      toast.error('Failed to trigger stage', e?.message || 'Server error')
    } finally {
      setRetryingKey(null)
    }
  }

  // Calculate Start -> End time estimate (typically start + 3 hours)
  const formatTimeRange = (start: string | null) => {
    if (!start) return 'Time not set'
    try {
      const [h, m] = start.split(':').map(Number)
      const endH = (h + 3) % 24
      const endStr = `${String(endH).padStart(2, '0')}:${String(m).padStart(2, '0')}`
      return `${start} → ${endStr}`
    } catch {
      return start
    }
  }

  return (
    <div
      className={`rounded-2xl bg-white p-5 shadow-xs transition-all border ring-1 ${
        hasProblem
          ? 'border-rose-300 bg-rose-50/30 dark:border-rose-900/60 dark:bg-rose-950/20 ring-rose-500/10'
          : 'border-slate-200/80 hover:border-slate-300 dark:border-slate-800/80 dark:bg-[#111726] dark:hover:border-slate-700/80 ring-slate-900/5 dark:ring-white/[0.03]'
      }`}
    >
      {/* Top Header */}
      <div className="flex flex-wrap items-start justify-between gap-3 pb-3 border-b border-slate-100 dark:border-slate-800">
        <div>
          <div className="flex items-center gap-2 flex-wrap">
            <Link
              to={`/classes/${item.classPlanId}`}
              className="font-bold text-base text-slate-900 hover:text-indigo-600 dark:text-slate-100 dark:hover:text-indigo-400 tracking-tight"
            >
              {item.group}
            </Link>
            <span
              className={`inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                item.headline === 'running'
                  ? 'bg-emerald-100 text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300 animate-pulse'
                  : item.headline === 'needsAttention'
                  ? 'bg-rose-100 text-rose-800 dark:bg-rose-950 dark:text-rose-300'
                  : item.headline === 'blocked'
                  ? 'bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300'
                  : item.headline === 'done'
                  ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-400'
                  : 'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300'
              }`}
            >
              <span
                className={`size-1.5 rounded-full ${
                  item.headline === 'running'
                    ? 'bg-emerald-500'
                    : item.headline === 'needsAttention'
                    ? 'bg-rose-500'
                    : item.headline === 'blocked'
                    ? 'bg-amber-500'
                    : item.headline === 'done'
                    ? 'bg-emerald-500'
                    : 'bg-slate-400'
                }`}
              />
              {item.headline === 'running'
                ? 'Running Now'
                : item.headline === 'needsAttention'
                ? 'Needs Attention'
                : item.headline === 'blocked'
                ? 'Blocked'
                : item.headline === 'done'
                ? 'Completed'
                : 'Scheduled'}
            </span>
          </div>

          <div className="mt-1 flex items-center gap-3 text-xs text-slate-500 dark:text-slate-400">
            <span className="flex items-center gap-1 font-medium text-slate-700 dark:text-slate-300">
              <IconClock className="size-3.5" />
              {formatTimeRange(item.startTime)}
            </span>
            <span>·</span>
            <span className="flex items-center gap-1">
              <IconCalendar className="size-3.5" />
              {item.date}
            </span>
          </div>
        </div>

        {/* Assigned Account & Meeting link */}
        <div className="flex items-center gap-2 text-xs">
          <span className="inline-flex items-center gap-1 rounded-md bg-slate-100 px-2 py-1 font-medium text-slate-700 dark:bg-slate-800 dark:text-slate-300">
            <IconVideo className="size-3 text-slate-500" />
            {item.coordinator.name || 'Admin'}
          </span>
          {item.meetingUrl ? (
            <a
              href={item.meetingUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1 rounded-md bg-indigo-50 px-2 py-1 font-semibold text-indigo-700 hover:bg-indigo-100 dark:bg-indigo-950/60 dark:text-indigo-300"
              title="Open Zoom meeting in browser"
            >
              Zoom Link
              <IconExternalLink className="size-3" />
            </a>
          ) : (
            <span className="rounded-md bg-rose-50 px-2 py-1 text-[11px] font-semibold text-rose-700 dark:bg-rose-950/50 dark:text-rose-300">
              No Zoom URL
            </span>
          )}
        </div>
      </div>

      {/* 4 Pillars Progress Grid */}
      <div className="mt-4 grid grid-cols-2 gap-2 sm:grid-cols-4">
        {/* Zoom */}
        <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-2.5 dark:border-slate-800/80 dark:bg-slate-800/40">
          <span className="text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider block">
            Zoom
          </span>
          <div className="mt-1 flex items-center gap-1.5">
            <span
              className={`size-2 rounded-full ${
                zoomPillar.tone === 'green'
                  ? 'bg-emerald-500'
                  : zoomPillar.tone === 'blue'
                  ? 'bg-sky-500 animate-pulse'
                  : zoomPillar.tone === 'red'
                  ? 'bg-rose-500'
                  : zoomPillar.tone === 'amber'
                  ? 'bg-amber-500'
                  : 'bg-slate-400'
              }`}
            />
            <span className="text-xs font-semibold text-slate-900 truncate dark:text-slate-100">
              {zoomPillar.label}
            </span>
          </div>
        </div>

        {/* Attendance */}
        <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-2.5 dark:border-slate-800/80 dark:bg-slate-800/40">
          <span className="text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider block">
            Attendance
          </span>
          <div className="mt-1 flex items-center gap-1.5">
            <span
              className={`size-2 rounded-full ${
                attendancePillar.tone === 'green'
                  ? 'bg-emerald-500'
                  : attendancePillar.tone === 'blue'
                  ? 'bg-sky-500 animate-pulse'
                  : attendancePillar.tone === 'red'
                  ? 'bg-rose-500'
                  : attendancePillar.tone === 'amber'
                  ? 'bg-amber-500'
                  : 'bg-slate-400'
              }`}
            />
            <span className="text-xs font-semibold text-slate-900 truncate dark:text-slate-100">
              {attendancePillar.label}
            </span>
          </div>
        </div>

        {/* LMS */}
        <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-2.5 dark:border-slate-800/80 dark:bg-slate-800/40">
          <span className="text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider block">
            LMS
          </span>
          <div className="mt-1 flex items-center gap-1.5">
            <span
              className={`size-2 rounded-full ${
                lmsPillar.tone === 'green'
                  ? 'bg-emerald-500'
                  : lmsPillar.tone === 'blue'
                  ? 'bg-sky-500 animate-pulse'
                  : lmsPillar.tone === 'red'
                  ? 'bg-rose-500'
                  : 'bg-slate-400'
              }`}
            />
            <span className="text-xs font-semibold text-slate-900 truncate dark:text-slate-100">
              {lmsPillar.label}
            </span>
          </div>
        </div>

        {/* Recording */}
        <div className="rounded-xl border border-slate-100 bg-slate-50/70 p-2.5 dark:border-slate-800/80 dark:bg-slate-800/40">
          <span className="text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider block">
            Recording
          </span>
          <div className="mt-1 flex items-center gap-1.5">
            <span
              className={`size-2 rounded-full ${
                recPillar.tone === 'green'
                  ? 'bg-emerald-500'
                  : recPillar.tone === 'blue'
                  ? 'bg-sky-500'
                  : recPillar.tone === 'red'
                  ? 'bg-rose-500'
                  : recPillar.tone === 'amber'
                  ? 'bg-amber-500'
                  : 'bg-slate-400'
              }`}
            />
            <span className="text-xs font-semibold text-slate-900 truncate dark:text-slate-100">
              {recPillar.label}
            </span>
          </div>
        </div>
      </div>

      {/* Problem Alert Banner if any */}
      {hasProblem && (
        <div className="mt-3 rounded-xl border border-rose-300 bg-rose-50/80 p-3 dark:border-rose-900/60 dark:bg-rose-950/40">
          <div className="flex items-start gap-2">
            <IconAlertTriangle className="size-4 shrink-0 text-rose-600 dark:text-rose-400 mt-0.5" />
            <div className="min-w-0 flex-1 text-xs">
              <span className="font-semibold text-rose-900 dark:text-rose-200">
                Action required:
              </span>{' '}
              <span className="text-rose-800 dark:text-rose-300">
                {failedStage?.detail || blockedStage?.detail || 'An automated execution step reported an issue.'}
              </span>
            </div>
            {failedStage && (
              <button
                type="button"
                onClick={() => handleRetryFailed(failedStage.key)}
                disabled={retryingKey === failedStage.key}
                className="shrink-0 rounded-md bg-rose-600 px-2.5 py-1 text-xs font-semibold text-white shadow-xs hover:bg-rose-700 disabled:opacity-50"
              >
                {retryingKey === failedStage.key ? 'Retrying…' : 'Retry Step'}
              </button>
            )}
          </div>
        </div>
      )}

      {/* Operational Next & Last Success Footer */}
      <div className="mt-3.5 flex flex-wrap items-center justify-between gap-2 text-xs text-slate-500 dark:text-slate-400 border-t border-slate-100 pt-3 dark:border-slate-800">
        <div>
          {nextStage && (
            <p>
              <strong className="font-semibold text-slate-700 dark:text-slate-300">Next:</strong>{' '}
              {nextStage.label} {nextStage.detail ? `(${nextStage.detail})` : ''}
            </p>
          )}
          {lastSuccessStage && (
            <p className="text-[11px] text-slate-400 mt-0.5">
              Last success: {lastSuccessStage.label} {lastSuccessStage.detail ? `· ${lastSuccessStage.detail}` : ''}
            </p>
          )}
        </div>

        {/* Action Controls */}
        <div className="flex items-center gap-1.5 flex-wrap">
          <button
            type="button"
            onClick={() => (onSelect ? onSelect(item.classPlanId) : navigate(`/classes/${item.classPlanId}`))}
            className="rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white hover:bg-slate-800 dark:bg-slate-100 dark:text-slate-900 dark:hover:bg-white transition-colors"
          >
            Open details
          </button>

          {onHealthCheck && (
            <button
              type="button"
              onClick={onHealthCheck}
              className="rounded-lg border border-slate-200 px-2.5 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
            >
              Health check
            </button>
          )}

          {stageToRun && (
            <button
              type="button"
              onClick={() => handleRetryFailed(stageToRun.key)}
              disabled={retryingKey === stageToRun.key}
              className="rounded-lg border border-slate-200 px-2.5 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800 flex items-center gap-1"
              title={`Run ${stageToRun.label} now`}
            >
              <IconPlay className="size-3 text-indigo-600" />
              Run next
            </button>
          )}

          <Link
            to={`/attendance?group=${encodeURIComponent(item.group)}`}
            className="rounded-lg border border-slate-200 px-2.5 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
          >
            Attendance
          </Link>
        </div>
      </div>
    </div>
  )
}
