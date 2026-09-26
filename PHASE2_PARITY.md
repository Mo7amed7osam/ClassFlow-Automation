# Phase 2 cloud parity audit

Audit date: 2026-09-26. Repository: branch `linux`, local and `origin/linux` at
`28906044da172956b5fafd6654853a62c8a95d6b` (audit documentation update). Production was checked after the two
successful Coolify deployments using the public health endpoint and authenticated
dashboard. Do not treat a code path or a green build as proof of a successful live
Zoom/LMS run.

## Status meanings

- **PASS**: verified end to end in the live production service.
- **IMPLEMENTED_UNVERIFIED**: implementation and local tests exist; live behavior has
  not been proved.
- **PARTIAL**: only part of the required behavior is implemented, or live evidence is
  incomplete.
- **MISSING**: there is no implementation for the required behavior.
- **BLOCKED**: production evidence requires an unverified external account, human step,
  or safe real class.

## Production snapshot

| Item | Evidence | Result |
|---|---|---|
| Local commit / `origin/linux` | `28906044da172956b5fafd6654853a62c8a95d6b` | Matches; documentation-only commit after application deployment |
| Deployed backend | Coolify latest success is `040faf3` | Matches current source; redesign and timezone fix are live |
| Backend liveness | `GET /health` returned HTTP 200, `{"status":"ok"}` | PASS for process/database liveness only |
| Detailed backend / worker health | Authenticated health modal loaded; Backend and PostgreSQL responsive, server time reported in Africa/Cairo | PASS for those checks; not a live class proof |
| Worker signal | Health modal reported 2 online / 3 registered; Agents page showed one agent offline for 5 days | Warning; dashboard's “2 Worker Active” badge does not mean all workers are healthy |
| Current class signal | G1 and G2 show “Zoom failed”; G2 details show Zoom Started and Run Session failed, no attendance snapshot | Live automation is not ready |
| Production dashboard | `/dashboard/` responds 200 and new dashboard routes/assets load; bare `/` responds 404 | Use `/dashboard/` as the application URL |
| Recordings / attendance | No recordings; G2 attendance pending with no first Zoom snapshot; 2 sessions need attention | No successful end-to-end class evidence |
| Integrations | 1 LMS account and 3 Zoom account records configured; Google OAuth and Sheet missing; OpenRouter key missing | Credentials/configuration counts do not verify successful login |

Production URL: <https://classflow.152.239.115.207.sslip.io/dashboard/>

Coolify confirms source branch `linux`, source SHA `HEAD`, and successful deployments
of `ba357ba` (seeder safety) and `040faf3` (dashboard timezone diagnostics). The live
diagnostics modal now renders the formatted Cairo time rather than returning the 500
seen before the timezone fix. The source commits are pushed and deployed.

## Parity audit

