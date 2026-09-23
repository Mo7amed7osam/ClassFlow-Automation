import { useState } from 'react'
import { useActivity, useGroups } from '../api/hooks'
import type { ActivityItem } from '../api/types'
import { PageHeader } from '../components/Layout'
import { Card, EmptyState, ErrorBanner, input, LoadingRows, Pill, td, th, TimeAgo, type Tone } from '../components/ui'
import { formatDateTime } from '../lib/format'

/** Done, failed, or deliberately skipped - the three things a machine reports. */
export function outcomeTone(outcome: string): Tone {
  return outcome === 'done' ? 'green' : outcome === 'failed' ? 'red' : 'slate'
}

/** "class.ended" -> "Class ended". The kinds are the machines', so this reads whatever arrives. */
export function describeKind(kind: string): string {
  const words = kind.replace(/[._-]+/g, ' ').trim()
  return words.charAt(0).toUpperCase() + words.slice(1)
}

function Detail({ item }: { item: ActivityItem }) {
  const entries = Object.entries(item.detail ?? {}).filter(([, value]) => value !== null && value !== '')
  if (entries.length === 0) return null
  return (
    <dl className="mt-1 flex flex-wrap gap-x-4 gap-y-0.5">
      {entries.slice(0, 6).map(([key, value]) => (
        <div key={key} className="flex gap-1 text-xs text-slate-500">
          <dt>{key}:</dt>
          <dd className="text-slate-600">{typeof value === 'object' ? JSON.stringify(value) : String(value)}</dd>
        </div>
      ))}
    </dl>
  )
}

/**
 * What the machines have done, newest first - the app's Logs page, on the web.
 *
 * Every machine writes down what it did as it did it, and sends those notes whenever the server
 * answers, so this is the record of the work even for a class nobody watched: the meeting opened,
 * the attendance written, a class ended and why, an LMS step that failed. A coordinator sees their
 * own groups; the admin sees every machine.
 */
export function ActivityPage() {
  const [group, setGroup] = useState('')
  const [limit, setLimit] = useState(100)
  const { data, isLoading, error } = useActivity({ group: group || undefined, limit })
  const groups = useGroups()
  const items = data?.items ?? []

  const failures = items.filter((item) => item.outcome === 'failed').length

  return (
    <>
      <PageHeader
        title="What the machines did"
        description="Every class, as it happened: the meeting, the attendance, the LMS steps, and how each one ended."
      />

      <div className="mb-4 flex flex-wrap items-end gap-3">
        <label className="text-sm">
          <span className="mb-1.5 block font-medium text-slate-700">Group</span>
          <select className={input} value={group} onChange={(event) => setGroup(event.target.value)} aria-label="Group">
            <option value="">Every group</option>
            {(groups.data?.groups ?? []).map((item) => (
              <option key={item.id} value={item.group}>{item.group}</option>
            ))}
          </select>
        </label>
        <label className="text-sm">
          <span className="mb-1.5 block font-medium text-slate-700">How much</span>
          <select className={input} value={limit} onChange={(event) => setLimit(Number(event.target.value))} aria-label="How much">
            <option value={100}>The last 100</option>
            <option value={250}>The last 250</option>
            <option value={500}>The last 500</option>
          </select>
        </label>
        {failures > 0 && (
          <p className="ml-auto text-sm text-rose-700">{failures} of these failed.</p>
        )}
      </div>

      <Card title={`${items.length} thing${items.length === 1 ? '' : 's'} done`}>
        {error ? <ErrorBanner error={error} /> : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[44rem]">
              <thead className="border-b border-slate-100 bg-slate-50/60">
                <tr>
                  <th className={th}>When</th>
                  <th className={th}>What</th>
                  <th className={th}>Class</th>
                  <th className={th}>Machine</th>
                  <th className={th}>How it went</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {isLoading && <LoadingRows columns={5} />}
                {!isLoading && items.length === 0 && (
                  <tr><td colSpan={5}><EmptyState>Nothing yet. A machine writes here as soon as it does something.</EmptyState></td></tr>
                )}
                {items.map((item) => (
                  <tr key={item.id} className="hover:bg-slate-50/60">
                    <td className={`${td} whitespace-nowrap text-slate-500`} title={formatDateTime(item.at)}>
                      <TimeAgo iso={item.at} />
                    </td>
                    <td className={td}>
                      <p className="font-medium text-slate-900">{describeKind(item.kind)}</p>
                      {item.summary && <p className="text-xs text-slate-600">{item.summary}</p>}
                      <Detail item={item} />
                    </td>
                    <td className={td}>
                      {item.group ? (
                        <>
                          <p className="text-slate-800">{item.group}</p>
                          {item.date && <p className="text-xs text-slate-500">{item.date}</p>}
                        </>
                      ) : <span className="text-slate-400">—</span>}
                    </td>
                    <td className={`${td} text-slate-600`}>{item.device ?? <span className="text-slate-400">gone</span>}</td>
                    <td className={td}><Pill tone={outcomeTone(item.outcome)}>{item.outcome === 'done' ? 'Done' : item.outcome === 'failed' ? 'Failed' : 'Skipped'}</Pill></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  )
}
