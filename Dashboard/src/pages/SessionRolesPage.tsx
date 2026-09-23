import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { useMe, useSaveSessionRoles, useSessionRoles } from '../api/hooks'
import type { RolePerson, SessionRoleProfile } from '../api/types'
import { PageHeader } from '../components/Layout'
import { useToast } from '../components/Toast'
import { button, Card, EmptyState, ErrorBanner, input, Pill, Spinner, td, th, TimeAgo } from '../components/ui'
import { Field } from './ZoomAccountsPage'

/** "Nada Instructor, Mona Samir" -> two people of that role. Blank lines and repeats are dropped. */
export function parsePeople(text: string, role: RolePerson['role']): RolePerson[] {
  const seen = new Set<string>()
  const people: RolePerson[] = []
  for (const part of text.split(/[,;\n]/)) {
    const name = part.trim()
    if (!name || seen.has(name.toLowerCase())) continue
    seen.add(name.toLowerCase())
    people.push({ name, role })
  }
  return people
}

export const listPeople = (profile: SessionRoleProfile, role: RolePerson['role']) =>
  profile.people.filter((person) => person.role === role)

const namesOf = (profile: SessionRoleProfile, role: RolePerson['role']) =>
  listPeople(profile, role).map((person) => person.name).join(', ')

interface Draft {
  sessionType: string
  keywords: string
  accounts: string
  instructors: string
  coHosts: string
}

const empty: Draft = { sessionType: '', keywords: '', accounts: '', instructors: '', coHosts: '' }

const split = (text: string) => text.split(/[,;\n]/).map((part) => part.trim()).filter(Boolean)

/**
 * Who is made co-host when a class starts - the app's Session Roles page, on the web.
 *
 * A machine holding a class watches who comes in, recognises the instructor by these names, and
 * makes them co-host; the same person leaving near the end is what tells it the lesson is over. The
 * list lives on the server so every machine and every worker uses the same one, and the app sends
 * what it has here whenever the admin saves it there.
 *
 * A profile with no words and no groups covers every meeting, which is the simple way to name one
 * instructor for everything.
 */