| # | Feature | Status | Implementation and state | Persistence / retry | Production evidence / remaining gap |
|---:|---|---|---|---|---|
| 1 | Scheduler and six weekly slots | PARTIAL | `Backend/central_backend/scheduling.py`; Cairo stage schedule; production seeder makes plans | `class_plans`, `class_occurrences`, idempotent stage keys; retry queue | Overview showed today's G1 and G2 Zoom jobs failed. No successful scheduled class evidence. |
| 2 | ClassOccurrence | IMPLEMENTED_UNVERIFIED | `scheduling.py`, `models.py`, `occurrence_lifecycle.py` | PostgreSQL row linked to jobs; outcome projection | Public health confirms DB liveness, not actual occurrence lifecycle/restart recovery. |
| 3 | Zoom Web profiles | BLOCKED | `CloudWorker` profile managers and encrypted Zoom account records | Worker state volume stores per-account browser profiles | Health reports 3 configured Zoom account records, but cookie/session readiness is not verified. Today's G1/G2 Zoom jobs failed. |
| 4 | Zoom account isolation | IMPLEMENTED_UNVERIFIED | Account-specific profile path and operation lock in CloudWorker | Persistent worker volume | No live proof that G1/G2 were opened with the correct separate accounts. |
| 5 | Zoom meeting start | BLOCKED | `Stages/ClassRunStage.cs`, `WebAutoAdmitEngine` | Job state and browser profile | Production Overview reports Zoom failures; no successful live start. |
| 6 | Mic/camera state | IMPLEMENTED_UNVERIFIED | `WebAutoAdmitEngine` DOM controls | Browser state during meeting only | No live Zoom Web evidence. |
| 7 | Waiting Room | IMPLEMENTED_UNVERIFIED | Zoom Web DOM parser in `Windows/src/ZoomAutoAdmit.WebAutomation` | In-memory browser DOM, polled during meeting | No live Linux meeting verification. |
| 8 | Auto Admit | IMPLEMENTED_UNVERIFIED | `WebAutoAdmitEngine` and admission verifier | Browser polling and bounded click retry | No live waiting-room arrival/admit evidence. |
| 9 | Participant capture | IMPLEMENTED_UNVERIFIED | Joined-list / participant DOM readers; `MeetingAttendance.cs` | Posted snapshot evidence is durable in PostgreSQL | Attendance view showed no sessions; capture and API delivery unverified in production. |
| 10 | Participant watchdog | IMPLEMENTED_UNVERIFIED | Zoom toolbar wake and participant-panel reopening in WebAutomation | Current browser session | No live headless Linux evidence. |
| 11 | Attendance snapshots | PARTIAL | `MeetingAttendance.cs` uses a 15-minute cadence and explicit snapshot completeness | `attendance_snapshots`, idempotent snapshot IDs | No production attendance sessions observed. A failed/incomplete participant read must remain distinct from an empty class. |
| 12 | Ignore lists | PARTIAL | `attendance.py` has global staff/name handling plus G1/G2 special cases | Ignored participant flag persists with attendance | Re-audit the configured names against production records. Hard-coded group ignores are present; correctness not verified. |
| 13 | Name matching | IMPLEMENTED_UNVERIFIED | `attendance_matching.py`, `attendance_names.py` | Deterministic roster/alias matching | Algorithm tests exist; no production class snapshot available to validate. |
| 14 | Alias persistence | IMPLEMENTED_UNVERIFIED | `attendance.py`, `models.StudentAlias` | Accepted/rejected aliases persist in PostgreSQL | UI showed seeded alias text for G1; full G1/G2 alias state and correctness not audited. |
| 15 | OpenRouter integration | PARTIAL | `attendance_ai.py`, configured from `CENTRAL_AI_API_KEY` | Request is stateless; applied alias records persist | Only requested by the manual attendance `/match` endpoint (`useAi=true`). No automatic end-of-class invocation. Key presence and a safe live call remain unverified. |
| 16 | Automatic AI before finalization | MISSING | No scheduler/worker pipeline calls `apply_ai` at the class end | None | `/match` is user-triggered; required automatic final AI reconciliation is absent. |
| 17 | Attendance finalization | PARTIAL | Attendance finalize endpoint; successful `lms.late_joiners` outcome also finalizes the session | PostgreSQL status | No final snapshot → deterministic reconciliation → AI → final LMS correction pipeline. |
| 18 | Co-host automation | IMPLEMENTED_UNVERIFIED | `WebCoHostAssigner` in CloudWorker/WebAutomation | Assigned names live within the meeting | No live Zoom Web co-host validation. |
| 19 | LMS login | BLOCKED | `LmsSessionRunner` and encrypted `LmsAccount` credentials | Encrypted DB credentials, worker account profile | Credential presence and authenticated login on VPS not proven. Do not report LMS-ready. |
| 20 | LMS Run Session | BLOCKED | `Stages/LmsStages.cs` `RunSessionStage` | Idempotent class job | No successful production session execution observed. |
| 21 | +90 minute attendance | IMPLEMENTED_UNVERIFIED | `AttendanceStage` writes the current present roster | Durable job outcome and retry metadata | No live LMS submission/readback evidence. |
| 22 | +3 hour correction | IMPLEMENTED_UNVERIFIED | `LateJoinersStage` re-reads attendance and corrects changed rows | Durable job outcome and retry metadata | No live LMS submission/readback evidence. This stage currently triggers attendance finalization. |
| 23 | Separate final correction | MISSING | No correction job after final snapshot and AI | None | The current +3h correction precedes the +190m completion job and does not consume a final AI result. |
| 24 | Complete Session | IMPLEMENTED_UNVERIFIED | `CompleteSessionStage` at +190m | Durable job outcome | No successful production completion/readback evidence. Do not equate completion with cancellation. |
| 25 | Zoom recording discovery | IMPLEMENTED_UNVERIFIED | `ZoomReportStages.cs`; reads Zoom Web, not Zoom API | Durable recording job; matched link stored on occurrence | No recordings showed in production Overview; no live discovery evidence. |
| 26 | `recording.process` | IMPLEMENTED_UNVERIFIED | `RecordingProcessStage.cs`, `LmsSessionRunner.AttachRecordLinkAsync` | Linked occurrence/job and retryable result | No live LMS readback of a Zoom or Drive link. |
| 27 | Google Sheets read-only sync | PARTIAL | `google_sheets.py`, Sheets API read-only scope; 08:00 Cairo loop | Encrypted refresh token + row ledger | Production OAuth linkage and live Sheet read not verified. No sheet write endpoint is used. |
| 28 | Drive link replacement | IMPLEMENTED_UNVERIFIED | `google_sheets.py` links rows by Group + Date; `recording.process` replaces Zoom link and verifies via reload | Sync row, occurrence and job state; conflict on ambiguity/unrelated link | Code path exists; live Sheet → LMS replacement/readback unverified. |
| 29 | Queue and retries | IMPLEMENTED_UNVERIFIED | `jobs.py`, `dispatch.py`; exponential retry and orphan sweep | PostgreSQL `jobs` | Overview showed 3 failed jobs in 24h; individual causes/retry eligibility not available in audit view. |
| 30 | Browser recovery | IMPLEMENTED_UNVERIFIED | Browser lifecycle scoped by job/profile; failed jobs can be retried | Profile and job state on volume/Postgres | No production browser restart/recovery test. |
| 31 | Worker recovery | PARTIAL | Worker reconnects and registers; persistent `/var/lib/classflow` volume | Worker token, profiles and journals | Overview reports 2/3 agents online; no safe restart recovery evidence during class. |
| 32 | VPS restart recovery | IMPLEMENTED_UNVERIFIED | Compose restart policies, named PostgreSQL/worker volumes, entrypoint migrations | Persistent named volumes | Health is live, but no controlled VPS restart proof from this audit. |
| 33 | Notifications | PARTIAL | `notifications.py` and dashboard notification APIs | Stored notification/audit state depends on event path | Notifications Center loads and reports four errors for today's classes; external delivery channel/configuration is not proven. |
| 34 | Pre-flight | PARTIAL | `CloudWorker/Preflight.cs` checks Chromium/backend/time/storage at worker startup | Startup logs only | No current pre-flight report retrieved; 2/3 online and today's Zoom failures are blockers. |
| 35 | Secrets and migrations | PARTIAL | AES-GCM secret store, environment configuration, Alembic entrypoint | Encrypted account credentials and PostgreSQL schema | `/health` indicates DB responds. Secret presence/validity, current migration revision, OAuth and account login remain unverified. Never expose secret values. |
| 36 | Dashboard redesign and route wiring | PARTIAL | React/Vite `Dashboard/src`; redesign is included in `8f152b1` and deployed in `040faf3` | Built into backend image | Authenticated Overview, Live, Schedule, Attendance, Groups, Zoom, LMS, Recordings, Automation/Agents, Notifications, Logs, Settings, Help, Users, Coordinators, Sessions, and class detail routes load. The Automation nav points to `/dashboard/agents`; Schedule handles class timing and class detail shows lifecycle stages. The overview's recent activity says G2 was admitted/held, but G2's actual class detail reports Zoom and Run Session failed and no attendance snapshot, so the audit treats it as failed. Responsive mobile/tablet layout and a working dark/light toggle were not verified. |

