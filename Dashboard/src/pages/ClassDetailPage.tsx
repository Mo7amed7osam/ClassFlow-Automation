import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { useClassDetails, useRunStage, useSessions } from '../api/hooks'
import { PageHeader } from '../components/Layout'
import { LifecycleTimeline } from '../components/LifecycleTimeline'
import { useToast } from '../components/Toast'
import {
  IconActivity,
  IconAttendance,
  IconCalendar,
  IconClock,
  IconExternalLink,
  IconFilm,
  IconLMS,
  IconOverview,
  IconPlay,
  IconRefresh,
  IconUsers,
  IconVideo,
} from '../components/Icons'
import { Card, EmptyState, Pill, Spinner, StatCard, TimeAgo } from '../components/ui'
import { formatHumanActivity, translateOccurrenceState } from '../lib/translations'

type DetailTab = 'overview' | 'attendance' | 'participants' | 'lms' | 'recording' | 'activity'

export function ClassDetailPage() {
  const { id = '' } = useParams()
  const toast = useToast()
  const [activeTab, setActiveTab] = useState<DetailTab>('overview')
  const [retryingStage, setRetryingStage] = useState<string | null>(null)

  const { data: details, isLoading, refetch, isFetching } = useClassDetails(id)
  const runStage = useRunStage()

  // Fallback to sessions list if class details endpoint fails or is in fallback mode
  const { data: sessionsData } = useSessions()
  const fallbackClass = sessionsData?.classes.find((c) => c.classPlanId === id)

  const classItem = details?.class || fallbackClass

  if (isLoading && !classItem) {
    return (
      <div className="py-20 text-center">
        <Spinner label="Loading class occurrence details…" />
      </div>
    )
  }

  if (!classItem) {
    return (
      <div className="py-12">
        <PageHeader title="Class Not Found" />
        <Card>
          <div className="py-12 text-center">
            <p className="text-sm text-slate-500">Could not locate class with ID: {id}</p>
            <Link to="/schedules" className="mt-4 inline-block font-semibold text-indigo-600 hover:underline">
              ← Return to Schedule
            </Link>
          </div>
        </Card>
      </div>
    )
  }

  const occurrence = details?.occurrence
  const attendance = details?.attendance
  const recording = details?.recording
  const activity = details?.activity || []

  const occState = occurrence ? translateOccurrenceState(occurrence.state) : null

  const handleRunStage = async (stageKey: string) => {
    const stageJobMap: Record<string, string> = {
      zoom: 'class.run',
      run: 'lms.run_session',
      attendance: 'lms.attendance',
      lateJoiners: 'lms.late_joiners',
      complete: 'lms.complete',
      zoomReport: 'zoom.report',
      zoomRecording: 'zoom.recording',
      drive: 'recording.process',
    }
    const jobType = stageJobMap[stageKey] || stageKey
    try {
      setRetryingStage(stageKey)
      await runStage.mutateAsync({ planId: id, stage: jobType })
      toast.success('Stage queued', `Enqueued ${stageKey} execution.`)
      refetch()
    } catch (e: any) {
      toast.error('Trigger failed', e?.message || 'Server rejected stage trigger.')
    } finally {
      setRetryingStage(null)
    }
  }

  return (
    <div className="space-y-6 pb-12">
      {/* Top Breadcrumb */}
      <div className="flex items-center gap-2 text-xs font-medium text-slate-500">
        <Link to="/" className="hover:text-slate-800 dark:hover:text-slate-200">
          Overview
        </Link>
        <span>/</span>
        <Link to="/schedules" className="hover:text-slate-800 dark:hover:text-slate-200">
          Schedule
        </Link>
        <span>/</span>
        <span className="text-slate-900 dark:text-slate-100 font-semibold">{classItem.group}</span>
      </div>

      {/* Header Banner */}
      <div className="rounded-2xl border border-slate-200/80 bg-white p-6 shadow-xs dark:border-slate-800/80 dark:bg-[#111726] dark:shadow-none ring-1 ring-slate-900/5 dark:ring-white/[0.03]">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <div className="flex items-center gap-2.5 flex-wrap">
              <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-slate-100">
                {classItem.group}
              </h1>
              {occState ? (
                <Pill tone={occState.tone}>{occState.label}</Pill>
              ) : (
                <Pill tone={classItem.headline === 'running' ? 'green' : classItem.headline === 'needsAttention' ? 'red' : 'slate'}>
                  {classItem.headline}
                </Pill>
              )}
            </div>

            <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
              {classItem.title || 'Live Interactive Class Session'}
            </p>

            <div className="mt-3 flex flex-wrap items-center gap-4 text-xs text-slate-600 dark:text-slate-300">
              <span className="flex items-center gap-1.5 font-medium">
                <IconCalendar className="size-4 text-indigo-500" />
                {classItem.date}
              </span>
              <span>·</span>
              <span className="flex items-center gap-1.5 font-medium">
                <IconClock className="size-4 text-indigo-500" />
                {classItem.startTime ?? 'Time not set'}
              </span>
              <span>·</span>
              <span className="flex items-center gap-1.5">
                <IconVideo className="size-4 text-slate-400" />
                Coordinator: {classItem.coordinator.name || 'Admin'}
              </span>
            </div>
          </div>

          {/* Quick Actions */}
          <div className="flex items-center gap-2 flex-wrap">
            <button
              type="button"
              onClick={() => refetch()}
              disabled={isFetching}
              className="flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-800 dark:bg-[#161d2f] dark:text-slate-200 dark:hover:bg-[#1e273e] disabled:opacity-50 transition-colors"
            >
              <IconRefresh className={`size-3.5 ${isFetching ? 'animate-spin' : ''}`} />
              Refresh
            </button>

            {classItem.meetingUrl && (
              <a
                href={classItem.meetingUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-slate-800 dark:bg-[#161d2f] dark:text-slate-200 dark:hover:bg-[#1e273e] shadow-xs transition-colors"
              >
                Open Zoom Meeting
                <IconExternalLink className="size-3.5 text-slate-400" />
              </a>
            )}

            <button
              type="button"
              onClick={() => handleRunStage('run')}
              disabled={retryingStage === 'run'}
              className="flex items-center gap-1.5 rounded-xl bg-indigo-600 px-3.5 py-2 text-xs font-semibold text-white shadow-xs hover:bg-indigo-500 active:bg-indigo-700 disabled:opacity-50 transition-colors"
            >
              <IconPlay className="size-3.5" />
              Trigger Next Step
            </button>
          </div>
        </div>
      </div>

      {/* Main 8-stage Visual Lifecycle Timeline */}
      <LifecycleTimeline
        stages={classItem.stages}
        onRetryStage={handleRunStage}
        retryingKey={retryingStage}
      />

      {/* Navigation Tabs */}
      <div className="border-b border-slate-200 dark:border-slate-800">
        <nav className="flex gap-2 overflow-x-auto" aria-label="Class Details Tabs">
          {[
            { key: 'overview', label: 'Overview', icon: IconOverview },
            { key: 'attendance', label: `Attendance ${attendance ? `(${attendance.present}/${attendance.students})` : ''}`, icon: IconAttendance },
            { key: 'participants', label: 'Participants', icon: IconUsers },
            { key: 'lms', label: 'LMS Session', icon: IconLMS },
            { key: 'recording', label: 'Recording', icon: IconFilm },
            { key: 'activity', label: `Activity (${activity.length})`, icon: IconActivity },
          ].map((tab) => {
            const Icon = tab.icon
            const active = activeTab === tab.key
            return (
              <button
                key={tab.key}
                type="button"
                onClick={() => setActiveTab(tab.key as DetailTab)}
                className={`flex items-center gap-2 border-b-2 px-4 py-3 text-xs font-bold whitespace-nowrap transition-colors ${
                  active
                    ? 'border-indigo-600 text-indigo-600 dark:border-indigo-400 dark:text-indigo-400'
                    : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-300'
                }`}
              >
                <Icon className="size-4 shrink-0" />
                <span>{tab.label}</span>
              </button>
            )
          })}
        </nav>
      </div>

      {/* Tab Panels */}
      {activeTab === 'overview' && (
        <div className="space-y-6">
          {/* Key Stat Cards */}
          <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            <StatCard
              label="Attendance Status"
              value={attendance ? `${attendance.present} Present` : 'Pending'}
              hint={attendance ? `${attendance.needsReview} needs review · ${attendance.absent} absent` : 'Awaiting first Zoom snapshot'}
              tone={attendance && attendance.present > 0 ? 'green' : 'slate'}
            />
            <StatCard
              label="Meeting Admittance"
              value={classItem.stages.find((s) => s.key === 'zoom')?.detail || 'Scheduled'}
              hint="Worker browser automation"
              tone={classItem.stages.find((s) => s.key === 'zoom')?.state === 'done' ? 'green' : 'blue'}
            />
            <StatCard
              label="LMS Workflow"
              value={classItem.stages.find((s) => s.key === 'run')?.detail || 'Scheduled'}
              hint="Portal synchronization"
              tone={classItem.stages.find((s) => s.key === 'run')?.state === 'done' ? 'green' : 'slate'}
            />
            <StatCard
              label="Recording Archive"
              value={recording?.driveLink ? 'Drive attached' : recording?.zoomLink ? 'Zoom link found' : 'Waiting for end'}
              hint="Permanent Google Drive storage"
              tone={recording?.driveLink ? 'green' : 'slate'}
            />
          </div>

          {/* Details Grid */}
          <div className="grid gap-6 md:grid-cols-2">
            <Card title="Session Configuration">
              <dl className="divide-y divide-slate-100 text-xs dark:divide-slate-800">
                <div className="flex justify-between py-2.5">
                  <dt className="text-slate-500">Group Code</dt>
                  <dd className="font-semibold text-slate-900 dark:text-slate-100">{classItem.group}</dd>
                </div>
                <div className="flex justify-between py-2.5">
                  <dt className="text-slate-500">Scheduled Date</dt>
                  <dd className="font-medium text-slate-800 dark:text-slate-200">{classItem.date}</dd>
                </div>
                <div className="flex justify-between py-2.5">
                  <dt className="text-slate-500">Start Time (Cairo)</dt>
                  <dd className="font-medium text-slate-800 dark:text-slate-200">{classItem.startTime ?? '—'}</dd>
                </div>
                <div className="flex justify-between py-2.5">
                  <dt className="text-slate-500">Coordinator</dt>
                  <dd className="font-medium text-slate-800 dark:text-slate-200">{classItem.coordinator.name || 'Admin'}</dd>
                </div>
                <div className="flex justify-between py-2.5">
                  <dt className="text-slate-500">Zoom Meeting Link</dt>
                  <dd className="max-w-[200px] truncate text-indigo-600 dark:text-indigo-400">
                    {classItem.meetingUrl ? (
                      <a href={classItem.meetingUrl} target="_blank" rel="noopener noreferrer" className="hover:underline">
                        {classItem.meetingUrl}
                      </a>
                    ) : (
                      'None'
                    )}
                  </dd>
                </div>
              </dl>
            </Card>

            <Card title="Automated Stage Breakdown">
              <div className="space-y-2">
                {classItem.stages.map((st) => (
                  <div key={st.key} className="flex items-center justify-between rounded-xl border border-slate-200/80 bg-slate-50/50 p-2.5 dark:border-slate-800/80 dark:bg-[#0c111d]">
                    <div>
                      <p className="text-xs font-semibold text-slate-900 dark:text-slate-100">{st.label}</p>
                      <p className="text-[11px] text-slate-500 dark:text-slate-400">{st.caption} {st.detail ? `· ${st.detail}` : ''}</p>
                    </div>
                    <div className="flex items-center gap-2">
                      <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-bold ${
                        st.state === 'done'
                          ? 'bg-emerald-500/10 text-emerald-600 ring-1 ring-emerald-500/20 dark:bg-emerald-500/15 dark:text-emerald-400'
                          : st.state === 'running'
                          ? 'bg-sky-500/10 text-sky-600 ring-1 ring-sky-500/20 dark:bg-sky-500/15 dark:text-sky-400'
                          : st.state === 'failed'
                          ? 'bg-rose-500/10 text-rose-600 ring-1 ring-rose-500/20 dark:bg-rose-500/15 dark:text-rose-400'
                          : st.state === 'blocked'
                          ? 'bg-amber-500/10 text-amber-600 ring-1 ring-amber-500/20 dark:bg-amber-500/15 dark:text-amber-400'
                          : 'bg-slate-500/10 text-slate-600 dark:bg-[#161d2f] dark:text-slate-400'
                      }`}>
                        {st.state}
                      </span>
                      {st.state === 'failed' && (
                        <button
                          type="button"
                          onClick={() => handleRunStage(st.key)}
                          className="rounded bg-rose-600 px-2 py-0.5 text-[10px] font-semibold text-white hover:bg-rose-700 transition-colors"
                        >
                          Retry
                        </button>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </Card>
          </div>
        </div>
      )}

      {activeTab === 'attendance' && (
        <Card title="Attendance Review">
          {attendance ? (
            <div className="space-y-4">
              <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                <StatCard label="Total Students" value={attendance.students} />
                <StatCard label="Present" value={attendance.present} tone="green" />
                <StatCard label="Needs Review" value={attendance.needsReview} tone={attendance.needsReview ? 'amber' : 'slate'} />
                <StatCard label="Absent" value={attendance.absent} />
              </div>
              <div className="flex justify-end gap-2 pt-4">
                <Link
                  to={`/attendance/${attendance.id}`}
                  className="rounded-xl bg-indigo-600 px-4 py-2 text-xs font-semibold text-white hover:bg-indigo-700"
                >
                  Open Full Attendance Workspace →
                </Link>
              </div>
            </div>
          ) : (
            <EmptyState>
              No attendance session has been recorded yet for this class occurrence. Attendance reads begin 15 minutes before scheduled start when Zoom admits students.
            </EmptyState>
          )}
        </Card>
      )}

      {activeTab === 'participants' && (
        <Card title="Observed Zoom Participants">
          <EmptyState>
            Participant snapshots are recorded periodically by cloud workers during the meeting. Each sighting records first observed, last observed, and total observation count without interpolating unobserved intervals.
            {attendance?.id && (
              <div className="mt-3">
                <Link
                  to={`/attendance/${attendance.id}?tab=names`}
                  className="font-semibold text-indigo-600 hover:underline dark:text-indigo-400"
                >
                  View All Observed Zoom Names ({attendance.students || 0} roster students) →
                </Link>
              </div>
            )}
          </EmptyState>
        </Card>
      )}

      {activeTab === 'lms' && (
        <Card title="LMS Integration Lifecycle">
          <div className="space-y-4 text-xs">
            <p className="text-slate-600 dark:text-slate-300">
              ClassFlow signs into the portal autonomously using encrypted credentials, triggers Run Session, posts attendance snapshots, executes late joiner corrections, and completes the session.
            </p>
            <div className="rounded-xl border border-slate-200/80 bg-slate-50/50 p-4 dark:border-slate-800/80 dark:bg-[#0c111d]">
              <h4 className="font-semibold text-slate-900 dark:text-slate-100">Portal Actions Executed</h4>
              <ul className="mt-2 space-y-2">
                <li className="flex items-center gap-2">
                  <span className="size-2 rounded-full bg-emerald-500" />
                  <span>Run Session — Triggered at class start</span>
                </li>
                <li className="flex items-center gap-2">
                  <span className="size-2 rounded-full bg-slate-400" />
                  <span>LMS Attendance — 90-minute attendance upload</span>
                </li>
                <li className="flex items-center gap-2">
                  <span className="size-2 rounded-full bg-slate-400" />
                  <span>Late Joiners Correction — 180-minute late attendee resolution</span>
                </li>
                <li className="flex items-center gap-2">
                  <span className="size-2 rounded-full bg-slate-400" />
                  <span>Complete Session — Portal completion status</span>
                </li>
              </ul>
            </div>
          </div>
        </Card>
      )}

      {activeTab === 'recording' && (
        <Card title="Class Recording Pipeline">
          <div className="space-y-4 text-xs">
            <div className="grid gap-4 sm:grid-cols-2">
              <div className="rounded-xl border border-slate-200/80 bg-slate-50/50 p-4 dark:border-slate-800/80 dark:bg-[#0c111d]">
                <h4 className="font-semibold text-slate-900 dark:text-slate-100">Zoom Cloud Recording</h4>
                <p className="mt-1 text-slate-500 dark:text-slate-400">Captured automatically upon meeting termination.</p>
                {recording?.zoomLink ? (
                  <a href={recording.zoomLink} target="_blank" rel="noopener noreferrer" className="mt-2 inline-block font-semibold text-indigo-600 hover:underline dark:text-indigo-400">
                    View Zoom Recording Link →
                  </a>
                ) : (
                  <span className="mt-2 inline-block text-slate-400">Not available yet</span>
                )}
              </div>

              <div className="rounded-xl border border-slate-200/80 bg-slate-50/50 p-4 dark:border-slate-800/80 dark:bg-[#0c111d]">
                <h4 className="font-semibold text-slate-900 dark:text-slate-100">Google Drive Permanent Link</h4>
                <p className="mt-1 text-slate-500 dark:text-slate-400">Synchronized via automated Google Sheets reader.</p>
                {recording?.driveLink ? (
                  <a href={recording.driveLink} target="_blank" rel="noopener noreferrer" className="mt-2 inline-block font-semibold text-emerald-600 hover:underline dark:text-emerald-400">
                    View Google Drive Link →
                  </a>
                ) : (
                  <span className="mt-2 inline-block text-slate-400">Waiting for 08:00 Cairo Google sync</span>
                )}
              </div>
            </div>
          </div>
        </Card>
      )}

      {activeTab === 'activity' && (
        <Card title="Occurrence Execution Logs">
          {activity.length === 0 ? (
            <EmptyState>No activity logs recorded yet for this class occurrence.</EmptyState>
          ) : (
            <div className="divide-y divide-slate-100 text-xs dark:divide-slate-800">
              {activity.map((act) => (
                <div key={act.id} className="flex items-start justify-between py-3">
                  <div>
                    <p className="font-semibold text-slate-900 dark:text-slate-100">
                      {formatHumanActivity(act.kind, act.summary, act.detail, act.outcome)}
                    </p>
                    <p className="text-[11px] text-slate-500 font-mono mt-0.5">{act.kind}</p>
                  </div>
                  <span className="text-[11px] text-slate-400">
                    <TimeAgo iso={act.at} />
                  </span>
                </div>
              ))}
            </div>
          )}
        </Card>
      )}
    </div>
  )
}
