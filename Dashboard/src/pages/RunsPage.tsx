import { useMemo, useState } from 'react'
import { useDelegations, useRunPlan, useSetDelegation, useUpdateClassPlan } from '../api/hooks'
import type { ClassPlan, Delegation, PreferredEngine, ZoomAccountRef } from '../api/types'
import { PageHeader } from '../components/Layout'
import { useToast } from '../components/Toast'
import { reason } from '../components/UserModals'
import { button, Card, EmptyState, ErrorBanner, input, LoadingRows, Pill, td, th } from '../components/ui'

const COLUMNS = ['Class', 'Coordinator', 'Zoom link', 'Opens with', '']

const ENGINES: { value: PreferredEngine | 'auto'; label: string }[] = [
  { value: 'auto', label: 'Auto' },
  { value: 'desktop', label: 'Zoom app' },
  { value: 'web', label: 'Web' },
]

/** Today, and three weeks out: the window the running PC keeps schedules for. */
function window(): { from: string; to: string } {
  const day = (offset: number) => {
    const date = new Date()
    date.setDate(date.getDate() + offset)
    return date.toISOString().slice(0, 10)
  }
  return { from: day(-1), to: day(21) }
}

function when(item: ClassPlan): string {
  const date = new Date(`${item.date}T00:00:00`)
  const day = date.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })
  return item.startTime ? `${day} · ${item.startTime}` : day
}

/**
 * The admin's own PC opens and finishes classes for the coordinators chosen here, and nothing about
 * a class is typed in twice: their LMS sign-in is kept encrypted on the server (read only for a
 * coordinator turned on here), their timetable is read from their own LMS session list, and the
 * meeting link comes from their own Zoom account for the group - all of it theirs, set up once in
 * their own copy of the app. A class whose group has no Zoom account of theirs is the only one that
 * still asks for a link, and filling it in once carries it across the group.
 */
/**
 * The Zoom account of theirs that opens a group, matched the way the server matches it: the one kept
 * for that group, then the one named after it, then their only account. Several accounts and none
 * for the group means that group cannot open yet - it never borrows another group's.
 */
function hostOf(person: Delegation, group: string): ZoomAccountRef | undefined {
  if (person.groupsWithoutZoom?.includes(group)) return undefined
  const key = group.toLowerCase()
  return (
    person.zoomAccounts.find((account) => (account.group ?? '').toLowerCase() === key) ??
    person.zoomAccounts.find((account) => account.accountId.toLowerCase() === key) ??
    (person.zoomAccounts.length === 1 ? person.zoomAccounts[0] : undefined)
  )
}