## Seeder safety

`Backend/central_backend/seed.py` is called at every backend start by
`deploy/backend-entrypoint.sh`. It creates absent starter data and is intended to be
idempotent. The audit found it also reset existing students' active flag, order, and
aliases and re-enabled an existing `RunDelegation`. Commit `ba357ba` leaves existing
student fields and delegation choices alone; it creates starter alias records only
alongside newly created students. The fix is in deployed commit `040faf3`, which includes
`ba357ba`. PostgreSQL-backed regression cases were added, but could not execute in the
local environment because PostgreSQL binaries were unavailable; pytest reported those
cases skipped. Thus the source-level safety fix is deployed, but its database scenario
was not exercised locally.

## Lifecycle and business-order gaps

The scheduler currently creates `lms.attendance` at +90m, `lms.late_joiners` at +180m,
`lms.complete` at +190m, Zoom report at +200m, and Zoom recording at +210m. This means
the LMS completion is before Zoom report/recording, which is acceptable as a separate
later activity. However, no automatic end snapshot, deterministic final reconciliation,
OpenRouter call, and separate final correction currently run before `lms.complete`. The
desired attendance finalization order is therefore not met. The Zoom close safety rule
remains separate from LMS completion; do not alter it without live room evidence.

## Readiness

**NOT READY. Do not turn the Mac off yet.** The latest source commit `040faf3` is pushed
and deployed, and `/health` is 200, but both today's production classes show Zoom failure;
G2's detail shows its first two lifecycle stages failed and attendance is still pending.
Only 2/3 workers are online (one offline for 5 days), there are no recordings, Google OAuth
and a spreadsheet are not configured, and OpenRouter reports rule-based matching only.
The dashboard stores Zoom/LMS account records, but the live Zoom and LMS login/submission
paths have not passed a full class run. Bare `/` returns 404; the application is at
`/dashboard/`. Turning off the Mac cannot be recommended as verified-safe based on this
production state.
