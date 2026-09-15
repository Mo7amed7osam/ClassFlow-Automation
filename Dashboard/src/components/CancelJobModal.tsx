import { ApiError } from '../api/client'
import { useCancelRecordingJob } from '../api/hooks'
import type { Recording } from '../api/types'
import { formatSessionDate } from '../lib/format'
import { useToast } from './Toast'
import { ConfirmModal } from './UserModals'

function describe(error: unknown): string {
  if (error instanceof ApiError) {
    const details = error.details as { message?: string } | string | null
    if (details && typeof details === 'object' && details.message) return details.message
    if (typeof details === 'string') return details
  }
  return error instanceof Error ? error.message : 'Something went wrong.'
}

/** "Cancel this attach job?" - only while the job still waits for an agent. */
export function CancelJobModal({ recording, onClose }: { recording: Recording | null; onClose: () => void }) {
  const cancel = useCancelRecordingJob()
  const toast = useToast()
  if (!recording || !recording.lastJob) return null
  const job = recording.lastJob
  const close = () => {
    cancel.reset()
    onClose()
  }

  return (
    <ConfirmModal
      title="Cancel the attach job?"
      confirm={cancel.isPending ? 'Cancelling…' : 'Cancel the job'}
      dismiss="Keep it"
      danger
      busy={cancel.isPending}
      error={cancel.error ? new Error(describe(cancel.error)) : undefined}
      onClose={close}
      onConfirm={() =>
        cancel.mutate(recording.id, {
          onSuccess: () => {
            toast.success(`Cancelled: ${recording.group} · ${formatSessionDate(recording.date)}`, 'Nothing was sent to the LMS. You can attach it again.')
            close()
          },
        })
      }
      body={
        <div className="space-y-3">
          <p>
            <span className="font-medium">{recording.group}</span> · {formatSessionDate(recording.date)}
            {recording.fileName && <span className="block break-all text-slate-500">{recording.fileName}</span>}
          </p>
          <p>
            This job is still waiting for an agent, so nothing has reached the LMS yet. It is{' '}
            <span className="font-medium">{job.dryRun ? 'a dry run' : 'a real attach'}</span>
            {job.replaceExisting ? ', set to replace an existing link' : ''}.
          </p>
          <p className="font-mono text-xs text-slate-500">job {job.jobId}</p>
        </div>
      }
    />
  )
}
