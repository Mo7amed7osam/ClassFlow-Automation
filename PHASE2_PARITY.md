# Phase 2 cloud parity audit

Audit date: 2026-09-26. Repository: branch `linux`, local and `origin/linux` at
`8f152b1` before the audit fixes in this working tree. Production was read from the
public health endpoint, the authenticated dashboard already open in Chrome, and the
Coolify deployment history. Do not treat a code path or a green build as proof of a
successful live Zoom/LMS run.

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
| Local commit | `8f152b1a0e2b8620072ec282fb9c64c3b46d7726` | Same as origin before this audit's changes |
| `origin/linux` | `8f152b1a0e2b8620072ec282fb9c64c3b46d7726` | No ahead/behind commits |
| Deployed backend | Coolify latest successful deployment `3a4fbad7a4c91f23b0103ee4f0fbfb91a3d81e13` | Behind desired `linux` head |
| Backend liveness | `GET /health` returned HTTP 200, `{"status":"ok"}` | PASS for process + database liveness only |
| Detailed backend / worker health | Anonymous `GET /api/v1/dashboard/health-detailed` returns 401 | Authenticated details not retrieved through direct request |
| Worker signal | Authenticated Overview reported 2 of 3 agents online; 0 busy | Warning; does not prove Zoom readiness |
| Current class signal | Overview listed today's G1 and G2 as “Zoom failed / Needs somebody” | Live automation is not ready |
| Production dashboard | `/dashboard/` responds 200; assets are served | Older menu and copy are live; requested redesign is not deployed |
| Current recordings / attendance | Overview showed no recordings; Attendance showed no sessions | No successful live class evidence in those views |

Production URL: <https://classflow.152.239.115.207.sslip.io/dashboard/>

Coolify confirms source branch `linux`, source SHA `HEAD`, and latest successful run
`3a4fbad`. The service is serving that older frontend despite local `8f152b1`. A
deployment was not started during this audit because the startup seeder below was found
to modify operator-owned fields and required a source fix before redeployment.

## Parity audit

| # | Feature | Status | Implementation and state | Persistence / retry | Production evidence / remaining gap |
|---:|---|---|---|---|---|
| 1 | Scheduler and six weekly slots | PARTIAL | `Backend/central_backend/scheduling.py`; Cairo stage schedule; production seeder makes plans | `class_plans`, `class_occurrences`, idempotent stage keys; retry queue | Overview showed today's G1 and G2 Zoom jobs failed. No successful scheduled class evidence. |
| 2 | ClassOccurrence | IMPLEMENTED_UNVERIFIED | `scheduling.py`, `models.py`, `occurrence_lifecycle.py` | PostgreSQL row linked to jobs; outcome projection | Public health confirms DB liveness, not actual occurrence lifecycle/restart recovery. |
| 3 | Zoom Web profiles | BLOCKED | `CloudWorker` profile managers and encrypted Zoom account records | Worker state volume stores per-account browser profiles | No authenticated proof that the two required profiles exist, have cookies, or are signed in. Today's Zoom jobs failed. |
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
| 33 | Notifications | PARTIAL | `notifications.py` and dashboard notification APIs | Stored notification/audit state depends on event path | Production redesign with notifications is not deployed; delivery channel/configuration not proven. |
| 34 | Pre-flight | PARTIAL | `CloudWorker/Preflight.cs` checks Chromium/backend/time/storage at worker startup | Startup logs only | No current pre-flight report retrieved; 2/3 online and today's Zoom failures are blockers. |
| 35 | Secrets and migrations | PARTIAL | AES-GCM secret store, environment configuration, Alembic entrypoint | Encrypted account credentials and PostgreSQL schema | `/health` indicates DB responds. Secret presence/validity, current migration revision, OAuth and account login remain unverified. Never expose secret values. |
| 36 | Dashboard redesign and route wiring | PARTIAL | React/Vite `Dashboard/src`; current desired redesign is in `8f152b1` | Built into backend image | Production still serves the older `3a4fbad` bundle. Old Groups copy mentions n8n; redesigned Overview/Live/Class Detail/Notifications/Help/health modal were not verified live. |

## Seeder safety

`Backend/central_backend/seed.py` is called at every backend start by
`deploy/backend-entrypoint.sh`. It creates absent starter data and is intended to be
idempotent. The audit found it also reset existing students' active flag, order, and
aliases and re-enabled an existing `RunDelegation`. That would overwrite operator edits on
every deployment. The current working-tree fix leaves existing student fields and
delegation choices alone; it creates starter alias records only alongside newly created
students. Existing group,
Zoom account, LMS account, and class-plan rows are not replaced. Verify this fix is on the
deployed source before redeploying.

## Lifecycle and business-order gaps

The scheduler currently creates `lms.attendance` at +90m, `lms.late_joiners` at +180m,
`lms.complete` at +190m, Zoom report at +200m, and Zoom recording at +210m. This means
the LMS completion is before Zoom report/recording, which is acceptable as a separate
later activity. However, no automatic end snapshot, deterministic final reconciliation,
OpenRouter call, and separate final correction currently run before `lms.complete`. The
desired attendance finalization order is therefore not met. The Zoom close safety rule
remains separate from LMS completion; do not alter it without live room evidence.

## Readiness

**NOT READY. Do not turn the Mac off yet.** The latest intended dashboard/backend commit
is not deployed, today's production Overview reports Zoom failures, worker connectivity is
2/3, and Zoom profile login, LMS login, Google OAuth/Sheet access, and OpenRouter live
request were not verified. There is no complete live class run in the available evidence.