export function SessionRolesPage() {
  const { data: me } = useMe()
  const { data, isLoading, error } = useSessionRoles()
  const save = useSaveSessionRoles()
  const toast = useToast()
  const isAdmin = me?.role === 'admin'
  const profiles = data?.value?.profiles ?? []

  const [editing, setEditing] = useState<string | null>(null)
  const [draft, setDraft] = useState<Draft>(empty)
  const [submitted, setSubmitted] = useState(false)

  const was = profiles.find((profile) => profile.sessionType === editing) ?? null
  const isNew = editing === ''

  const problem =
    !draft.sessionType.trim() ? 'A name for the kind of session is required, for example Technical.'
      : isNew && profiles.some((profile) => profile.sessionType.toLowerCase() === draft.sessionType.trim().toLowerCase())
        ? 'There is already a profile with that name.'
        : parsePeople(draft.instructors, 'Instructor').length + parsePeople(draft.coHosts, 'CoHost').length === 0
          ? 'Name at least one person who may be made co-host.'
          : null

  function open(profile: SessionRoleProfile | null) {
    setSubmitted(false)
    setEditing(profile ? profile.sessionType : '')
    setDraft(profile
      ? {
        sessionType: profile.sessionType,
        keywords: profile.keywords.join(', '),
        accounts: profile.accounts.join(', '),
        instructors: namesOf(profile, 'Instructor'),
        coHosts: namesOf(profile, 'CoHost'),
      }
      : empty)
  }

  function sendAll(next: SessionRoleProfile[], done: string) {
    save.mutate({ profiles: next }, {
      onSuccess: () => { setEditing(null); setSubmitted(false); toast.success(done) },
    })
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (problem) return
    const profile: SessionRoleProfile = {
      sessionType: draft.sessionType.trim(),
      keywords: split(draft.keywords),
      accounts: split(draft.accounts),
      // The aliases a machine learnt for somebody are kept: they are how an unfamiliar Zoom display
      // name is recognised next time, and retyping a name here must not throw them away.
      people: [...parsePeople(draft.instructors, 'Instructor'), ...parsePeople(draft.coHosts, 'CoHost')].map((person) => {
        const known = was?.people.find((old) => old.name.toLowerCase() === person.name.toLowerCase())
        return known?.aliases?.length ? { ...person, aliases: known.aliases } : person
      }),
    }
    const others = profiles.filter((other) => other.sessionType !== editing)
    sendAll([...others, profile], isNew ? `${profile.sessionType} added.` : `${profile.sessionType} saved.`)
  }

  function remove(profile: SessionRoleProfile) {
    if (!window.confirm(`Remove ${profile.sessionType}? Nobody will be made co-host in those classes.`)) return
    sendAll(profiles.filter((other) => other.sessionType !== profile.sessionType), `${profile.sessionType} removed.`)
  }

  const failure = save.error instanceof ApiError && typeof save.error.details === 'string' ? save.error.details : save.error?.message

  return (
    <>
      <PageHeader
        title="Who is made co-host"
        description="A class recognises its instructor by these names and makes them co-host as they arrive."
        action={isAdmin ? <button type="button" className={button.primary} onClick={() => open(null)}>Add a session type</button> : undefined}
      />

      {save.isError && <p role="alert" className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{failure}</p>}
      {!isAdmin && (
        <p className="mb-4 rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600">
          These are shared by everyone, so only the admin changes them.
        </p>
      )}

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <Card
          title={`${profiles.length} session type${profiles.length === 1 ? '' : 's'}`}
          action={data?.updatedAt ? <span className="text-xs text-slate-500">saved <TimeAgo iso={data.updatedAt} /></span> : null}
        >
          {error ? <ErrorBanner error={error} /> : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[40rem]">
                <thead className="border-b border-slate-100 bg-slate-50/60">
                  <tr>
                    <th className={th}>Session type</th>
                    <th className={th}>Recognised by</th>
                    <th className={th}>Instructor</th>
                    <th className={th}>Also co-host</th>
                    <th className={th}><span className="sr-only">Actions</span></th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {isLoading && (
                    <tr><td colSpan={5}><EmptyState>Loading…</EmptyState></td></tr>
                  )}
                  {!isLoading && profiles.length === 0 && (
                    <tr><td colSpan={5}><EmptyState>Nobody is made co-host yet. Add a session type and name its instructor.</EmptyState></td></tr>
                  )}
                  {profiles.map((profile) => {
                    const everyMeeting = profile.keywords.length === 0 && profile.accounts.length === 0
                    return (
                      <tr key={profile.sessionType} className="hover:bg-slate-50/60">
                        <td className={td}>
                          <p className="font-medium text-slate-900">{profile.sessionType}</p>
                          {everyMeeting && <Pill tone="blue">Every meeting</Pill>}
                        </td>
                        <td className={`${td} text-slate-600`}>
                          {everyMeeting ? <span className="text-slate-400">nothing in particular</span> : (
                            <>
                              {profile.keywords.length > 0 && <p className="text-xs">words: {profile.keywords.join(', ')}</p>}
                              {profile.accounts.length > 0 && <p className="text-xs">groups: {profile.accounts.join(', ')}</p>}
                            </>
                          )}
                        </td>
                        <td className={td}>
                          {listPeople(profile, 'Instructor').length === 0 ? <span className="text-slate-400">—</span> : (
                            <span className="flex flex-wrap gap-1">
                              {listPeople(profile, 'Instructor').map((person) => <Pill key={person.name} tone="green">{person.name}</Pill>)}
                            </span>
                          )}
                        </td>
                        <td className={td}>
                          {listPeople(profile, 'CoHost').length === 0 ? <span className="text-slate-400">—</span> : (
                            <span className="flex flex-wrap gap-1">
                              {listPeople(profile, 'CoHost').map((person) => <Pill key={person.name} tone="slate">{person.name}</Pill>)}
                            </span>
                          )}
                        </td>
                        <td className={`${td} text-right`}>
                          {isAdmin && (
                            <span className="inline-flex gap-1.5">
                              <button type="button" className={button.small} onClick={() => open(profile)} aria-label={`Edit ${profile.sessionType}`}>Edit</button>
                              <button type="button" className={button.smallDanger} onClick={() => remove(profile)} disabled={save.isPending} aria-label={`Remove ${profile.sessionType}`}>Remove</button>
                            </span>
                          )}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </Card>

        {editing !== null && isAdmin && (
          <Card title={isNew ? 'New session type' : `Edit ${editing}`}>
            <form onSubmit={submit} noValidate aria-label="Session type" className="flex flex-col gap-4 px-5 py-4">
              <Field label="Session type" hint="What you call this kind of class, for example Technical or Soft skills.">
                <input className={`${input} w-full`} value={draft.sessionType} maxLength={100} onChange={(e) => setDraft({ ...draft, sessionType: e.target.value })} />
              </Field>
              <Field label="Words in the class's name" hint="Any of these in the class's name means this kind. Leave empty with no groups below to cover every meeting.">
                <input className={`${input} w-full`} value={draft.keywords} onChange={(e) => setDraft({ ...draft, keywords: e.target.value })} />
              </Field>
              <Field label="Groups that always mean this kind" hint="Whatever the class's name says. Separate with commas.">
                <input className={`${input} w-full`} value={draft.accounts} onChange={(e) => setDraft({ ...draft, accounts: e.target.value })} />
              </Field>
              <Field label="Instructor" hint="Who teaches it. They are made co-host, and their leaving near the end says the lesson is over.">
                <textarea className={`${input} h-auto w-full py-2`} rows={2} value={draft.instructors} onChange={(e) => setDraft({ ...draft, instructors: e.target.value })} />
              </Field>
              <Field label="Anybody else who may be co-host" hint="Made co-host too, but their leaving does not end the class.">
                <textarea className={`${input} h-auto w-full py-2`} rows={2} value={draft.coHosts} onChange={(e) => setDraft({ ...draft, coHosts: e.target.value })} />
              </Field>

              {submitted && problem && <p className="text-xs text-rose-700">{problem}</p>}
              <div className="flex gap-2">
                <button type="submit" className={button.primary} disabled={save.isPending}>
                  {save.isPending && <Spinner label="Saving" />}
                  {save.isPending ? 'Saving…' : 'Save'}
                </button>
                <button type="button" className={button.secondary} onClick={() => setEditing(null)}>Cancel</button>
              </div>
            </form>
          </Card>
        )}
      </div>
    </>
  )
}