export function RunsPage() {
  const delegations = useDelegations()
  const [only, setOnly] = useState<string[]>([])
  const { from, to } = useMemo(window, [])
  const plan = useRunPlan(from, to, only)
  const setDelegation = useSetDelegation()
  const toast = useToast()

  const people = delegations.data?.delegations ?? []
  const running = people.filter((person) => person.enabled)
  const names = new Map(people.map((person) => [person.coordinatorId, person.displayName]))

  function toggle(person: Delegation) {
    setDelegation.mutate(
      {
        coordinatorId: person.coordinatorId,
        enabled: !person.enabled,
        // Every group of theirs runs, each with the Zoom account of theirs that hosts it.
        zoomAccountId: null,
      },
      {
        onSuccess: () =>
          toast.success(
            person.enabled ? `${person.displayName}'s classes are theirs again` : `Running ${person.displayName}'s classes`,
            person.enabled
              ? 'Their classes come off this PC; nothing of theirs opens here any more.'
              : 'Their timetable is read from their own LMS account the next time the app checks.',
          ),
        onError: (error) => toast.error('Could not change that', reason(error)),
      },
    )
  }

  const classes = plan.data?.classes ?? []
  const needLinks = classes.filter((item) => item.needsLink).length

  return (
    <>
      <PageHeader
        title="Run classes"
        description="Classes you open for other coordinators. Each one runs under its own coordinator's Zoom and LMS accounts, so two at the same time never get in each other's way."
      />

      <Card title="Coordinators" className="mb-5">
        {delegations.isLoading ? (
          <LoadingRows columns={3} />
        ) : delegations.error ? (
          <ErrorBanner error={delegations.error} />
        ) : people.length === 0 ? (
          <EmptyState>No coordinator has an account yet.</EmptyState>
        ) : (
          <ul className="divide-y divide-slate-100">
            {people.map((person) => (
              <li key={person.coordinatorId} className="flex flex-wrap items-center justify-between gap-3 px-5 py-3">
                <div className="min-w-56">
                  <p className="text-sm font-medium text-slate-900">
                    {person.displayName}{' '}
                    {person.enabled && <Pill tone="green">Running theirs</Pill>}
                    {person.enabled && !person.lmsAccount && <Pill tone="red">No LMS sign-in</Pill>}
                  </p>
                  <p className="text-xs text-slate-500">
                    {person.groups.length > 0 ? person.groups.map((group) => group.name).join(', ') : 'No groups assigned'}
                    {person.lmsAccount && ` · ${person.lmsAccount.email}`}
                  </p>
                  {person.enabled && person.classes.needsLink > 0 && (
                    <p className="text-xs text-amber-700">{person.classes.needsLink} of their classes still need a Zoom link.</p>
                  )}
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  {/* Every group of theirs, and the one of their own Zoom accounts that opens it -
                      with its link, so nobody types one twice. */}
                  {person.zoomAccounts.length > 0 ? (
                    <ul className="flex flex-wrap gap-1.5" aria-label={`${person.displayName}'s groups and their Zoom accounts`}>
                      {person.groups.filter((group) => !group.archived).map((group) => {
                        const account = hostOf(person, group.name)
                        return (
                          <li key={group.id}>
                            <Pill tone={account ? (account.meetingUrl ? 'green' : 'amber') : 'red'}>
                              {group.name}
                              {account
                                ? `${account.accountId === group.name ? '' : ` · ${account.accountId}`}${account.meetingUrl ? '' : ' — no link yet'}`
                                : ' · no Zoom account'}
                            </Pill>
                          </li>
                        )
                      })}
                    </ul>
                  ) : (
                    <span className="text-xs text-amber-700">
                      No Zoom account of theirs yet — they add it in their own copy of the app.
                    </span>
                  )}
                  <button
                    type="button"
                    className={person.enabled ? button.small : button.smallPrimary}
                    disabled={setDelegation.isPending || (person.status !== 'active' && !person.enabled)}
                    onClick={() => toggle(person)}
                    aria-label={`${person.enabled ? 'Stop running' : 'Run'} ${person.username}'s classes`}
                  >
                    {person.enabled ? 'Stop running theirs' : 'Run their classes'}
                  </button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </Card>

      <Card
        title="Their classes"
        action={
          needLinks > 0 ? <Pill tone="amber">{needLinks} still need a Zoom link</Pill> : undefined
        }
      >
        {running.length > 1 && (
          <div className="flex flex-wrap items-center gap-1.5 border-b border-slate-100 px-4 py-3" role="group" aria-label="Show only these coordinators">
            <button
              type="button"
              className={only.length === 0 ? button.smallPrimary : button.small}
              onClick={() => setOnly([])}
              aria-pressed={only.length === 0}
            >
              Everyone
            </button>
            {running.map((person) => {
              const picked = only.includes(person.coordinatorId)
              return (
                <button
                  key={person.coordinatorId}
                  type="button"
                  className={picked ? button.smallPrimary : button.small}
                  aria-pressed={picked}
                  onClick={() =>
                    setOnly((chosen) =>
                      picked ? chosen.filter((id) => id !== person.coordinatorId) : [...chosen, person.coordinatorId],
                    )
                  }
                >
                  {person.displayName}
                </button>
              )
            })}
          </div>
        )}

        {plan.isLoading ? (
          <LoadingRows columns={COLUMNS.length} />
        ) : plan.error ? (
          <ErrorBanner error={plan.error} />
        ) : classes.length === 0 ? (
          <EmptyState>
            {running.length === 0
              ? 'Turn a coordinator on above and their own timetable is read from the LMS.'
              : 'Nothing listed for the next three weeks yet. The running PC reads each timetable every few hours.'}
          </EmptyState>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full">
              <thead className="border-b border-slate-100">
                <tr>
                  {COLUMNS.map((column) => (
                    <th key={column} className={th} scope="col">
                      {column}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-50">
                {classes.map((item) => (
                  <ClassRow key={item.id} item={item} who={names.get(item.coordinatorId) ?? ''} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  )
}

/** One class: when it is, whose it is, the link it opens and what it opens with. */
function ClassRow({ item, who }: { item: ClassPlan; who: string }) {
  const update = useUpdateClassPlan()
  const toast = useToast()
  const [link, setLink] = useState(item.meetingUrl ?? '')

  function save(applyToGroup: boolean) {
    update.mutate(
      { id: item.id, meetingUrl: link.trim() || null, applyToGroup },
      {
        onSuccess: (saved) =>
          toast.success(
            'Saved',
            applyToGroup && saved.alsoInGroup > 0
              ? `${saved.alsoInGroup} more ${item.group} class(es) use this link too.`
              : `${item.group} on ${item.date} opens with this link.`,
          ),
        onError: (error) => toast.error('Could not save the link', reason(error)),
      },
    )
  }

  const engine = item.preferredEngine ?? 'auto'
  const skipped = item.status === 'skipped'

  return (
    <tr className={skipped ? 'opacity-50' : undefined}>
      <td className={td}>
        <p className="font-medium text-slate-900">{item.group}</p>
        <p className="text-xs text-slate-500">
          {when(item)}
          {item.title ? ` · ${item.title}` : ''}
        </p>
      </td>
      <td className={td}>
        <p className="text-slate-700">{who}</p>
        {item.zoomAccount && <p className="text-xs text-slate-500">{item.zoomAccount}</p>}
      </td>
      <td className={td}>
        <div className="flex flex-wrap items-center gap-1.5">
          <input
            className={`${input} w-64`}
            value={link}
            placeholder="https://zoom.us/j/…"
            aria-label={`Zoom link for ${item.group} on ${item.date}`}
            onChange={(event) => setLink(event.target.value)}
          />
          <button type="button" className={button.small} disabled={update.isPending} onClick={() => save(false)}>
            Save
          </button>
          <button
            type="button"
            className={button.small}
            disabled={update.isPending}
            onClick={() => save(true)}
            title={`Use this link for every ${item.group} class that has not happened yet`}
          >
            Whole group
          </button>
        </div>
        {item.needsLink && <p className="mt-1 text-xs text-amber-700">Without a link this class cannot open by itself.</p>}
      </td>
      <td className={td}>
        <select
          className={input}
          value={engine}
          aria-label={`What ${item.group} on ${item.date} opens with`}
          onChange={(event) =>
            update.mutate(
              { id: item.id, preferredEngine: event.target.value as PreferredEngine | 'auto' },
              { onError: (error) => toast.error('Could not change that', reason(error)) },
            )
          }
        >
          {ENGINES.map((choice) => (
            <option key={choice.value} value={choice.value}>
              {choice.label}
            </option>
          ))}
        </select>
      </td>
      <td className={td}>
        <button
          type="button"
          className={button.small}
          disabled={update.isPending}
          onClick={() =>
            update.mutate(
              { id: item.id, status: skipped ? 'planned' : 'skipped' },
              { onError: (error) => toast.error('Could not change that', reason(error)) },
            )
          }
          aria-label={`${skipped ? 'Run' : 'Skip'} ${item.group} on ${item.date}`}
        >
          {skipped ? 'Run it' : 'Skip'}
        </button>
      </td>
    </tr>
  )
}
