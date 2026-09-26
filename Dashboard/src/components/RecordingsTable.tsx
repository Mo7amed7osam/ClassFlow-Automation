import type { MouseEvent } from 'react'
import type { Recording } from '../api/types'
import { formatSessionDate } from '../lib/format'
import { attachBlocker, button, canCancelJob, EmptyState, LmsStatusBadge, LoadingRows, SourceBadge, TimeAgo, td, th } from './ui'

interface Props {
  items: Recording[] | undefined
  loading: boolean
  empty?: string
  /** Row click opens the details. */
  onSelect?: (recording: Recording) => void
  onEdit?: (recording: Recording) => void
  onAttach?: (recording: Recording) => void
  onCancel?: (recording: Recording) => void
}

/**
 * The recordings table. With action handlers it is the operations table (Edit / Attach LMS, the
 * row opens the details); without them it is the read-only list the overview shows.
 * On narrow screens it scrolls sideways with the Actions column kept in view.
 */
export function RecordingsTable({ items, loading, empty = 'No recordings yet.', onSelect, onEdit, onAttach, onCancel }: Props) {
  const operations = Boolean(onEdit || onAttach)
  const columns = ['Group', 'Date', 'Start time', 'File name', 'Source', 'LMS status', 'Updated', operations ? 'Actions' : '']

  return (
    <div className="overflow-x-auto">
      <table className="min-w-full divide-y divide-slate-100 dark:divide-slate-800">
        <thead className="bg-slate-50/70 dark:bg-[#0c111d]">
          <tr>
            {columns.map((column, i) => (
              <th
                key={column || i}
                scope="col"
                className={`${th} ${i === columns.length - 1 && operations ? 'sticky right-0 bg-slate-50 dark:bg-[#0c111d] text-right shadow-[-8px_0_8px_-8px_rgb(15_23_42/0.12)]' : ''}`}
              >
                {column}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
          {loading && !items ? (
            <LoadingRows columns={columns.length} />
          ) : (
            items?.map((recording) => (
              <Row key={recording.id} recording={recording} operations={operations} onSelect={onSelect} onEdit={onEdit} onAttach={onAttach} onCancel={onCancel} />
            ))
          )}
        </tbody>
      </table>
      {!loading && items?.length === 0 && <EmptyState>{empty}</EmptyState>}
    </div>
  )
}

function Row({ recording, operations, onSelect, onEdit, onAttach, onCancel }: { recording: Recording; operations: boolean } & Omit<Props, 'items' | 'loading' | 'empty'>) {
  const link = recording.driveLink ?? recording.zoomLink
  const blocked = attachBlocker(recording)
  const stop = (handler?: (r: Recording) => void) => (event: MouseEvent) => {
    event.stopPropagation()
    handler?.(recording)
  }

  return (
    <tr className={`group hover:bg-slate-50/60 dark:hover:bg-slate-800/40 ${onSelect ? 'cursor-pointer' : ''}`} onClick={onSelect ? () => onSelect(recording) : undefined}>
      <td className={`${td} font-medium text-slate-900 dark:text-slate-100`}>
        {onSelect ? (
          // The row is clickable; this button is the same action for keyboards and screen readers.
          <button type="button" onClick={stop(onSelect)} className="text-left hover:text-indigo-600 dark:hover:text-indigo-400 hover:underline" aria-label={`Details of ${recording.group} on ${recording.date}`}>
            {recording.group}
          </button>
        ) : (
          recording.group
        )}
      </td>
      <td className={`${td} whitespace-nowrap text-slate-700 dark:text-slate-300`}>{formatSessionDate(recording.date)}</td>
      <td className={`${td} font-mono tabular-nums text-slate-700 dark:text-slate-300`}>{recording.startTime ?? <span className="text-slate-400">—</span>}</td>
      <td className={`${td} max-w-56 truncate text-slate-700 dark:text-slate-300`} title={recording.fileName ?? undefined}>
        {recording.fileName ?? <span className="text-slate-400">—</span>}
      </td>
      <td className={td}>
        <SourceBadge recording={recording} />
      </td>
      <td className={td}>
        <LmsStatusBadge recording={recording} />
      </td>
      <td className={`${td} text-slate-500 dark:text-slate-400`}>
        <TimeAgo iso={recording.updatedAt} />
      </td>
      {operations ? (
        <td className={`${td} sticky right-0 bg-white dark:bg-[#111726] text-right shadow-[-8px_0_8px_-8px_rgb(15_23_42/0.12)] group-hover:bg-slate-50 dark:group-hover:bg-[#161d2f]/60`}>
          <div className="flex justify-end gap-1.5">
            {onEdit && (
              <button type="button" className={button.small} onClick={stop(onEdit)} aria-label={`Edit ${recording.group} on ${recording.date}`}>
                Edit
              </button>
            )}
            {onCancel && canCancelJob(recording) && (
              <button
                type="button"
                className={button.smallDanger}
                onClick={stop(onCancel)}
                title="Cancel the attach job while it is still waiting for an agent"
                aria-label={`Cancel the attach job of ${recording.group} on ${recording.date}`}
              >
                Cancel
              </button>
            )}
            {onAttach && (
              <button
                type="button"
                className={button.smallPrimary}
                disabled={Boolean(blocked)}
                title={blocked ?? 'Attach the stored Google Drive link to this LMS session'}
                onClick={stop(onAttach)}
                aria-label={`Attach ${recording.group} on ${recording.date} to its LMS session`}
              >
                Attach LMS
              </button>
            )}
          </div>
        </td>
      ) : link ? (
        <td className={`${td} text-right`}>
          <a href={link} target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 dark:hover:text-indigo-300 text-xs font-medium hover:underline">Open</a>
        </td>
      ) : null}
    </tr>
  )
}
