import { useEffect, useState } from 'react'
import { ApiError } from '../api/client'
import { useAttachRecording } from '../api/hooks'
import type { AttachResult, Recording } from '../api/types'
import { formatSessionDate } from '../lib/format'
import { Modal } from './Overlay'
import { attachBlocker, button, LmsStatusBadge, Pill, SourceBadge, Spinner } from './ui'

/** Confirm, then ask the backend to create the attach job; show the job it created. */
export function AttachRecordingModal({ recording, onClose }: { recording: Recording | null; onClose: () => void }) {
  const attach = useAttachRecording()
  const [replaceExisting, setReplaceExisting] = useState(false)
  const [dryRun, setDryRun] = useState(false)
  const [result, setResult] = useState<AttachResult | null>(null)

  useEffect(() => {
    setReplaceExisting(false)
    setDryRun(false)
    setResult(null)
    attach.reset()
  }, [recording?.id])

  if (!recording) return null
  const blocked = result ? null : attachBlocker(recording)

  function confirm() {
    if (!recording) return
    attach.mutate(
      { id: recording.id, options: { replaceExisting, dryRun } },
      // The dialog itself shows the job it created; no toast on top of it.
      { onSuccess: (created) => setResult(created) },
    )
  }

  const failure = attach.error ? describe(attach.error) : null

  return (
    <Modal
      open
      busy={attach.isPending}
      onClose={onClose}
      title={result ? 'Job created' : 'Attach this recording to LMS?'}
      footer={
        result ? (
          <button type="button" className={button.primary} onClick={onClose}>Done</button>
        ) : (
          <>
            <button type="button" className={button.secondary} onClick={onClose} disabled={attach.isPending}>Cancel</button>
            <button type="button" className={button.primary} onClick={confirm} disabled={attach.isPending || Boolean(blocked)}>
              {attach.isPending && <Spinner label="Creating the job" />}
              {attach.isPending ? 'Creating job…' : dryRun ? 'Start dry run' : 'Attach to LMS'}
            </button>
          </>
        )
      }
    >
      <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm">
        <dt className="text-slate-500">Group</dt>
        <dd className="font-medium text-slate-900">{recording.group}</dd>
        <dt className="text-slate-500">Date</dt>
        <dd>{formatSessionDate(recording.date)}{recording.startTime ? ` · ${recording.startTime}` : ''}</dd>
        <dt className="text-slate-500">File name</dt>
        <dd className="break-all">{recording.fileName ?? <span className="text-slate-400">—</span>}</dd>
        <dt className="text-slate-500">Source</dt>
        <dd><SourceBadge recording={recording} /></dd>
        {!result && (
          // Once the job exists the status is about to change; the result below says what happens next.
          <>
            <dt className="text-slate-500">LMS status</dt>
            <dd><LmsStatusBadge recording={recording} /></dd>
          </>
        )}
      </dl>

      {result ? (
        <div className="mt-5 rounded-xl border border-emerald-200 bg-emerald-50/60 p-4 text-sm" role="status">
          <p className="font-medium text-emerald-900">
            {result.dryRun ? 'The dry run is queued. It fills the link box on the LMS but saves nothing.' : 'The attach job is queued.'}
          </p>
          <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1.5">
            <dt className="text-slate-500">Job ID</dt>
            <dd className="break-all font-mono text-xs text-slate-800">{result.jobId}</dd>
            <dt className="text-slate-500">Status</dt>
            <dd><Pill tone="blue">{result.status}</Pill></dd>
          </dl>
          <p className="mt-3 text-slate-600">
            The row shows <strong>Processing</strong> until an agent reports back, then Attached or Failed
            {result.dryRun ? ' (a dry run leaves the LMS status as it is)' : ''}.
          </p>
        </div>
      ) : (
        <>
          <div className="mt-5 space-y-3">
            <label className="flex items-start gap-3 text-sm">
              <input type="checkbox" aria-label="Dry run" className="mt-0.5 size-4 accent-teal-700" checked={dryRun} onChange={(e) => setDryRun(e.target.checked)} />
              <span>
                <span className="font-medium text-slate-800">Dry run</span>
                <span className="block text-slate-500">Open the session and fill in the link, but do not press Save.</span>
              </span>
            </label>
            <label className="flex items-start gap-3 text-sm">
              <input type="checkbox" aria-label="Replace an existing link" className="mt-0.5 size-4 accent-teal-700" checked={replaceExisting} onChange={(e) => setReplaceExisting(e.target.checked)} />
              <span>
                <span className="font-medium text-slate-800">Replace an existing link</span>
                <span className="block text-slate-500">Off: if the session already has a recording link, it is left as it is.</span>
              </span>
            </label>
          </div>
          {recording.lmsStatus === 'attached' && !replaceExisting && (
            <p className="mt-4 rounded-lg bg-sky-50 px-3 py-2 text-sm text-sky-800">
              This recording is already marked as attached. Without “Replace an existing link” the LMS keeps its current link.
            </p>
          )}
          {blocked && <p role="alert" className="mt-4 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800">{blocked}</p>}
          {failure && <p role="alert" className="mt-4 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700">{failure}</p>}
        </>
      )}
    </Modal>
  )
}

function describe(error: Error): string {
  if (error instanceof ApiError && error.details && typeof error.details === 'object' && 'message' in error.details) {
    const details = error.details as { message: string; jobId?: string }
    return details.jobId ? `${details.message.replace(/\.$/, '')} (job ${details.jobId.slice(0, 8)}).` : details.message
  }
  if (error instanceof ApiError && typeof error.details === 'string') return error.details
  return error.message
}
