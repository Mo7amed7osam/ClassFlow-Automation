// What DashboardWebView.BuildState sends. Kept in step with that method by hand.
export interface DashState {
  server: { up: boolean; text: string; detail: string; clientMode: boolean }
  status: string
  busy: boolean
  savedLogin: boolean
  known: { username: string; displayName: string; role: string; hasSession: boolean; hasPassword?: boolean; lastUsed: string }[]
  me: null | { username: string; displayName: string; role: string; allGroups: boolean; groups: string[] }
  lms: { active?: string | null; onServer: boolean; canKeepPasswords: boolean; accounts: { id: string; label: string; email: string; role: string }[] }
  sessions: {
    running: number; today: number; attention: number; drivePending: number; done: number; total: number; lastRead: string; status: string
    next: null | { group: string; title: string; date: string; start: string }
    attentionRows: { group: string; title: string; date: string; start: string; next: string }[]
  }
  recordings: null | { total: number; onLms: number; pending: number; drive: number; zoomOnly: number; missing: number }
  users: null | { id: string; username: string; displayName: string; role: string; status: string; lastLogin?: string | null; groups: { id: string; name: string }[] }[]
  groups: null | { id: string; name: string }[]
  allGroups: null | { id: string; name: string; displayName?: string | null; archived: boolean; recordings: number; lastSession?: string | null; pending: number; onLms: number; missing: number; coordinators: string[] }[]
  pages: { sessions: number; recordings: number; coordinators: number; server: number }
}
