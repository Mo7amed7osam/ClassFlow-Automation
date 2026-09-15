export interface Student { id: string; order: number; name: string; aliases: string[]; email: string }
export interface Group { id: string; name: string; mine: boolean; students: Student[] }

export interface RosterState {
  groups: Group[]
  /** Groups this person can bring from the LMS (a coordinator's own, or every group for the admin). */
  lmsGroups: string[]
  account: { label: string; email: string } | null
  me: { name: string; role: string } | null
  /** The group being read from the LMS right now. */
  reading: string | null
  status: string
  pages: { attendance: number; dashboard: number }
}

export interface Reply { ok: boolean; message: string; group?: string; added?: number }
