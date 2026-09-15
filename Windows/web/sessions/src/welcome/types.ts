export type Reply = { ok: boolean; message: string }

export type ZoomAccount = { id: string; group: string; displayName: string; email: string; link: string; hasPassword: boolean }

export type WelcomeState = {
  server: string
  busy: boolean
  status: string
  me: { username: string; displayName: string; role: string; allGroups: boolean; groups: string[] } | null
  lms: { onServer: boolean; accounts: { id: string; label: string; email: string; active: boolean }[] }
  zoom: ZoomAccount[]
  ai: { model: string; models: string[]; ready: boolean; status: string }
  pages: { dashboard: number; schedules: number; sessions: number }
}
