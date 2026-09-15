import type { ReactNode } from 'react'
import { useRecordingDetails } from '../api/hooks'
import type { AuditEntry, JobSummary, Recording } from '../api/types'
import { fieldLabel, formatDateTime, formatSessionDate } from '../lib/format'
import { Drawer } from './Overlay'
import { attachBlocker, button, canCancelJob, ErrorBanner, jobTone, LinkStatusBadge, LmsStatusBadge, Pill, SourceBadge, TimeAgo } from './ui'

export function jobUpdatedAt(job: JobSummary): string {
  return job.finishedAt ?? job.startedAt ?? job.assignedAt ?? job.createdAt
}

function outcome(job: JobSummary): string {
  if (job.errorCode) return job.errorCode
  if (job.status === 'succeeded') return job.dryRun ? 'dry run done' : job.alreadyExists ? 'link was already there' : 'link attached'
  return ''
}

/** One recording: its details, its jobs and what admins did to it. Opened from a table row. */
export function RecordingDrawer({ recordingId, onClose, onEdit, onAttach, onCancel }: {
  recordingId: string | null
  onClose: () => void
  onEdit: (recording: Recording) => void
  onAttach: (recording: Recording) => void
  onCancel: (recording: Recording) => void
}) {
  const details = useRecordingDetails(recordingId)
  const recording = details.data?.recording
  const blocked = recording ? attachBlocker(recording) : null

  return (
    <Drawer
      open={recordingId !== null}
      onClose={onClose}
      title={recording ? recording.group : 'Recording'}
      description={recording ? `${formatSessionDate(recording.date)}${recording.startTime ? ` · ${recording.startTime}` : ''}` : undefined}
      footer={
        recording && (
          <>
            <button type="button" className={button.secondary} onClick={() => onEdit(recording)}>Edit</button>
            {canCancelJob(recording) && (
              <button type="button" className={button.secondary.replace('text-slate-700', 'text-rose-700')} onClick={() => onCancel(recording)}>
                Cancel job
              </button>
            )}
            <button type="button" className={button.primary} disabled={Boolean(blocked)} title={blocked ?? undefined} onClick={() => onAttach(recording)}>
              Attach LMS
            </button>
          </>
        )
      }
    >
      {details.error ? (
        <ErrorBanner error={details.error} />
      ) : !recording ? (
        <div className="space-y-3" aria-label="Loading the recording">
          {[1, 2, 3, 4, 5].map((i) => <div key={i} className="h-4 animate-pulse rounded bg-slate-100" />)}
        </div>
      ) : (
        <div className="space-y-6">
          <Section title="Recording">
            <Row label="Group">{recording.group}</Row>
            <Row label="Date">{formatSessionDate(recording.date)}</Row>
            <Row label="Start time">{recording.startTime ?? '—'}</Row>
            <Row label="File name"><span className="break-all">{recording.fileName ?? '—'}</span></Row>
            <Row label="Type">{recording.type ?? '—'}</Row>
            <Row label="Source"><SourceBadge recording={recording} /></Row>
            <Row label="Link status"><LinkStatusBadge status={recording.linkStatus} /></Row>
            <Row label="Link">
              {recording.driveLink || recording.zoomLink ? (
                <a href={(recording.driveLink ?? recording.zoomLink)!} target="_blank" rel="noopener noreferrer" className="font-medium text-teal-700 hover:underline">
                  Open {recording.driveLink ? 'in Drive' : 'in Zoom'} ↗
                </a>
              ) : '—'}
            </Row>
            <Row label="LMS status"><LmsStatusBadge recording={recording} /></Row>
            <Row label="LMS updated">{recording.lmsUpdatedAt ? formatDateTime(recording.lmsUpdatedAt) : '—'}</Row>
            <Row label="Created">{formatDateTime(recording.createdAt)}</Row>
            <Row label="Updated">{formatDateTime(recording.updatedAt)} · <TimeAgo iso={recording.updatedAt} /></Row>
          </Section>

          <Section title="Jobs">
            {details.data!.jobs.length === 0 ? (
              <p className="text-sm text-slate-500">No attach job yet.</p>
            ) : (
              <ul className="divide-y divide-slate-100 rounded-lg border border-slate-200">
                {details.data!.jobs.map((job) => (
                  <li key={job.jobId} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2.5 text-sm">
                    <div className="min-w-0">
                      <p className="font-mono text-xs text-slate-500" title={job.jobId}>{job.jobId}</p>
                      <p className="mt-0.5 text-slate-600">
                        Updated <TimeAgo iso={jobUpdatedAt(job)} />
                        {outcome(job) && <> · {outcome(job)}</>}
                      </p>
                    </div>
                    <div className="flex items-center gap-1.5">
                      {job.dryRun && <Pill tone="slate">dry run</Pill>}
                      <Pill tone={jobTone(job.status)}>{job.status}</Pill>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </Section>

          {details.data!.audit.length > 0 && (
            <Section title="History">
              <ul className="space-y-2 text-sm">
                {details.data!.audit.map((entry, i) => (
                  <li key={i} className="flex justify-between gap-3">
                    <span className="text-slate-700">{describeAudit(entry)}</span>
                    <span className="shrink-0 text-slate-400"><TimeAgo iso={entry.createdAt} /></span>
                  </li>
                ))}
              </ul>
            </Section>
          )}
        </div>
      )}
    </Drawer>
  )
}

function describeAudit(entry: AuditEntry): string {
  const details = entry.details ?? {}
  if (entry.action === 'recording.update') {
    const fields = Array.isArray(details.fields) ? (details.fields as string[]).map(fieldLabel).join(', ') : ''
    return `${entry.username} edited ${fields || 'the recording'}`
  }
  if (entry.action === 'recording.attach') return `${entry.username} started ${details.dryRun ? 'a dry run' : 'an attach job'}`
  if (entry.action === 'recording.cancel') return `${entry.username} cancelled the attach job`
  return `${entry.username}: ${entry.action}`
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section>
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">{title}</h3>
      {children}
    </section>
  )
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="grid grid-cols-[7.5rem_1fr] items-center gap-3 border-b border-slate-100 py-2 text-sm last:border-0">
      <span className="text-slate-500">{label}</span>
      <span className="min-w-0 text-slate-800">{children}</span>
    </div>
  )
}
