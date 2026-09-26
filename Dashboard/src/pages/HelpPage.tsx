import { useState } from 'react'
import { PageHeader } from '../components/Layout'
import { Card } from '../components/ui'
import {
  IconAlertTriangle,
  IconAttendance,
  IconCalendar,
  IconClock,
  IconFilm,
  IconGoogle,
  IconGroups,
  IconLMS,
  IconLive,
  IconOverview,
  IconSparkles,
  IconVideo,
} from '../components/Icons'

type HelpTopic =
  | 'getting-started'
  | 'daily-operation'
  | 'lifecycle'
  | 'groups'
  | 'schedules'
  | 'zoom-setup'
  | 'lms-setup'
  | 'google-sheets'
  | 'attendance'
  | 'ai-matching'
  | 'recordings'
  | 'troubleshooting'

export function HelpPage() {
  const [topic, setTopic] = useState<HelpTopic>('getting-started')

  const TOPICS: { key: HelpTopic; label: string; icon: any }[] = [
    { key: 'getting-started', label: 'Getting Started', icon: IconOverview },
    { key: 'daily-operation', label: 'Daily Operation', icon: IconClock },
    { key: 'lifecycle', label: 'Class Lifecycle Flow', icon: IconLive },
    { key: 'groups', label: 'Groups & Rosters', icon: IconGroups },
    { key: 'schedules', label: 'Schedules & Timetable', icon: IconCalendar },
    { key: 'zoom-setup', label: 'Zoom Setup (G1/G2)', icon: IconVideo },
    { key: 'lms-setup', label: 'LMS Portal Integration', icon: IconLMS },
    { key: 'google-sheets', label: 'Google Sheets Setup', icon: IconGoogle },
    { key: 'attendance', label: 'Attendance Workflow', icon: IconAttendance },
    { key: 'ai-matching', label: 'AI Name Matching', icon: IconSparkles },
    { key: 'recordings', label: 'Recording Lifecycle', icon: IconFilm },
    { key: 'troubleshooting', label: 'Troubleshooting & FAQ', icon: IconAlertTriangle },
  ]

  return (
    <div className="space-y-6 pb-12">
      <PageHeader
        title="ClassFlow Operator Guide & Help Center"
        description="Comprehensive documentation for managing automated Zoom admittance, portal session lifecycle, attendance reconciliation, and Google Drive archival."
      />

      <div className="grid gap-6 lg:grid-cols-4">
        {/* Navigation Sidebar */}
        <div className="rounded-2xl border border-slate-200/90 bg-white p-3 shadow-xs dark:border-slate-800 dark:bg-slate-900 space-y-1">
          {TOPICS.map((item) => {
            const Icon = item.icon
            const active = topic === item.key
            return (
              <button
                key={item.key}
                type="button"
                onClick={() => setTopic(item.key)}
                className={`w-full flex items-center gap-3 rounded-xl px-3 py-2.5 text-xs font-bold text-left transition-colors ${
                  active
                    ? 'bg-indigo-600 text-white'
                    : 'text-slate-600 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-slate-200'
                }`}
              >
                <Icon className="size-4 shrink-0" />
                <span>{item.label}</span>
              </button>
            )
          })}
        </div>

        {/* Content Panel */}
        <div className="lg:col-span-3">
          <Card>
            {topic === 'getting-started' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  Getting Started with ClassFlow
                </h3>
                <p>
                  ClassFlow is a fully automated operations platform that runs live Zoom classes, admits students from the waiting room, tracks attendance, synchronizes portal records, and archives video recordings to Google Drive without requiring manual intervention from an operator or instructor.
                </p>
                <div className="rounded-xl border border-slate-100 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-800/40 space-y-2">
                  <h4 className="font-bold text-slate-900 dark:text-slate-100">The 3-Step Setup Checklist:</h4>
                  <ol className="list-decimal list-inside space-y-1.5 pl-1">
                    <li><strong className="text-slate-900 dark:text-slate-100">Add Zoom Web Profiles:</strong> Configure your G1 and G2 profiles in Zoom Accounts.</li>
                    <li><strong className="text-slate-900 dark:text-slate-100">Configure LMS Credentials:</strong> Store your encrypted portal account in LMS Accounts.</li>
                    <li><strong className="text-slate-900 dark:text-slate-100">Import Timetable & Groups:</strong> Pull session schedules and student rosters under Groups & Students.</li>
                  </ol>
                </div>
              </div>
            )}

            {topic === 'daily-operation' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  Daily Operator Experience
                </h3>
                <p>
                  As an operator, your daily workflow follows the <strong>5-Second Rule</strong>:
                </p>
                <blockquote className="rounded-xl border-l-4 border-indigo-600 bg-indigo-50/50 p-4 font-medium text-indigo-950 dark:bg-indigo-950/20 dark:text-indigo-200">
                  "If everything is green: do nothing.<br />
                  If an issue occurs: ClassFlow alerts you immediately on the Overview page with exact reason, automated retry state, and a 1-click resolution button."
                </blockquote>
                <p>
                  You do not need to keep a Mac running, open Zoom desktop apps, SSH into servers, or monitor technical terminal output. Everything is autonomously handled on the cloud worker and verified in this dashboard.
                </p>
              </div>
            )}

            {topic === 'lifecycle' && (
              <div className="space-y-6 text-xs text-slate-700 dark:text-slate-300">
                <div>
                  <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                    The 8-Stage Autonomous Class Lifecycle
                  </h3>
                  <p className="mt-1">
                    Every scheduled class occurrence progresses through these sequential stages:
                  </p>
                </div>

                <div className="relative pl-6 space-y-4 border-l-2 border-indigo-200 dark:border-indigo-900">
                  {[
                    { step: '1. Before Class (-15m)', desc: 'Pre-flight check verifies Zoom link, worker availability, and credentials.' },
                    { step: '2. Start Zoom (-15m)', desc: 'Worker opens Zoom web profile, starts meeting, and continuously admits students.' },
                    { step: '3. Run Session (0m)', desc: 'Automated Playwright worker presses "Run Session" on the LMS portal.' },
                    { step: '4. Attendance (+90m)', desc: 'Snapshots are matched against group roster and submitted to the portal.' },
                    { step: '5. Correction (+180m)', desc: 'Late joiners who arrived in the second half are updated to Joined on LMS.' },
                    { step: '6. Complete Class (+190m)', desc: 'Meeting ends cleanly and LMS portal session is marked Complete.' },
                    { step: '7. Zoom Recording (+210m)', desc: 'Zoom cloud recording share link is found and attached temporarily to LMS.' },
                    { step: '8. Drive Recording (08:00)', desc: 'Permanent Google Drive video replaces the Zoom link via automated sheet sync.' },
                  ].map((s, idx) => (
                    <div key={idx} className="relative">
                      <span className="absolute -left-[31px] top-1 size-3 rounded-full bg-indigo-600 ring-4 ring-white dark:ring-slate-900" />
                      <h4 className="font-bold text-slate-900 dark:text-slate-100">{s.step}</h4>
                      <p className="text-slate-500 dark:text-slate-400 mt-0.5">{s.desc}</p>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {topic === 'groups' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  Managing Groups & Rosters
                </h3>
                <p>
                  Groups correspond to official training cohorts (e.g. <code>CAI5_IND1_G1</code>). Each group has an official roster of enrolled students.
                </p>
                <ul className="list-disc list-inside space-y-1.5 pl-1">
                  <li><strong>Importing Rosters:</strong> Supports pasting names, uploading CSV, or Excel files.</li>
                  <li><strong>Aliases:</strong> If a student logs into Zoom under a nickname (e.g. "M. Ahmed" for "Mohamed Ahmed"), ClassFlow permanently remembers the alias once confirmed.</li>
                  <li><strong>Ignores:</strong> Host, instructor, and tech support accounts can be added to the Ignore list so they are never counted as students.</li>
                </ul>
              </div>
            )}

            {topic === 'schedules' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  Schedules & Class Timetable
                </h3>
                <p>
                  Class schedules can be viewed in Calendar view or Table view. Times are always stored in UTC and presented in <strong>Africa/Cairo (UTC+3)</strong> local time.
                </p>
                <p>
                  ClassFlow features a schedule editor that preserves selected accounts, day-of-week recurrence, and meeting URLs without dropdown reset bugs.
                </p>
              </div>
            )}

            {topic === 'zoom-setup' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  Zoom Profiles (G1 / G2)
                </h3>
                <p>
                  To host concurrent classes, ClassFlow separates Zoom web profiles into <strong>G1</strong> and <strong>G2</strong>:
                </p>
                <div className="grid gap-3 sm:grid-cols-2">
                  <div className="rounded-xl border border-slate-200 p-3 dark:border-slate-800">
                    <h4 className="font-bold text-slate-900 dark:text-slate-100">Profile G1</h4>
                    <p className="mt-1 text-slate-500">Dedicated browser instance for primary group sessions.</p>
                  </div>
                  <div className="rounded-xl border border-slate-200 p-3 dark:border-slate-800">
                    <h4 className="font-bold text-slate-900 dark:text-slate-100">Profile G2</h4>
                    <p className="mt-1 text-slate-500">Isolated secondary browser profile for concurrent evening classes.</p>
                  </div>
                </div>
              </div>
            )}

            {topic === 'lms-setup' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  LMS Portal Integration
                </h3>
                <p>
                  Portal credentials are encrypted using authenticated AES-256-GCM. Passwords are never echoed back over the API or displayed in the browser.
                </p>
                <p>
                  You can use the <strong>Test Login</strong> action on the LMS page to verify that your account can successfully authenticate against the portal.
                </p>
              </div>
            )}

            {topic === 'google-sheets' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  Google Sheets Recording Synchronization
                </h3>
                <div className="rounded-xl border border-emerald-200 bg-emerald-50/70 p-3 text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-200 font-medium">
                  Important Guarantee: ClassFlow only reads this spreadsheet. It never modifies or writes back to it.
                </div>
                <p className="mt-2">
                  Every morning at <strong>08:00 Cairo</strong>, ClassFlow inspects the linked spreadsheet for newly recorded Google Drive links. Once found, it automatically attaches them to the corresponding completed portal sessions.
                </p>
              </div>
            )}

            {topic === 'attendance' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  Attendance Tracking & Review
                </h3>
                <p>
                  During the class, the worker captures Zoom participant presence in periodic snapshots. Evidence is strictly recorded as <em>First Observed</em>, <em>Last Observed</em>, and <em>Observation Count</em> without inventing unobserved join/leave timestamps. Names are normalized and matched against official group rosters using:
                </p>
                <ol className="list-decimal list-inside space-y-1 pl-1">
                  <li><strong>Exact Match:</strong> Official spelling matches observed name.</li>
                  <li><strong>Remembered Alias:</strong> Matches a previously accepted operator assignment.</li>
                  <li><strong>Fuzzy Match:</strong> High-confidence similarity score.</li>
                  <li><strong>AI Match:</strong> LLM semantic evaluation of transliterated names.</li>
                </ol>
                <p>
                  Any participant below confidence thresholds is highlighted in <strong>Needs Review</strong>.
                </p>
              </div>
            )}

            {topic === 'ai-matching' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  AI-Assisted Name Matching (OpenRouter)
                </h3>
                <p>
                  When student names in Arabic are transliterated in English or entered with informal spelling, ClassFlow can query OpenRouter AI to determine the best match from the official roster.
                </p>
                <p>
                  AI suggestions include detailed reasoning and confidence scores. Operators can accept suggestions with a single click.
                </p>
              </div>
            )}

            {topic === 'recordings' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  Recording Lifecycle States
                </h3>
                <div className="grid gap-2 sm:grid-cols-2">
                  <div className="rounded-lg border border-slate-100 p-2.5 dark:border-slate-800">
                    <span className="font-bold text-slate-900 dark:text-slate-100">Waiting for class end:</span>
                    <p className="text-slate-500 mt-0.5">Session is still active.</p>
                  </div>
                  <div className="rounded-lg border border-slate-100 p-2.5 dark:border-slate-800">
                    <span className="font-bold text-slate-900 dark:text-slate-100">Zoom link attached:</span>
                    <p className="text-slate-500 mt-0.5">Cloud recording found and linked to portal.</p>
                  </div>
                  <div className="rounded-lg border border-slate-100 p-2.5 dark:border-slate-800">
                    <span className="font-bold text-slate-900 dark:text-slate-100">Drive found:</span>
                    <p className="text-slate-500 mt-0.5">Google Drive video link detected in sheet.</p>
                  </div>
                  <div className="rounded-lg border border-slate-100 p-2.5 dark:border-slate-800">
                    <span className="font-bold text-slate-900 dark:text-slate-100">Done:</span>
                    <p className="text-slate-500 mt-0.5">Permanent Google Drive link attached on LMS.</p>
                  </div>
                </div>
              </div>
            )}

            {topic === 'troubleshooting' && (
              <div className="space-y-4 text-xs leading-relaxed text-slate-700 dark:text-slate-300">
                <h3 className="text-base font-bold text-slate-900 dark:text-slate-100">
                  Troubleshooting & Common Fixes
                </h3>
                <div className="space-y-3">
                  <div className="rounded-xl border border-rose-200 bg-rose-50/50 p-3 dark:border-rose-900/60 dark:bg-rose-950/30">
                    <h4 className="font-bold text-rose-900 dark:text-rose-200">Worker Offline / No Cloud Worker</h4>
                    <p className="mt-1 text-rose-800 dark:text-rose-300">
                      <strong>Fix:</strong> Ensure the Docker container on your VPS is running (<code>docker ps</code>) or start the Windows Agent.
                    </p>
                  </div>
                  <div className="rounded-xl border border-amber-200 bg-amber-50/50 p-3 dark:border-amber-900/60 dark:bg-amber-950/30">
                    <h4 className="font-bold text-amber-900 dark:text-amber-200">Missing Zoom Meeting URL</h4>
                    <p className="mt-1 text-amber-800 dark:text-amber-300">
                      <strong>Fix:</strong> In Schedule or Class Details, enter the Zoom meeting URL. You can apply it to the whole group at once.
                    </p>
                  </div>
                  <div className="rounded-xl border border-amber-200 bg-amber-50/50 p-3 dark:border-amber-900/60 dark:bg-amber-950/30">
                    <h4 className="font-bold text-amber-900 dark:text-amber-200">Stage Failed (e.g. LMS Portal Busy)</h4>
                    <p className="mt-1 text-amber-800 dark:text-amber-300">
                      <strong>Fix:</strong> Open the Class Detail page and click "Retry Step". ClassFlow will immediately re-attempt the task.
                    </p>
                  </div>
                </div>
              </div>
            )}
          </Card>
        </div>
      </div>
    </div>
  )
}
